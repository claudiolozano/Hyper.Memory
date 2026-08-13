using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperMemory.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HyperMemory.Infrastructure;

public sealed class SqliteProjectStateProjectionStore : IProjectStateProjectionStore, IDisposable
{
    private const string ProjectorVersion = "project-state-v1";
    private readonly StorageLayout _layout;
    private readonly IOperationalEventStore _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;

    public SqliteProjectStateProjectionStore(
        StorageLayout layout,
        IOperationalEventStore events,
        IOptions<HyperMemoryOptions> options)
    {
        if (!options.Value.Operational.EnableEventJournal || !options.Value.Operational.EnableProjectState)
            throw new InvalidOperationException("Project state projection is disabled by configuration.");
        _layout = layout;
        _events = events;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public async Task<int> ProjectPendingAsync(
        OperationalScope scope,
        int batchSize = 200,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        if (batchSize is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Projection batch size must be between 1 and 10000.");
        var projectScope = ProjectScope(scope);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ProjectPendingCoreAsync(projectScope, batchSize, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectStateSnapshot?> GetCurrentAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var projectScope = ProjectScope(scope);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await ReadSnapshotAsync(connection, ScopeKey(projectScope), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RebuildAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var projectScope = ProjectScope(scope);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM project_state_projections WHERE scope_key=$scopeKey";
                delete.Parameters.AddWithValue("$scopeKey", ScopeKey(projectScope));
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            int projected;
            do
            {
                projected = await ProjectPendingCoreAsync(projectScope, 1_000, cancellationToken);
            } while (projected == 1_000);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ProjectPendingCoreAsync(
        OperationalScope scope,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var key = ScopeKey(scope);
        var current = await ReadSnapshotAsync(connection, key, cancellationToken) ?? EmptySnapshot(scope);
        var pending = await _events.ReadAsync(new OperationalEventQuery(
            scope.WorkspaceId,
            ProjectId: scope.ProjectId,
            AfterSequence: current.ThroughSequence,
            Limit: batchSize), cancellationToken);
        if (pending.Count == 0) return 0;

        var next = Reduce(current, pending);
        var snapshotJson = JsonSerializer.Serialize(next);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO project_state_projections(
                scope_key, workspace_id, project_id, through_sequence, snapshot_json,
                projector_version, projected_at)
            VALUES($scopeKey, $workspaceId, $projectId, $throughSequence, $snapshotJson,
                $projectorVersion, $projectedAt)
            ON CONFLICT(scope_key) DO UPDATE SET
                through_sequence=excluded.through_sequence,
                snapshot_json=excluded.snapshot_json,
                projector_version=excluded.projector_version,
                projected_at=excluded.projected_at
            WHERE excluded.through_sequence >= project_state_projections.through_sequence
            """;
        upsert.Parameters.AddWithValue("$scopeKey", key);
        upsert.Parameters.AddWithValue("$workspaceId", scope.WorkspaceId);
        upsert.Parameters.AddWithValue("$projectId", Db(scope.ProjectId));
        upsert.Parameters.AddWithValue("$throughSequence", next.ThroughSequence);
        upsert.Parameters.AddWithValue("$snapshotJson", snapshotJson);
        upsert.Parameters.AddWithValue("$projectorVersion", ProjectorVersion);
        upsert.Parameters.AddWithValue("$projectedAt", next.ProjectedAt.ToUniversalTime().ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending.Count;
    }

    private static ProjectStateSnapshot Reduce(
        ProjectStateSnapshot current,
        IReadOnlyList<OperationalEvent> pending)
    {
        var artifacts = current.Artifacts.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var relationships = current.Relationships.ToDictionary(item => item.RelationshipId, StringComparer.Ordinal);
        var contracts = current.Contracts.ToDictionary(item => item.ContractId, StringComparer.Ordinal);
        var tasks = current.Tasks.ToDictionary(item => item.TaskId, StringComparer.Ordinal);
        var dependencies = current.TaskDependencies.ToDictionary(
            item => DependencyKey(item.FromTaskId, item.ToTaskId, item.DependencyType), StringComparer.Ordinal);
        var validations = current.Validations.ToDictionary(item => item.ValidationId, StringComparer.Ordinal);
        var errors = current.Errors.ToDictionary(item => item.ErrorId, StringComparer.Ordinal);
        var decisions = current.Decisions.ToDictionary(item => item.DecisionId, StringComparer.Ordinal);
        var working = current.WorkingMemory.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var statements = current.Statements.ToDictionary(item => item.StatementId, StringComparer.Ordinal);

        foreach (var item in pending)
        {
            switch (item.EventType)
            {
                case "artifact.observed":
                case "artifact.changed":
                case "artifact.deleted":
                {
                    var change = Parse<ArtifactStateChange>(item);
                    artifacts[Required(change.ArtifactId, "artifact id")] = new ArtifactState(
                        change.ArtifactId, Required(change.Uri, "artifact URI"), Required(change.ArtifactType, "artifact type"),
                        change.ContentHash, change.Revision, change.IsSourceOfTruth, change.ObservedAt ?? item.OccurredAt,
                        item.EventId, change.Metadata)
                    {
                        IsDeleted = item.EventType == "artifact.deleted" || change.IsDeleted
                    };
                    break;
                }
                case "relationship.upserted":
                case "relationship.removed":
                {
                    var change = Parse<RelationshipStateChange>(item);
                    relationships[Required(change.RelationshipId, "relationship id")] = new OperationalRelationship(
                        change.RelationshipId, change.From, change.To, Required(change.RelationshipType, "relationship type"),
                        item.EventId, item.Revision, item.EventType != "relationship.removed" && change.IsActive,
                        item.OccurredAt, change.Metadata);
                    break;
                }
                case "contract.upserted":
                case "contract.invalidated":
                {
                    var change = Parse<ContractStateChange>(item);
                    contracts[Required(change.ContractId, "contract id")] = new ContractRecord(
                        change.ContractId, change.Subject, Required(change.ContractType, "contract type"),
                        Required(change.DefinitionJson, "contract definition"), item.Revision, item.EventId,
                        item.EventType != "contract.invalidated" && change.IsActive, item.OccurredAt)
                    {
                        Dependencies = change.Dependencies ?? []
                    };
                    break;
                }
                case "task.created":
                case "task.updated":
                case "task.completed":
                case "task.failed":
                {
                    var change = Parse<TaskStateChange>(item);
                    var status = item.EventType switch
                    {
                        "task.completed" => "completed",
                        "task.failed" => "failed",
                        _ => Required(change.Status, "task status")
                    };
                    tasks[Required(change.TaskId, "task id")] = new TaskRecord(
                        change.TaskId, Required(change.Title, "task title"), status, change.ParentTaskId,
                        change.RequiredEvidenceIds ?? [], item.Revision, item.EventId, item.OccurredAt, change.Metadata);
                    break;
                }
                case "task.dependency.upserted":
                case "task.dependency.removed":
                {
                    var change = Parse<TaskDependencyStateChange>(item);
                    var key = DependencyKey(Required(change.FromTaskId, "source task id"),
                        Required(change.ToTaskId, "target task id"), Required(change.DependencyType, "dependency type"));
                    dependencies[key] = new TaskDependency(change.FromTaskId, change.ToTaskId, change.DependencyType,
                        item.EventId, item.EventType != "task.dependency.removed" && change.IsActive);
                    break;
                }
                case "validation.recorded":
                case "validation.stale":
                {
                    var change = Parse<ValidationStateChange>(item);
                    validations[Required(change.ValidationId, "validation id")] = new ValidationRecord(
                        change.ValidationId, change.Subject, Required(change.ValidatorId, "validator id"),
                        item.EventType == "validation.stale" ? ValidationStatus.Stale : change.Status,
                        Required(change.ScopeJson, "validation scope"), change.EvidenceIds ?? [], item.EventId,
                        item.OccurredAt, item.EventType == "validation.stale" ? item.OccurredAt : change.StaleAt,
                        change.Explanation);
                    break;
                }
                case "error.observed":
                case "error.updated":
                case "error.repair-attempted":
                case "error.resolved":
                {
                    var change = Parse<ErrorStateChange>(item);
                    var errorRecord = new ErrorRecord(
                        change.ErrorId, Required(change.ErrorType, "error type"), Required(change.Message, "error message"),
                        Required(change.Fingerprint, "error fingerprint"), Required(change.Status, "error status"),
                        change.ArtifactIds ?? [], change.EvidenceIds ?? [], change.RepairAttempts,
                        change.MaxRepairAttempts, item.Revision, item.EventId, item.OccurredAt, change.Metadata);
                    errors[Required(change.ErrorId, "error id")] = errorRecord with
                    {
                        Occurrences = Math.Max(1, change.Occurrences),
                        FirstSeenAt = change.FirstSeenAt ?? item.OccurredAt,
                        LastSeenAt = change.LastSeenAt ?? item.OccurredAt
                    };
                    break;
                }
                case "decision.recorded":
                case "decision.superseded":
                {
                    var change = Parse<DecisionStateChange>(item);
                    decisions[Required(change.DecisionId, "decision id")] = new DecisionRecord(
                        change.DecisionId, Required(change.Title, "decision title"), Required(change.Outcome, "decision outcome"),
                        Required(change.Rationale, "decision rationale"),
                        item.EventType == "decision.superseded" ? "superseded" : Required(change.Status, "decision status"),
                        change.SupersedesDecisionId, change.EvidenceIds ?? [], item.Revision, item.EventId,
                        item.OccurredAt, change.Metadata);
                    if (!string.IsNullOrWhiteSpace(change.SupersedesDecisionId) &&
                        decisions.TryGetValue(change.SupersedesDecisionId, out var superseded))
                        decisions[change.SupersedesDecisionId] = superseded with { Status = "superseded" };
                    break;
                }
                case "working.upserted":
                {
                    var change = Parse<WorkingMemoryChange>(item);
                    working[Required(change.Key, "working-memory key")] = new WorkingMemoryItem(
                        change.Key, Required(change.ItemType, "working-memory type"),
                        Required(change.ValueJson, "working-memory value"), change.Priority, change.ExpiresAt,
                        item.Revision, item.EventId, item.OccurredAt, change.Metadata);
                    break;
                }
                case "working.removed":
                case "working.expired":
                {
                    var change = Parse<WorkingMemoryChange>(item);
                    working.Remove(Required(change.Key, "working-memory key"));
                    break;
                }
                case "statement.recorded":
                case "statement.superseded":
                {
                    var change = Parse<ProjectStatementChange>(item);
                    statements[Required(change.StatementId, "statement id")] = new ProjectStatementRecord(
                        change.StatementId, Required(change.StatementType, "statement type"),
                        Required(change.Text, "statement text"),
                        item.EventType == "statement.superseded" ? "superseded" : Required(change.Status, "statement status"),
                        Required(change.Provenance, "statement provenance"), change.Confidence,
                        change.EvidenceIds ?? [], item.Revision, item.EventId, item.OccurredAt, change.Metadata);
                    break;
                }
            }
        }

        return new ProjectStateSnapshot(
            current.Scope,
            pending[^1].Sequence,
            artifacts.Values.OrderBy(item => item.ArtifactId, StringComparer.Ordinal).ToArray(),
            relationships.Values.OrderBy(item => item.RelationshipId, StringComparer.Ordinal).ToArray(),
            contracts.Values.OrderBy(item => item.ContractId, StringComparer.Ordinal).ToArray(),
            tasks.Values.OrderBy(item => item.TaskId, StringComparer.Ordinal).ToArray(),
            dependencies.Values.OrderBy(item => DependencyKey(item.FromTaskId, item.ToTaskId, item.DependencyType), StringComparer.Ordinal).ToArray(),
            validations.Values.OrderBy(item => item.ValidationId, StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow)
        {
            Errors = errors.Values.OrderBy(item => item.ErrorId, StringComparer.Ordinal).ToArray(),
            Decisions = decisions.Values.OrderBy(item => item.DecisionId, StringComparer.Ordinal).ToArray(),
            WorkingMemory = working.Values.OrderByDescending(item => item.Priority)
                .ThenBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            Statements = statements.Values.OrderBy(item => item.StatementType, StringComparer.Ordinal)
                .ThenBy(item => item.StatementId, StringComparer.Ordinal).ToArray()
        };
    }

    private static T Parse<T>(OperationalEvent item)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(item.DataJson) ??
                throw new InvalidOperationException($"Operational event '{item.EventId}' has an empty {typeof(T).Name} payload.");
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                $"Operational event '{item.EventId}' has an invalid {typeof(T).Name} payload.", error);
        }
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Projected {name} cannot be empty.") : value.Trim();

    private static string DependencyKey(string from, string to, string type) => $"{from}\u001f{to}\u001f{type}";

    private static ProjectStateSnapshot EmptySnapshot(OperationalScope scope) =>
        new(scope, 0, [], [], [], [], [], [], DateTimeOffset.UtcNow);

    private static OperationalScope ProjectScope(OperationalScope scope) =>
        new(scope.WorkspaceId.Trim(), Clean(scope.ProjectId));

    private static void ValidateScope(OperationalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static object Db(string? value) => value is null ? DBNull.Value : value;

    private static string ScopeKey(OperationalScope scope)
    {
        var canonical = JsonSerializer.Serialize(new[] { scope.WorkspaceId, scope.ProjectId });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<ProjectStateSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM project_state_projections WHERE scope_key=$scopeKey";
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : JsonSerializer.Deserialize<ProjectStateSnapshot>(json) ??
            throw new InvalidOperationException("Stored project state projection is invalid.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_layout.DatabasePath))
            throw new InvalidOperationException("HyperMemory must be initialized before projecting project state.");
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public void Dispose() => _gate.Dispose();
}
