using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperMemory.Core;
using Microsoft.Data.Sqlite;

namespace HyperMemory.Infrastructure;

public sealed class SqliteMemoryStore(StorageLayout layout) : IMemoryStore, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = layout.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true,
        Pooling = true
    }.ToString();

    public string StorageRoot => layout.Root;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA foreign_keys=ON;
                PRAGMA auto_vacuum=NONE;
                CREATE TABLE IF NOT EXISTS memory_atoms (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    version_id TEXT NOT NULL UNIQUE,
                    logical_id TEXT NOT NULL,
                    content TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    project TEXT NULL,
                    source TEXT NULL,
                    metadata_json TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    stored_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_memory_atoms_logical ON memory_atoms(logical_id, sequence DESC);
                CREATE INDEX IF NOT EXISTS ix_memory_atoms_project ON memory_atoms(project, sequence DESC);
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                    version_id UNINDEXED, content, project, tokenize='unicode61 remove_diacritics 2'
                );
                CREATE TABLE IF NOT EXISTS memory_vectors (
                    version_id TEXT PRIMARY KEY REFERENCES memory_atoms(version_id),
                    provider TEXT NOT NULL,
                    model TEXT NOT NULL,
                    dimensions INTEGER NOT NULL,
                    vector BLOB NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_vectors_space ON memory_vectors(provider, model, dimensions);
                CREATE TABLE IF NOT EXISTS audit_log (
                    audit_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    operation TEXT NOT NULL CHECK(operation = 'append'),
                    version_id TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS memory_evidence (
                    version_id TEXT PRIMARY KEY REFERENCES memory_atoms(version_id),
                    source_uri TEXT NULL,
                    source_title TEXT NULL,
                    author TEXT NULL,
                    valid_from TEXT NULL,
                    valid_to TEXT NULL,
                    supersedes_version_id TEXT NULL,
                    claim_key TEXT NULL,
                    stated_confidence REAL NULL CHECK(stated_confidence IS NULL OR (stated_confidence >= 0 AND stated_confidence <= 1))
                );
                CREATE INDEX IF NOT EXISTS ix_evidence_claim ON memory_evidence(claim_key, version_id);
                CREATE INDEX IF NOT EXISTS ix_evidence_validity ON memory_evidence(valid_from, valid_to);
                CREATE TABLE IF NOT EXISTS memory_relations (
                    relation_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_version_id TEXT NOT NULL REFERENCES memory_atoms(version_id),
                    to_version_id TEXT NOT NULL REFERENCES memory_atoms(version_id),
                    relation_type TEXT NOT NULL CHECK(relation_type IN ('supersedes','contradicts')),
                    created_at TEXT NOT NULL,
                    UNIQUE(from_version_id, to_version_id, relation_type)
                );
                CREATE INDEX IF NOT EXISTS ix_relations_to ON memory_relations(to_version_id, relation_type);
                INSERT OR IGNORE INTO memory_evidence(version_id)
                    SELECT version_id FROM memory_atoms;
                """, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<MemoryWriteResult> AppendAsync(MemoryWriteRequest request, EmbeddingVector embedding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        if (embedding.Values.Length == 0) throw new ArgumentException("Embedding cannot be empty.", nameof(embedding));
        var versionId = string.IsNullOrWhiteSpace(request.EventId) ? Guid.NewGuid().ToString("N") : request.EventId.Trim();
        var logicalId = string.IsNullOrWhiteSpace(request.LogicalId) ? versionId : request.LogicalId.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Content)));
        var metadata = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>());
        var occurred = (request.OccurredAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O");
        var stored = DateTimeOffset.UtcNow.ToString("O");
        var validFrom = request.ValidFrom?.ToUniversalTime().ToString("O");
        var validTo = request.ValidTo?.ToUniversalTime().ToString("O");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.SupersedesVersionId) &&
                !await VersionExistsAsync(connection, request.SupersedesVersionId.Trim(), cancellationToken))
                throw new InvalidOperationException($"Superseded version '{request.SupersedesVersionId}' does not exist.");
            await PreserveImmutableEnvelopeAsync(new ArchivedEvent(versionId, logicalId, request.Content, hash,
                request.Project, request.Source, metadata, occurred, stored, embedding, request.SourceUri,
                request.SourceTitle, request.Author, validFrom, validTo, request.SupersedesVersionId,
                request.ClaimKey, request.StatedConfidence), cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = (SqliteTransaction)transaction;
                existing.CommandText = "SELECT logical_id, sequence, content_hash FROM memory_atoms WHERE version_id=$id";
                existing.Parameters.AddWithValue("$id", versionId);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!string.Equals(reader.GetString(2), hash, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Event id '{versionId}' already exists with different immutable content.");
                    return new MemoryWriteResult(versionId, reader.GetString(0), reader.GetInt64(1), false);
                }
            }

            long sequence;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO memory_atoms(version_id,logical_id,content,content_hash,project,source,metadata_json,occurred_at,stored_at)
                    VALUES($version,$logical,$content,$hash,$project,$source,$metadata,$occurred,$stored);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$version", versionId);
                insert.Parameters.AddWithValue("$logical", logicalId);
                insert.Parameters.AddWithValue("$content", request.Content);
                insert.Parameters.AddWithValue("$hash", hash);
                insert.Parameters.AddWithValue("$project", (object?)request.Project ?? DBNull.Value);
                insert.Parameters.AddWithValue("$source", (object?)request.Source ?? DBNull.Value);
                insert.Parameters.AddWithValue("$metadata", metadata);
                insert.Parameters.AddWithValue("$occurred", occurred);
                insert.Parameters.AddWithValue("$stored", stored);
                sequence = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            }

            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO memory_fts(version_id,content,project) VALUES($id,$content,$project)",
                [("$id", versionId), ("$content", request.Content), ("$project", (object?)request.Project ?? "")], cancellationToken);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO memory_vectors(version_id,provider,model,dimensions,vector) VALUES($id,$provider,$model,$dimensions,$vector)",
                [("$id", versionId), ("$provider", embedding.Provider), ("$model", embedding.Model), ("$dimensions", embedding.Dimensions), ("$vector", Serialize(embedding.Values))], cancellationToken);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                """
                INSERT INTO memory_evidence(version_id,source_uri,source_title,author,valid_from,valid_to,supersedes_version_id,claim_key,stated_confidence)
                VALUES($id,$uri,$title,$author,$from,$to,$supersedes,$claim,$confidence)
                """,
                [("$id", versionId), ("$uri", Db(request.SourceUri)), ("$title", Db(request.SourceTitle)),
                 ("$author", Db(request.Author)), ("$from", Db(validFrom)), ("$to", Db(validTo)),
                 ("$supersedes", Db(request.SupersedesVersionId)), ("$claim", Db(request.ClaimKey)),
                 ("$confidence", (object?)request.StatedConfidence ?? DBNull.Value)], cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.SupersedesVersionId))
                await InsertRelationAsync(connection, (SqliteTransaction)transaction, versionId,
                    request.SupersedesVersionId.Trim(), "supersedes", stored, cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.ClaimKey))
                await InsertClaimContradictionsAsync(connection, (SqliteTransaction)transaction, versionId,
                    request.ClaimKey.Trim(), hash, stored, cancellationToken);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO audit_log(operation,version_id,content_hash,created_at) VALUES('append',$id,$hash,$created)",
                [("$id", versionId), ("$hash", hash), ("$created", stored)], cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new MemoryWriteResult(versionId, logicalId, sequence, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, EmbeddingVector embedding, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var ftsQuery = BuildFtsQuery(query.Text);
        if (ftsQuery.Length > 0)
        {
            await using var text = connection.CreateCommand();
            text.CommandText = """
                SELECT a.version_id,a.logical_id,a.sequence,a.content,a.content_hash,a.project,a.source,a.metadata_json,a.occurred_at,a.stored_at,
                       e.source_uri,e.source_title,e.author,e.valid_from,e.valid_to,e.supersedes_version_id,e.claim_key,e.stated_confidence,bm25(memory_fts)
                FROM memory_fts JOIN memory_atoms a ON a.version_id=memory_fts.version_id
                LEFT JOIN memory_evidence e ON e.version_id=a.version_id
                WHERE memory_fts MATCH $query AND ($project IS NULL OR a.project=$project) AND ($before IS NULL OR a.sequence<$before)
                  AND ($occurredFrom IS NULL OR a.occurred_at >= $occurredFrom)
                  AND ($occurredTo IS NULL OR a.occurred_at <= $occurredTo)
                  AND ($validAt IS NULL OR (e.valid_from IS NULL OR e.valid_from <= $validAt) AND (e.valid_to IS NULL OR e.valid_to >= $validAt))
                  AND ($includeSuperseded = 1 OR NOT EXISTS (
                      SELECT 1 FROM memory_relations r WHERE r.to_version_id=a.version_id AND r.relation_type='supersedes'))
                ORDER BY bm25(memory_fts) LIMIT $limit
                """;
            text.Parameters.AddWithValue("$query", ftsQuery);
            text.Parameters.AddWithValue("$project", (object?)query.Project ?? DBNull.Value);
            text.Parameters.AddWithValue("$before", (object?)query.BeforeSequence ?? DBNull.Value);
            AddTemporalParameters(text, query);
            text.Parameters.AddWithValue("$limit", Math.Max(query.Limit * 5, 50));
            await using var reader = await text.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var atom = ReadAtom(reader);
                var rank = reader.GetDouble(18);
                candidates[atom.VersionId] = new Candidate(atom, 1d / (1d + Math.Abs(rank)), 0);
            }
        }

        var semanticTop = new PriorityQueue<Candidate, double>();
        var semanticCapacity = Math.Max(query.Limit * 10, 100);
        await using (var semantic = connection.CreateCommand())
        {
            semantic.CommandText = """
                SELECT a.version_id,a.logical_id,a.sequence,a.content,a.content_hash,a.project,a.source,a.metadata_json,a.occurred_at,a.stored_at,
                       e.source_uri,e.source_title,e.author,e.valid_from,e.valid_to,e.supersedes_version_id,e.claim_key,e.stated_confidence,v.vector
                FROM memory_vectors v JOIN memory_atoms a ON a.version_id=v.version_id
                LEFT JOIN memory_evidence e ON e.version_id=a.version_id
                WHERE v.provider=$provider AND v.model=$model AND v.dimensions=$dimensions
                  AND ($project IS NULL OR a.project=$project) AND ($before IS NULL OR a.sequence<$before)
                  AND ($occurredFrom IS NULL OR a.occurred_at >= $occurredFrom)
                  AND ($occurredTo IS NULL OR a.occurred_at <= $occurredTo)
                  AND ($validAt IS NULL OR (e.valid_from IS NULL OR e.valid_from <= $validAt) AND (e.valid_to IS NULL OR e.valid_to >= $validAt))
                  AND ($includeSuperseded = 1 OR NOT EXISTS (
                      SELECT 1 FROM memory_relations r WHERE r.to_version_id=a.version_id AND r.relation_type='supersedes'))
                ORDER BY a.sequence DESC
                """;
            semantic.Parameters.AddWithValue("$provider", embedding.Provider);
            semantic.Parameters.AddWithValue("$model", embedding.Model);
            semantic.Parameters.AddWithValue("$dimensions", embedding.Dimensions);
            semantic.Parameters.AddWithValue("$project", (object?)query.Project ?? DBNull.Value);
            semantic.Parameters.AddWithValue("$before", (object?)query.BeforeSequence ?? DBNull.Value);
            AddTemporalParameters(semantic, query);
            await using var reader = await semantic.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var atom = ReadAtom(reader);
                var similarity = Math.Max(0, Cosine(embedding.Values, Deserialize((byte[])reader[18])));
                if (candidates.TryGetValue(atom.VersionId, out var prior))
                    candidates[atom.VersionId] = prior with { Semantic = similarity };
                else if (similarity > 0)
                {
                    semanticTop.Enqueue(new Candidate(atom, 0, similarity), similarity);
                    if (semanticTop.Count > semanticCapacity) semanticTop.Dequeue();
                }
            }
        }
        foreach (var item in semanticTop.UnorderedItems)
            candidates[item.Element.Atom.VersionId] = item.Element;

        var weightSum = query.TextWeight + query.SemanticWeight;
        var ranked = candidates.Values
            .Select(x => new RankedCandidate(x,
                (x.Text * query.TextWeight + x.Semantic * query.SemanticWeight) / weightSum))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Candidate.Atom.Sequence)
            .Take(query.Limit).ToArray();
        var results = new List<MemoryHit>(ranked.Length);
        foreach (var item in ranked)
            results.Add(await EnrichHitAsync(connection, item.Candidate, item.Score, cancellationToken));
        return results;
    }

    public async Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var problems = new List<string>();
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) problems.Add($"SQLite: {result}");
        }
        var atoms = await ScalarAsync(connection, "SELECT count(*) FROM memory_atoms", cancellationToken);
        var vectors = await ScalarAsync(connection, "SELECT count(*) FROM memory_vectors", cancellationToken);
        var audits = await ScalarAsync(connection, "SELECT count(*) FROM audit_log", cancellationToken);
        var fts = await ScalarAsync(connection, "SELECT count(*) FROM memory_fts", cancellationToken);
        var evidence = await ScalarAsync(connection, "SELECT count(*) FROM memory_evidence", cancellationToken);
        if (atoms != vectors) problems.Add($"Atom/vector count mismatch: {atoms}/{vectors}.");
        if (atoms != audits) problems.Add($"Atom/audit count mismatch: {atoms}/{audits}.");
        if (atoms != fts) problems.Add($"Atom/FTS count mismatch: {atoms}/{fts}.");
        if (atoms != evidence) problems.Add($"Atom/evidence count mismatch: {atoms}/{evidence}.");

        await using var hashes = connection.CreateCommand();
        hashes.CommandText = "SELECT version_id,content,content_hash FROM memory_atoms";
        await using var reader = await hashes.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reader.GetString(1))));
            if (!string.Equals(expected, reader.GetString(2), StringComparison.Ordinal))
                problems.Add($"Content hash mismatch for {reader.GetString(0)}.");
            var archivePath = GetArchivePath(reader.GetString(0));
            if (!File.Exists(archivePath))
                problems.Add($"Immutable archive missing for {reader.GetString(0)}.");
            else
            {
                try
                {
                    await using var archive = File.OpenRead(archivePath);
                    var envelope = await JsonSerializer.DeserializeAsync<ArchivedEvent>(archive, cancellationToken: cancellationToken);
                    if (envelope is null || !string.Equals(envelope.ContentHash, reader.GetString(2), StringComparison.Ordinal))
                        problems.Add($"Immutable archive hash mismatch for {reader.GetString(0)}.");
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
                {
                    problems.Add($"Immutable archive unreadable for {reader.GetString(0)}: {error.Message}");
                }
            }
        }
        return new IntegrityReport(problems.Count == 0, atoms, vectors, audits, problems);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteInTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        IEnumerable<(string Name, object Value)> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> VersionExistsAsync(SqliteConnection connection, string versionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM memory_atoms WHERE version_id=$id)";
        command.Parameters.AddWithValue("$id", versionId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static MemoryAtom ReadAtom(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8)), DateTimeOffset.Parse(reader.GetString(9)),
        NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12),
        NullableDate(reader, 13), NullableDate(reader, 14), NullableString(reader, 15), NullableString(reader, 16),
        reader.IsDBNull(17) ? null : reader.GetDouble(17));

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index));

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static void AddTemporalParameters(SqliteCommand command, MemoryQuery query)
    {
        command.Parameters.AddWithValue("$occurredFrom", query.OccurredFrom?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$occurredTo", query.OccurredTo?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$validAt", query.ValidAt?.ToUniversalTime().ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$includeSuperseded", query.IncludeSuperseded ? 1 : 0);
    }

    private static string BuildFtsQuery(string value) => string.Join(" OR ", value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(32)
        .Select(x => $"\"{x.Replace("\"", "\"\"")}\""));

    private static async Task InsertRelationAsync(SqliteConnection connection, SqliteTransaction transaction,
        string from, string to, string type, string created, CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(connection, transaction,
            "INSERT OR IGNORE INTO memory_relations(from_version_id,to_version_id,relation_type,created_at) VALUES($from,$to,$type,$created)",
            [("$from", from), ("$to", to), ("$type", type), ("$created", created)], cancellationToken);
    }

    private static async Task InsertClaimContradictionsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string versionId, string claimKey, string contentHash, string created, CancellationToken cancellationToken)
    {
        var prior = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT e.version_id FROM memory_evidence e
                JOIN memory_atoms a ON a.version_id=e.version_id
                WHERE e.claim_key=$claim AND e.version_id<>$id AND a.content_hash<>$hash
                """;
            command.Parameters.AddWithValue("$claim", claimKey);
            command.Parameters.AddWithValue("$id", versionId);
            command.Parameters.AddWithValue("$hash", contentHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) prior.Add(reader.GetString(0));
        }
        foreach (var other in prior)
            await InsertRelationAsync(connection, transaction, versionId, other, "contradicts", created, cancellationToken);
    }

    private static async Task<MemoryHit> EnrichHitAsync(SqliteConnection connection, Candidate candidate, double score,
        CancellationToken cancellationToken)
    {
        var contradictions = new List<string>();
        var superseded = false;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT from_version_id,to_version_id,relation_type FROM memory_relations
            WHERE from_version_id=$id OR to_version_id=$id
            """;
        command.Parameters.AddWithValue("$id", candidate.Atom.VersionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var from = reader.GetString(0);
            var to = reader.GetString(1);
            var type = reader.GetString(2);
            if (type == "contradicts") contradictions.Add(from == candidate.Atom.VersionId ? to : from);
            if (type == "supersedes" && to == candidate.Atom.VersionId) superseded = true;
        }

        var hasPrimarySource = !string.IsNullOrWhiteSpace(candidate.Atom.SourceUri);
        var confidence = candidate.Atom.StatedConfidence ?? (hasPrimarySource ? 0.9 :
            !string.IsNullOrWhiteSpace(candidate.Atom.Source) ? 0.72 : 0.55);
        confidence *= 0.65 + 0.35 * Math.Clamp(score, 0, 1);
        if (contradictions.Count > 0) confidence *= 0.7;
        if (superseded) confidence *= 0.65;
        confidence = Math.Round(Math.Clamp(confidence, 0, 1), 3);

        var status = contradictions.Count > 0 ? "contradictory" : superseded ? "possibly_obsolete" :
            hasPrimarySource ? "source_confirmed" : "stored_context";
        var label = candidate.Atom.SourceTitle ?? candidate.Atom.Source ?? candidate.Atom.Project ?? "HyperMemory record";
        var citation = new MemoryCitation(candidate.Atom.VersionId, label, candidate.Atom.SourceUri,
            candidate.Atom.OccurredAt, candidate.Atom.ContentHash);
        var evidence = new MemoryEvidence(status, confidence, hasPrimarySource, superseded, contradictions);
        return new MemoryHit(candidate.Atom, score, candidate.Text, candidate.Semantic, citation, evidence);
    }

    private static byte[] Serialize(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4, 4), BitConverter.SingleToInt32Bits(values[i]));
        return bytes;
    }

    private static float[] Deserialize(byte[] bytes)
    {
        var values = new float[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++) values[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
        return values;
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i]; }
        return a <= 0 || b <= 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private async Task PreserveImmutableEnvelopeAsync(ArchivedEvent envelope, CancellationToken cancellationToken)
    {
        var path = GetArchivePath(envelope.VersionId);
        EnsurePhysicalDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(stream, envelope, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<ArchivedEvent>(stream, cancellationToken: cancellationToken);
            if (existing is null || !string.Equals(existing.VersionId, envelope.VersionId, StringComparison.Ordinal) ||
                !string.Equals(existing.LogicalId, envelope.LogicalId, StringComparison.Ordinal) ||
                !string.Equals(existing.ContentHash, envelope.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(existing.Project, envelope.Project, StringComparison.Ordinal) ||
                !string.Equals(existing.Source, envelope.Source, StringComparison.Ordinal) ||
                !string.Equals(existing.MetadataJson, envelope.MetadataJson, StringComparison.Ordinal) ||
                !string.Equals(existing.SourceUri, envelope.SourceUri, StringComparison.Ordinal) ||
                !string.Equals(existing.SourceTitle, envelope.SourceTitle, StringComparison.Ordinal) ||
                !string.Equals(existing.Author, envelope.Author, StringComparison.Ordinal) ||
                !string.Equals(existing.ValidFrom, envelope.ValidFrom, StringComparison.Ordinal) ||
                !string.Equals(existing.ValidTo, envelope.ValidTo, StringComparison.Ordinal) ||
                !string.Equals(existing.SupersedesVersionId, envelope.SupersedesVersionId, StringComparison.Ordinal) ||
                !string.Equals(existing.ClaimKey, envelope.ClaimKey, StringComparison.Ordinal) ||
                existing.StatedConfidence != envelope.StatedConfidence)
                throw new InvalidOperationException($"Immutable archive collision for event '{envelope.VersionId}'.");
        }
    }

    private string GetArchivePath(string versionId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionId)));
        return Path.Combine(layout.Root, "events", key[..2], key + ".json");
    }

    private static void EnsurePhysicalDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Archive path cannot traverse a link or junction: {current.FullName}");
            if (string.Equals(current.Name, "Hyper_Memory", StringComparison.OrdinalIgnoreCase)) break;
            current = current.Parent;
        }
    }

    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
    private sealed record Candidate(MemoryAtom Atom, double Text, double Semantic);
    private sealed record RankedCandidate(Candidate Candidate, double Score);
    private sealed record ArchivedEvent(
        string VersionId,
        string LogicalId,
        string Content,
        string ContentHash,
        string? Project,
        string? Source,
        string MetadataJson,
        string OccurredAt,
        string StoredAt,
        EmbeddingVector Embedding,
        string? SourceUri = null,
        string? SourceTitle = null,
        string? Author = null,
        string? ValidFrom = null,
        string? ValidTo = null,
        string? SupersedesVersionId = null,
        string? ClaimKey = null,
        double? StatedConfidence = null);
}
