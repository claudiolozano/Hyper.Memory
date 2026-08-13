using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperMemory.Core;
using Microsoft.Data.Sqlite;

namespace HyperMemory.Infrastructure;

public sealed partial class SqliteMemoryStore
{
    private const int OperationalSchemaVersion = 5;

    private static async Task ApplyOperationalMigrationsAsync(
        SqliteConnection connection,
        bool enableProjectState,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                applied_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO schema_migrations(version, name, applied_at)
                VALUES(4, 'legacy-memory-baseline', $appliedAt);
            CREATE TABLE IF NOT EXISTS operational_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                event_type TEXT NOT NULL,
                subject_type TEXT NOT NULL,
                subject_id TEXT NOT NULL,
                scope_key TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                project_id TEXT NULL,
                session_id TEXT NULL,
                agent_id TEXT NULL,
                task_id TEXT NULL,
                revision INTEGER NOT NULL CHECK(revision > 0),
                data_json TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                causation_id TEXT NULL,
                correlation_id TEXT NULL,
                occurred_at TEXT NOT NULL,
                stored_at TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                UNIQUE(scope_key, subject_type, subject_id, revision)
            );
            CREATE INDEX IF NOT EXISTS ix_operational_events_scope
                ON operational_events(workspace_id, project_id, session_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_operational_events_subject
                ON operational_events(scope_key, subject_type, subject_id, revision);
            CREATE INDEX IF NOT EXISTS ix_operational_events_task
                ON operational_events(workspace_id, task_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_operational_events_correlation
                ON operational_events(correlation_id, sequence);
            INSERT OR IGNORE INTO schema_migrations(version, name, applied_at)
                VALUES(5, 'operational-event-journal', $appliedAt);
            UPDATE memory_schema SET value='5' WHERE key='version' AND CAST(value AS INTEGER) < 5;
            """;
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (!enableProjectState) return;

        await using var projectionTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var projectionCommand = connection.CreateCommand();
        projectionCommand.Transaction = projectionTransaction;
        projectionCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS project_state_projections (
                scope_key TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                project_id TEXT NULL,
                through_sequence INTEGER NOT NULL CHECK(through_sequence >= 0),
                snapshot_json TEXT NOT NULL,
                projector_version TEXT NOT NULL,
                projected_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_project_state_scope
                ON project_state_projections(workspace_id, project_id);
            INSERT OR IGNORE INTO schema_migrations(version, name, applied_at)
                VALUES(6, 'project-state-projection', $appliedAt);
            UPDATE memory_schema SET value='6' WHERE key='version' AND CAST(value AS INTEGER) < 6;
            """;
        projectionCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await projectionCommand.ExecuteNonQueryAsync(cancellationToken);
        await projectionTransaction.CommitAsync(cancellationToken);
    }

    public async Task<OperationalEventWriteResult> AppendAsync(
        OperationalEventWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureOperationalJournalEnabled();
        ValidateOperationalWrite(request);

        var eventId = string.IsNullOrWhiteSpace(request.EventId) ? Guid.NewGuid().ToString("N") : request.EventId.Trim();
        var eventType = request.EventType.Trim();
        var subject = new OperationalObjectRef(request.Subject.ObjectType.Trim(), request.Subject.ObjectId.Trim());
        var scope = NormalizeScope(request.Scope);
        var scopeKey = ComputeScopeKey(scope);
        var dataJson = OperationalDataSanitizer.RedactJson(NormalizeJson(request.DataJson));
        var sanitizedMetadata = OperationalDataSanitizer.RedactMetadata(request.Metadata);
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (sanitizedMetadata is not null)
            foreach (var item in sanitizedMetadata) metadata.Add(item.Key, item.Value);
        var metadataJson = JsonSerializer.Serialize(metadata);
        var explicitOccurredAt = request.OccurredAt?.ToUniversalTime().ToString("O");
        var immutable = new OperationalImmutableContent(eventType, subject, scope, dataJson,
            Clean(request.CausationId), Clean(request.CorrelationId), explicitOccurredAt, metadata);
        var immutableJson = JsonSerializer.Serialize(immutable);
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(immutableJson)));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: false);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT sequence, revision, content_hash FROM operational_events WHERE event_id=$eventId";
                existing.Parameters.AddWithValue("$eventId", eventId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!string.Equals(reader.GetString(2), contentHash, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Operational event id '{eventId}' already exists with different immutable content.");
                    return new OperationalEventWriteResult(eventId, reader.GetInt64(0), reader.GetInt64(1), false);
                }
            }

            long currentRevision;
            await using (var revision = connection.CreateCommand())
            {
                revision.Transaction = transaction;
                revision.CommandText = """
                    SELECT COALESCE(MAX(revision), 0)
                    FROM operational_events
                    WHERE scope_key=$scopeKey AND subject_type=$subjectType AND subject_id=$subjectId
                    """;
                revision.Parameters.AddWithValue("$scopeKey", scopeKey);
                revision.Parameters.AddWithValue("$subjectType", subject.ObjectType);
                revision.Parameters.AddWithValue("$subjectId", subject.ObjectId);
                currentRevision = Convert.ToInt64(await revision.ExecuteScalarAsync(cancellationToken));
            }

            if (request.ExpectedRevision is not null && request.ExpectedRevision.Value != currentRevision)
                throw new InvalidOperationException(
                    $"Operational revision conflict for '{subject.ObjectType}/{subject.ObjectId}': expected {request.ExpectedRevision}, current {currentRevision}.");

            var nextRevision = checked(currentRevision + 1);
            var occurredAt = explicitOccurredAt ?? DateTimeOffset.UtcNow.ToString("O");
            var storedAt = DateTimeOffset.UtcNow.ToString("O");
            var archive = new ArchivedOperationalEvent(eventId, nextRevision, eventType, subject, scope, dataJson,
                contentHash, Clean(request.CausationId), Clean(request.CorrelationId), occurredAt, storedAt, metadata);
            await PreserveOperationalEnvelopeAsync(archive, cancellationToken);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO operational_events(
                    event_id, event_type, subject_type, subject_id, scope_key, workspace_id,
                    project_id, session_id, agent_id, task_id, revision, data_json, content_hash,
                    causation_id, correlation_id, occurred_at, stored_at, metadata_json)
                VALUES(
                    $eventId, $eventType, $subjectType, $subjectId, $scopeKey, $workspaceId,
                    $projectId, $sessionId, $agentId, $taskId, $revision, $dataJson, $contentHash,
                    $causationId, $correlationId, $occurredAt, $storedAt, $metadataJson);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$eventId", eventId);
            insert.Parameters.AddWithValue("$eventType", eventType);
            insert.Parameters.AddWithValue("$subjectType", subject.ObjectType);
            insert.Parameters.AddWithValue("$subjectId", subject.ObjectId);
            insert.Parameters.AddWithValue("$scopeKey", scopeKey);
            insert.Parameters.AddWithValue("$workspaceId", scope.WorkspaceId);
            insert.Parameters.AddWithValue("$projectId", Db(scope.ProjectId));
            insert.Parameters.AddWithValue("$sessionId", Db(scope.SessionId));
            insert.Parameters.AddWithValue("$agentId", Db(scope.AgentId));
            insert.Parameters.AddWithValue("$taskId", Db(scope.TaskId));
            insert.Parameters.AddWithValue("$revision", nextRevision);
            insert.Parameters.AddWithValue("$dataJson", dataJson);
            insert.Parameters.AddWithValue("$contentHash", contentHash);
            insert.Parameters.AddWithValue("$causationId", Db(request.CausationId));
            insert.Parameters.AddWithValue("$correlationId", Db(request.CorrelationId));
            insert.Parameters.AddWithValue("$occurredAt", occurredAt);
            insert.Parameters.AddWithValue("$storedAt", storedAt);
            insert.Parameters.AddWithValue("$metadataJson", metadataJson);
            var sequence = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new OperationalEventWriteResult(eventId, sequence, nextRevision, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OperationalEvent>> ReadAsync(
        OperationalEventQuery query,
        CancellationToken cancellationToken = default)
    {
        EnsureOperationalJournalEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        if (query.Limit is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(query), "Operational event query limit must be between 1 and 10000.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, sequence, revision, event_type, subject_type, subject_id,
                       workspace_id, project_id, session_id, agent_id, task_id, data_json,
                       content_hash, causation_id, correlation_id, occurred_at, stored_at, metadata_json
                FROM operational_events
                WHERE workspace_id=$workspaceId
                  AND ($projectId IS NULL OR project_id=$projectId)
                  AND ($sessionId IS NULL OR session_id=$sessionId)
                  AND ($agentId IS NULL OR agent_id=$agentId)
                  AND ($taskId IS NULL OR task_id=$taskId)
                  AND ($objectType IS NULL OR subject_type=$objectType)
                  AND ($objectId IS NULL OR subject_id=$objectId)
                  AND ($eventType IS NULL OR event_type=$eventType)
                  AND ($afterSequence IS NULL OR sequence>$afterSequence)
                ORDER BY sequence ASC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$workspaceId", query.WorkspaceId.Trim());
            command.Parameters.AddWithValue("$projectId", Db(query.ProjectId));
            command.Parameters.AddWithValue("$sessionId", Db(query.SessionId));
            command.Parameters.AddWithValue("$agentId", Db(query.AgentId));
            command.Parameters.AddWithValue("$taskId", Db(query.TaskId));
            command.Parameters.AddWithValue("$objectType", Db(query.ObjectType));
            command.Parameters.AddWithValue("$objectId", Db(query.ObjectId));
            command.Parameters.AddWithValue("$eventType", Db(query.EventType));
            command.Parameters.AddWithValue("$afterSequence", query.AfterSequence ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$limit", query.Limit);

            var events = new List<OperationalEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new OperationalEvent(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                    new OperationalObjectRef(reader.GetString(4), reader.GetString(5)),
                    new OperationalScope(reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8),
                        NullableString(reader, 9), NullableString(reader, 10)),
                    reader.GetString(11), reader.GetString(12), NullableString(reader, 13), NullableString(reader, 14),
                    DateTimeOffset.Parse(reader.GetString(15)), DateTimeOffset.Parse(reader.GetString(16)),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(17)) ?? []));
            }
            return events;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureOperationalJournalEnabled()
    {
        if (!_enableOperationalEventJournal)
            throw new InvalidOperationException("Operational event journal is disabled by configuration.");
    }

    private static void ValidateOperationalWrite(OperationalEventWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EventType);
        ArgumentNullException.ThrowIfNull(request.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject.ObjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject.ObjectId);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Scope.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DataJson);
        if (request.ExpectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Expected revision cannot be negative.");
    }

    private static OperationalScope NormalizeScope(OperationalScope scope) => new(
        scope.WorkspaceId.Trim(), Clean(scope.ProjectId), Clean(scope.SessionId), Clean(scope.AgentId), Clean(scope.TaskId));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Operational event data must be valid JSON.", nameof(json), error);
        }
    }

    private static string ComputeScopeKey(OperationalScope scope)
    {
        var canonical = JsonSerializer.Serialize(new[]
        {
            scope.WorkspaceId, scope.ProjectId, scope.SessionId, scope.AgentId, scope.TaskId
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task PreserveOperationalEnvelopeAsync(
        ArchivedOperationalEvent envelope,
        CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.EventId)));
        var path = Path.Combine(layout.Root, "operational-events", key[..2], key + ".json");
        EnsurePhysicalDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            await using var existingStream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<ArchivedOperationalEvent>(existingStream,
                cancellationToken: cancellationToken);
            if (existing is null || !string.Equals(existing.EventId, envelope.EventId, StringComparison.Ordinal) ||
                !string.Equals(existing.ContentHash, envelope.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Immutable operational archive collision for event '{envelope.EventId}'.");
            return;
        }

        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            try
            {
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                await using var existingStream = File.OpenRead(path);
                var existing = await JsonSerializer.DeserializeAsync<ArchivedOperationalEvent>(existingStream,
                    cancellationToken: cancellationToken);
                if (existing is null || !string.Equals(existing.EventId, envelope.EventId, StringComparison.Ordinal) ||
                    !string.Equals(existing.ContentHash, envelope.ContentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Immutable operational archive collision for event '{envelope.EventId}'.");
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record OperationalImmutableContent(
        string EventType,
        OperationalObjectRef Subject,
        OperationalScope Scope,
        string DataJson,
        string? CausationId,
        string? CorrelationId,
        string? ExplicitOccurredAt,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed record ArchivedOperationalEvent(
        string EventId,
        long Revision,
        string EventType,
        OperationalObjectRef Subject,
        OperationalScope Scope,
        string DataJson,
        string ContentHash,
        string? CausationId,
        string? CorrelationId,
        string OccurredAt,
        string StoredAt,
        IReadOnlyDictionary<string, string> Metadata);
}
