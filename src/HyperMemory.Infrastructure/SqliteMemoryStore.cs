using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperMemory.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HyperMemory.Infrastructure;

public sealed partial class SqliteMemoryStore : IMemoryStore, IKnowledgeProjectionStore, IScaleMaintenanceStore,
    IOperationalDiagnosticsStore, IOperationalEventStore, IAsyncDisposable
{
    private const string TurnSeparator = "\n\nHermes response:\n";
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "al", "algo", "algun", "alguna", "alguno", "algunos", "ante", "como", "con", "cual", "cuando",
        "de", "del", "desde", "donde", "e", "el", "ella", "en", "era", "es", "ese", "esta", "este", "esto",
        "ha", "hacia", "hasta", "la", "las", "le", "lo", "los", "me", "mi", "mis", "o", "para", "pero",
        "por", "que", "se", "si", "sin", "su", "sus", "te", "tu", "tus", "un", "una", "uno", "y", "ya",
        "about", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on",
        "or", "that", "the", "this", "to", "was", "were", "what", "when", "where", "with"
    };
    private readonly StorageLayout layout;
    private readonly int _recentSemanticCandidateLimit;
    private readonly bool _enableOperationalEventJournal;
    private readonly bool _enableProjectState;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;

    public SqliteMemoryStore(StorageLayout layout) : this(layout, 2_000, new OperationalMemoryFeatureOptions()) { }

    public SqliteMemoryStore(StorageLayout layout, IOptions<HyperMemoryOptions> options)
        : this(layout, options.Value.RecentSemanticCandidateLimit, options.Value.Operational) { }

    private SqliteMemoryStore(StorageLayout layout, int recentSemanticCandidateLimit, OperationalMemoryFeatureOptions operational)
    {
        if (operational.EnableProjectState && !operational.EnableEventJournal)
            throw new InvalidOperationException("Project state requires the operational event journal.");
        if (operational.EnableValidationMemory && !operational.EnableEventJournal)
            throw new InvalidOperationException("Validation memory requires the operational event journal.");
        if ((operational.EnableErrorMemory || operational.EnableDecisionMemory) &&
            (!operational.EnableEventJournal || !operational.EnableProjectState))
            throw new InvalidOperationException("Error and decision memory require the event journal and project state.");
        if (operational.EnableContracts &&
            (!operational.EnableEventJournal || !operational.EnableProjectState || !operational.EnableValidationMemory))
            throw new InvalidOperationException("Contracts require the event journal, project state, and validation memory.");
        if ((operational.EnableCheckpoints || operational.EnableTaskGraph) &&
            (!operational.EnableEventJournal || !operational.EnableProjectState || !operational.EnableValidationMemory))
            throw new InvalidOperationException("Checkpoints and task evaluation require the event journal, project state, and validation memory.");
        if (operational.EnableSelectiveMemoryRouter &&
            (!operational.EnableEventJournal || !operational.EnableProjectState))
            throw new InvalidOperationException("Selective memory routing requires the event journal and project state.");
        if (operational.EnableWorkingMemory &&
            (!operational.EnableEventJournal || !operational.EnableProjectState))
            throw new InvalidOperationException("Working memory requires the event journal and project state.");
        this.layout = layout;
        _recentSemanticCandidateLimit = Math.Clamp(recentSemanticCandidateLimit, 100, 100_000);
        _enableOperationalEventJournal = operational.EnableEventJournal;
        _enableProjectState = operational.EnableProjectState;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

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
                PRAGMA busy_timeout=5000;
                PRAGMA wal_autocheckpoint=1000;
                PRAGMA auto_vacuum=NONE;
                CREATE TABLE IF NOT EXISTS memory_schema (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT OR IGNORE INTO memory_schema(key,value) VALUES('version','4');
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
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_turn_fts USING fts5(
                    version_id UNINDEXED, user_content, assistant_content, project UNINDEXED,
                    tokenize='unicode61 remove_diacritics 2'
                );
                CREATE TABLE IF NOT EXISTS memory_turn_indexed (
                    version_id TEXT PRIMARY KEY REFERENCES memory_atoms(version_id)
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
                CREATE TABLE IF NOT EXISTS knowledge_entities (
                    entity_id TEXT PRIMARY KEY,
                    entity_type TEXT NOT NULL,
                    label TEXT NOT NULL,
                    normalized_label TEXT NOT NULL,
                    first_seen_version_id TEXT NOT NULL REFERENCES memory_atoms(version_id),
                    last_seen_version_id TEXT NOT NULL REFERENCES memory_atoms(version_id),
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_knowledge_entities_type_label
                    ON knowledge_entities(entity_type, normalized_label);
                CREATE TABLE IF NOT EXISTS knowledge_mentions (
                    mention_id TEXT PRIMARY KEY,
                    version_id TEXT NOT NULL REFERENCES memory_atoms(version_id) ON DELETE CASCADE,
                    entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE CASCADE,
                    role TEXT NOT NULL,
                    evidence_class TEXT NOT NULL CHECK(evidence_class IN ('EXTRACTED','INFERRED','AMBIGUOUS','VERIFIED')),
                    confidence REAL NOT NULL CHECK(confidence >= 0 AND confidence <= 1),
                    start_offset INTEGER NULL,
                    end_offset INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS ix_knowledge_mentions_version ON knowledge_mentions(version_id, role);
                CREATE INDEX IF NOT EXISTS ix_knowledge_mentions_entity ON knowledge_mentions(entity_id, version_id);
                CREATE TABLE IF NOT EXISTS knowledge_edges (
                    relation_id TEXT PRIMARY KEY,
                    from_entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE CASCADE,
                    to_entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE CASCADE,
                    relation_type TEXT NOT NULL,
                    evidence_class TEXT NOT NULL CHECK(evidence_class IN ('EXTRACTED','INFERRED','AMBIGUOUS','VERIFIED')),
                    confidence REAL NOT NULL CHECK(confidence >= 0 AND confidence <= 1),
                    source_version_id TEXT NOT NULL REFERENCES memory_atoms(version_id) ON DELETE CASCADE,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_knowledge_edges_from ON knowledge_edges(from_entity_id, relation_type);
                CREATE INDEX IF NOT EXISTS ix_knowledge_edges_to ON knowledge_edges(to_entity_id, relation_type);
                CREATE INDEX IF NOT EXISTS ix_knowledge_edges_source ON knowledge_edges(source_version_id);
                CREATE TABLE IF NOT EXISTS knowledge_projection_state (
                    version_id TEXT PRIMARY KEY REFERENCES memory_atoms(version_id) ON DELETE CASCADE,
                    projector_version TEXT NOT NULL,
                    status TEXT NOT NULL CHECK(status IN ('complete','failed')),
                    projected_at TEXT NOT NULL,
                    error TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_knowledge_projection_status
                    ON knowledge_projection_state(projector_version, status);
                INSERT OR IGNORE INTO memory_evidence(version_id)
                    SELECT version_id FROM memory_atoms;
                """, cancellationToken);
            if (_enableOperationalEventJournal)
                await ApplyOperationalMigrationsAsync(connection, _enableProjectState, cancellationToken);
            await BackfillTurnIndexBatchAsync(connection, 500, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<MemoryWriteResult> AppendAsync(MemoryWriteRequest request, EmbeddingVector embedding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        if (embedding.Values.Length == 0) throw new ArgumentException("Embedding cannot be empty.", nameof(embedding));
        var driveRoot = Path.GetPathRoot(layout.Root);
        if (!string.IsNullOrWhiteSpace(driveRoot))
        {
            var drive = new DriveInfo(driveRoot);
            var requiredFree = Math.Max(256L * 1024 * 1024, Encoding.UTF8.GetByteCount(request.Content) * 4L);
            if (drive.AvailableFreeSpace < requiredFree)
                throw new IOException($"Insufficient free space to append memory safely. Required reserve: {requiredFree} bytes.");
        }
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
            var (userContent, assistantContent) = SplitTurn(request.Content);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO memory_turn_fts(version_id,user_content,assistant_content,project) VALUES($id,$user,$assistant,$project)",
                [("$id", versionId), ("$user", userContent), ("$assistant", assistantContent),
                 ("$project", (object?)request.Project ?? "")], cancellationToken);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO memory_turn_indexed(version_id) VALUES($id)", [("$id", versionId)], cancellationToken);
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
        var searchTokens = TokenizeSearch(query.Text);
        var ftsQuery = BuildFtsQuery(query.Text, searchTokens);
        if (ftsQuery.Length > 0)
        {
            var textMatches = new List<(MemoryAtom Atom, double Rank)>();
            await using var text = connection.CreateCommand();
            text.CommandText = """
                SELECT a.version_id,a.logical_id,a.sequence,a.content,a.content_hash,a.project,a.source,a.metadata_json,a.occurred_at,a.stored_at,
                       e.source_uri,e.source_title,e.author,e.valid_from,e.valid_to,e.supersedes_version_id,e.claim_key,e.stated_confidence,
                       bm25(memory_turn_fts,0.0,6.0,1.0,0.0)
                FROM memory_turn_fts JOIN memory_atoms a ON a.version_id=memory_turn_fts.version_id
                LEFT JOIN memory_evidence e ON e.version_id=a.version_id
                WHERE memory_turn_fts MATCH $query AND ($project IS NULL OR a.project=$project) AND ($before IS NULL OR a.sequence<$before)
                  AND ($occurredFrom IS NULL OR a.occurred_at >= $occurredFrom)
                  AND ($occurredTo IS NULL OR a.occurred_at <= $occurredTo)
                  AND ($validAt IS NULL OR (e.valid_from IS NULL OR e.valid_from <= $validAt) AND (e.valid_to IS NULL OR e.valid_to >= $validAt))
                  AND ($includeSuperseded = 1 OR NOT EXISTS (
                      SELECT 1 FROM memory_relations r WHERE r.to_version_id=a.version_id AND r.relation_type='supersedes'))
                ORDER BY bm25(memory_turn_fts,0.0,6.0,1.0,0.0) LIMIT $limit
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
                textMatches.Add((atom, rank));
            }
            for (var index = 0; index < textMatches.Count; index++)
            {
                var match = textMatches[index];
                var rankScore = 1d / (1d + index * 0.12d);
                var coverage = TokenCoverage(match.Atom.Content, searchTokens);
                // Exact topical coverage must outweigh recency/rank, especially for
                // numeric constraints such as "10 paragraphs" versus "20 paragraphs".
                var textScore = 0.45d * rankScore + 0.55d * coverage;
                textScore *= DerivedRecallPenalty(match.Atom);
                candidates[match.Atom.VersionId] = new Candidate(match.Atom, Math.Clamp(textScore, 0, 1), 0);
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
                  AND a.sequence > COALESCE((SELECT MAX(sequence) FROM memory_atoms),0)-$recentLimit
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
            semantic.Parameters.AddWithValue("$recentLimit", _recentSemanticCandidateLimit);
            semantic.Parameters.AddWithValue("$project", (object?)query.Project ?? DBNull.Value);
            semantic.Parameters.AddWithValue("$before", (object?)query.BeforeSequence ?? DBNull.Value);
            AddTemporalParameters(semantic, query);
            await using var reader = await semantic.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var atom = ReadAtom(reader);
                var similarity = Math.Max(0, Cosine(embedding.Values, Deserialize((byte[])reader[18])));
                similarity *= DerivedRecallPenalty(atom);
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

        await ExpandKnowledgeCandidatesAsync(connection, candidates, query, searchTokens, cancellationToken);

        var weightSum = query.TextWeight + query.SemanticWeight;
        var recallQuery = IsRecallQuery(searchTokens);
        var ranked = candidates.Values
            .Select(x =>
            {
                var weightedScore = (x.Text * query.TextWeight + x.Semantic * query.SemanticWeight) / weightSum;
                // Near-exact lexical evidence is stronger than an unsupported
                // semantic match. This preserves old identifiers and exact
                // constraints even when the bounded recent vector window is noisy.
                var evidenceScore = Math.Max(weightedScore, x.Text * 0.90d);
                var baseScore = evidenceScore *
                    (recallQuery ? RecallUtility(x.Atom) : 1d) * WorkspaceAffinity(x.Atom, query.PreferredWorkspace);
                var knowledgeScore = x.Knowledge * (recallQuery ? 0.95d : 1d);
                return new RankedCandidate(x, Math.Clamp(Math.Max(baseScore, knowledgeScore), 0, 1));
            })
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Candidate.Atom.Sequence)
            .Take(query.Limit).ToArray();
        var results = new List<MemoryHit>(ranked.Length);
        foreach (var item in ranked)
            results.Add(await EnrichHitAsync(connection, item.Candidate, item.Score, cancellationToken));
        return results;
    }

    private static async Task ExpandKnowledgeCandidatesAsync(SqliteConnection connection,
        IDictionary<string, Candidate> candidates, MemoryQuery query, IReadOnlyList<string> searchTokens,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0 || searchTokens.Count == 0) return;
        var seeds = candidates.Values.Where(candidate => Math.Max(candidate.Text, candidate.Semantic) > 0)
            .OrderByDescending(candidate => Math.Max(candidate.Text, candidate.Semantic))
            .Take(20).Select(candidate => candidate.Atom.VersionId).ToArray();
        if (seeds.Length == 0) return;
        var newestSeedSequence = seeds.Max(versionId => candidates[versionId].Atom.Sequence);
        var normalizedQuery = NormalizeKnowledgeLabel(query.Text);
        var related = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH seed_versions(version_id) AS (SELECT value FROM json_each($seeds)),
                relevant_entities(entity_id) AS (
                    SELECT DISTINCT m.entity_id FROM knowledge_mentions m
                    JOIN knowledge_entities e ON e.entity_id=m.entity_id
                    WHERE m.version_id IN (SELECT version_id FROM seed_versions)
                      AND e.entity_type IN ('artifact','file','decision','source','content_hash','command','verification','graph_node')
                    UNION
                    SELECT e.entity_id FROM knowledge_entities e
                    WHERE e.entity_type IN ('artifact','file','decision','source','command','verification','graph_node')
                      AND length(e.normalized_label)>=4 AND instr($query,e.normalized_label)>0
                ),
                related_versions(version_id,entity_id) AS (
                    SELECT m.version_id,m.entity_id FROM knowledge_mentions m
                    WHERE m.entity_id IN (SELECT entity_id FROM relevant_entities)
                    UNION
                    SELECT edge.source_version_id,edge.from_entity_id FROM knowledge_edges edge
                    WHERE edge.from_entity_id IN (SELECT entity_id FROM relevant_entities)
                      AND edge.evidence_class<>'AMBIGUOUS' AND edge.confidence>=0.5
                    UNION
                    SELECT edge.source_version_id,edge.to_entity_id FROM knowledge_edges edge
                    WHERE edge.to_entity_id IN (SELECT entity_id FROM relevant_entities)
                      AND edge.evidence_class<>'AMBIGUOUS' AND edge.confidence>=0.5
                )
                SELECT DISTINCT rv.version_id,e.entity_type,e.label
                FROM related_versions rv
                JOIN knowledge_entities e ON e.entity_id=rv.entity_id
                JOIN memory_atoms a ON a.version_id=rv.version_id
                LEFT JOIN memory_evidence ev ON ev.version_id=a.version_id
                WHERE ($project IS NULL OR a.project=$project) AND ($before IS NULL OR a.sequence<$before)
                  AND ($occurredFrom IS NULL OR a.occurred_at >= $occurredFrom)
                  AND ($occurredTo IS NULL OR a.occurred_at <= $occurredTo)
                  AND ($validAt IS NULL OR (ev.valid_from IS NULL OR ev.valid_from <= $validAt) AND (ev.valid_to IS NULL OR ev.valid_to >= $validAt))
                  AND ($includeSuperseded = 1 OR NOT EXISTS (
                      SELECT 1 FROM memory_relations r WHERE r.to_version_id=a.version_id AND r.relation_type='supersedes'))
                LIMIT 200
                """;
            command.Parameters.AddWithValue("$seeds", JsonSerializer.Serialize(seeds));
            command.Parameters.AddWithValue("$query", normalizedQuery);
            command.Parameters.AddWithValue("$project", (object?)query.Project ?? DBNull.Value);
            command.Parameters.AddWithValue("$before", (object?)query.BeforeSequence ?? DBNull.Value);
            AddTemporalParameters(command, query);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var version = reader.GetString(0);
                if (!related.TryGetValue(version, out var reasons)) related[version] = reasons = new(StringComparer.Ordinal);
                reasons.Add(reader.GetString(1) + ":" + reader.GetString(2));
            }
        }

        foreach (var (versionId, reasons) in related)
        {
            var directLabelMatch = reasons.Any(reason =>
            {
                var separator = reason.IndexOf(':');
                return separator >= 0 && normalizedQuery.Contains(NormalizeKnowledgeLabel(reason[(separator + 1)..]), StringComparison.Ordinal);
            });
            var score = directLabelMatch ? 0.92d : 0.72d;
            if (candidates.TryGetValue(versionId, out var existing))
            {
                // A seed must not boost itself merely because it owns an entity;
                // only a direct entity-label match is additional evidence. This
                // prevents graph metadata from flattening exact lexical ranking.
                if (!directLabelMatch && Math.Max(existing.Text, existing.Semantic) > 0) continue;
                if (!directLabelMatch && existing.Atom.Sequence > newestSeedSequence) score = 1d;
                candidates[versionId] = existing with
                {
                    Knowledge = Math.Max(existing.Knowledge, score),
                    KnowledgeReasons = reasons.OrderBy(value => value, StringComparer.Ordinal).Take(8).ToArray()
                };
                continue;
            }
            var atom = await ReadAtomByVersionAsync(connection, versionId, cancellationToken);
            if (atom is not null)
                candidates[versionId] = new Candidate(atom, 0, 0,
                    atom.Sequence > newestSeedSequence ? 1d : score,
                    reasons.OrderBy(value => value, StringComparer.Ordinal).Take(8).ToArray());
        }
    }

    private static async Task<MemoryAtom?> ReadAtomByVersionAsync(SqliteConnection connection, string versionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.version_id,a.logical_id,a.sequence,a.content,a.content_hash,a.project,a.source,a.metadata_json,a.occurred_at,a.stored_at,
                   e.source_uri,e.source_title,e.author,e.valid_from,e.valid_to,e.supersedes_version_id,e.claim_key,e.stated_confidence
            FROM memory_atoms a LEFT JOIN memory_evidence e ON e.version_id=a.version_id
            WHERE a.version_id=$version
            """;
        command.Parameters.AddWithValue("$version", versionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAtom(reader) : null;
    }

    public async Task<MemoryStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var atoms = await ScalarAsync(connection, "SELECT count(*) FROM memory_atoms", cancellationToken);
        var vectors = await ScalarAsync(connection, "SELECT count(*) FROM memory_vectors", cancellationToken);
        var audits = await ScalarAsync(connection, "SELECT count(*) FROM audit_log", cancellationToken);
        var status = atoms == vectors && atoms == audits ? "healthy" : "degraded";
        return new MemoryStatus(status, layout.Root, atoms, vectors, audits);
    }

    public async Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
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
            var turnFts = await ScalarAsync(connection, "SELECT count(*) FROM memory_turn_fts", cancellationToken);
            var evidence = await ScalarAsync(connection, "SELECT count(*) FROM memory_evidence", cancellationToken);
            if (atoms != vectors) problems.Add($"Atom/vector count mismatch: {atoms}/{vectors}.");
            if (atoms != audits) problems.Add($"Atom/audit count mismatch: {atoms}/{audits}.");
            if (atoms != fts) problems.Add($"Atom/FTS count mismatch: {atoms}/{fts}.");
            if (atoms != turnFts) problems.Add($"Atom/turn-FTS count mismatch: {atoms}/{turnFts}.");
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
        finally { _gate.Release(); }
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

    private static IReadOnlyList<string> TokenizeSearch(string value) => Regex.Matches(NormalizeSearch(value), @"[\p{L}\p{N}]+")
        .Select(match => match.Value)
        .Where(token => token.Length > 1 && !SearchStopWords.Contains(token))
        .Distinct(StringComparer.Ordinal)
        .Take(32)
        .ToArray();

    private static string BuildFtsQuery(string original, IReadOnlyList<string> tokens)
    {
        var identifiers = new List<string>();
        foreach (Match identifier in StructuredIdentifierRegex().Matches(NormalizeSearch(original)))
        {
            var parts = Regex.Matches(identifier.Value, @"[\p{L}\p{N}]+")
                .Select(match => match.Value).Where(part => part.Length > 0).ToArray();
            if (parts.Length >= 2)
                identifiers.Add($"\"{string.Join(' ', parts).Replace("\"", "\"\"")}\"");
        }
        // A structured identifier is a stronger constraint than any individual
        // component. OR-ing its generic prefix back into the query can crowd an
        // old exact hit out of the bounded FTS candidate set at multi-year scale.
        if (identifiers.Count > 0)
            return string.Join(" OR ", identifiers.Distinct(StringComparer.Ordinal));
        var terms = new List<string>();
        terms.AddRange(tokens.Select(token => $"\"{token.Replace("\"", "\"\"")}\""));
        return string.Join(" OR ", terms.Distinct(StringComparer.Ordinal));
    }

    private static string NormalizeSearch(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[\p{L}\p{N}]+(?:[-_.:/][\p{L}\p{N}]+)+(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredIdentifierRegex();

    private static double TokenCoverage(string content, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return 0;
        var normalized = NormalizeSearch(content);
        var matched = tokens.Count(token => Regex.IsMatch(normalized, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(token)}(?![\p{{L}}\p{{N}}])"));
        return (double)matched / tokens.Count;
    }

    private static bool IsRecallQuery(IReadOnlyList<string> tokens) => tokens.Any(token =>
        token.StartsWith("record", StringComparison.Ordinal) || token.StartsWith("repet", StringComparison.Ordinal) ||
        token.StartsWith("devolv", StringComparison.Ordinal) || token.StartsWith("recuper", StringComparison.Ordinal) ||
        token is "antes" or "anterior" or "anteriores" or "hicimos" or "hice" or "hiciste" or "historial");

    private static double RecallUtility(MemoryAtom atom)
    {
        var (user, assistant) = SplitTurn(atom.Content);
        var substance = Math.Clamp(assistant.Length / 4_000d, 0, 1);
        var userTokens = TokenizeSearch(user);
        var isRecallOfAnotherTurn = IsRecallQuery(userTokens) || userTokens.Any(token =>
            token.StartsWith("pedi", StringComparison.Ordinal) || token.StartsWith("pediste", StringComparison.Ordinal) ||
            token.StartsWith("hablam", StringComparison.Ordinal) || token.StartsWith("trabaj", StringComparison.Ordinal));
        return (0.35d + 1.35d * substance) * (isRecallOfAnotherTurn ? 0.55d : 1d);
    }

    private static double DerivedRecallPenalty(MemoryAtom atom)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(atom.MetadataJson);
            if (metadata is null) return 1d;
            if (metadata.TryGetValue("memory.recalledVersionIds", out var recalled) && !string.IsNullOrWhiteSpace(recalled))
                return 0.72d;
            if (metadata.TryGetValue("memory.kind", out var kind) && string.Equals(kind, "summary", StringComparison.OrdinalIgnoreCase))
                return 0.78d;
            if (metadata.ContainsKey("summary.origin_version")) return 0.78d;
            return 1d;
        }
        catch (JsonException) { return 1d; }
    }

    private static double WorkspaceAffinity(MemoryAtom atom, string? preferredWorkspace)
    {
        if (string.IsNullOrWhiteSpace(preferredWorkspace)) return 1d;
        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(atom.MetadataJson);
            if (metadata is not null && metadata.TryGetValue("workspace", out var workspace) &&
                string.Equals(workspace, preferredWorkspace, StringComparison.OrdinalIgnoreCase)) return 1.12d;
        }
        catch (JsonException) { }
        return 1d;
    }

    private static (string User, string Assistant) SplitTurn(string content)
    {
        var separator = content.IndexOf(TurnSeparator, StringComparison.Ordinal);
        if (separator < 0) return (content, string.Empty);
        var user = content[..separator];
        if (user.StartsWith("User request:\n", StringComparison.Ordinal)) user = user["User request:\n".Length..];
        return (user, content[(separator + TurnSeparator.Length)..]);
    }

    public async Task<int> BackfillTurnIndexBatchAsync(int batchSize = 500, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await BackfillTurnIndexBatchAsync(connection, Math.Clamp(batchSize, 1, 5_000), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static async Task<int> BackfillTurnIndexBatchAsync(SqliteConnection connection, int batchSize, CancellationToken cancellationToken)
    {
        var missing = new List<(string Id, string Content, string Project)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT a.version_id,a.content,COALESCE(a.project,'') FROM memory_atoms a
                LEFT JOIN memory_turn_indexed i ON i.version_id=a.version_id
                WHERE i.version_id IS NULL ORDER BY a.sequence LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", batchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                missing.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        if (missing.Count == 0) return 0;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in missing)
        {
            var (user, assistant) = SplitTurn(item.Content);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO memory_turn_fts(version_id,user_content,assistant_content,project) VALUES($id,$user,$assistant,$project)",
                [("$id", item.Id), ("$user", user), ("$assistant", assistant), ("$project", item.Project)], cancellationToken);
            await ExecuteInTransactionAsync(connection, (SqliteTransaction)transaction,
                "INSERT INTO memory_turn_indexed(version_id) VALUES($id)", [("$id", item.Id)], cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return missing.Count;
    }

    public async Task<int> ProjectPendingKnowledgeAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var pending = new List<KnowledgeProjectionInput>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT a.version_id,a.logical_id,a.content,a.project,a.source,a.metadata_json,
                           e.source_uri,e.author,e.claim_key,e.supersedes_version_id,a.occurred_at
                    FROM memory_atoms a
                    LEFT JOIN memory_evidence e ON e.version_id=a.version_id
                    LEFT JOIN knowledge_projection_state p ON p.version_id=a.version_id
                    WHERE p.version_id IS NULL OR p.projector_version<>$projector OR p.status<>'complete'
                    ORDER BY a.sequence LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$projector", DeterministicKnowledgeExtractor.Version);
                command.Parameters.AddWithValue("$limit", Math.Clamp(batchSize, 1, 5_000));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pending.Add(new KnowledgeProjectionInput(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3),
                        NullableString(reader, 4), reader.GetString(5), NullableString(reader, 6),
                        NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9),
                        DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
            }
            if (pending.Count == 0) return 0;

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var item in pending)
            {
                await ExecuteInTransactionAsync(connection, transaction,
                    "DELETE FROM knowledge_edges WHERE source_version_id=$version",
                    [("$version", item.VersionId)], cancellationToken);
                await ExecuteInTransactionAsync(connection, transaction,
                    "DELETE FROM knowledge_mentions WHERE version_id=$version",
                    [("$version", item.VersionId)], cancellationToken);
                await ExecuteInTransactionAsync(connection, transaction,
                    "DELETE FROM knowledge_projection_state WHERE version_id=$version",
                    [("$version", item.VersionId)], cancellationToken);

                var projection = DeterministicKnowledgeExtractor.Extract(item);
                var projectedAt = DateTimeOffset.UtcNow.ToString("O");
                foreach (var entity in projection.Entities)
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO knowledge_entities(entity_id,entity_type,label,normalized_label,first_seen_version_id,last_seen_version_id,created_at)
                        VALUES($id,$type,$label,$normalized,$version,$version,$created)
                        ON CONFLICT(entity_id) DO UPDATE SET last_seen_version_id=excluded.last_seen_version_id
                        """, [("$id", entity.EntityId), ("$type", entity.EntityType), ("$label", entity.Label),
                               ("$normalized", NormalizeKnowledgeLabel(entity.Label)), ("$version", item.VersionId),
                               ("$created", projectedAt)], cancellationToken);

                foreach (var mention in projection.Mentions)
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO knowledge_mentions(mention_id,version_id,entity_id,role,evidence_class,confidence,start_offset,end_offset)
                        VALUES($id,$version,$entity,$role,$evidence,$confidence,$start,$end)
                        """, [("$id", mention.MentionId), ("$version", mention.VersionId), ("$entity", mention.EntityId),
                               ("$role", mention.Role), ("$evidence", mention.EvidenceClass), ("$confidence", mention.Confidence),
                               ("$start", (object?)mention.StartOffset ?? DBNull.Value), ("$end", (object?)mention.EndOffset ?? DBNull.Value)],
                        cancellationToken);

                foreach (var relation in projection.Relations)
                    await ExecuteInTransactionAsync(connection, transaction, """
                        INSERT INTO knowledge_edges(relation_id,from_entity_id,to_entity_id,relation_type,evidence_class,confidence,source_version_id,created_at)
                        VALUES($id,$from,$to,$type,$evidence,$confidence,$version,$created)
                        """, [("$id", relation.RelationId), ("$from", relation.FromEntityId), ("$to", relation.ToEntityId),
                               ("$type", relation.RelationType), ("$evidence", relation.EvidenceClass),
                               ("$confidence", relation.Confidence), ("$version", relation.SourceVersionId),
                               ("$created", projectedAt)], cancellationToken);

                await ExecuteInTransactionAsync(connection, transaction, """
                    INSERT INTO knowledge_projection_state(version_id,projector_version,status,projected_at,error)
                    VALUES($version,$projector,'complete',$projected,NULL)
                    """, [("$version", item.VersionId), ("$projector", DeterministicKnowledgeExtractor.Version),
                           ("$projected", projectedAt)], cancellationToken);
            }
            await ExecuteInTransactionAsync(connection, transaction, """
                DELETE FROM knowledge_entities
                WHERE NOT EXISTS (SELECT 1 FROM knowledge_mentions m WHERE m.entity_id=knowledge_entities.entity_id)
                  AND NOT EXISTS (SELECT 1 FROM knowledge_edges e WHERE e.from_entity_id=knowledge_entities.entity_id OR e.to_entity_id=knowledge_entities.entity_id)
                """, [], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return pending.Count;
        }
        finally { _gate.Release(); }
    }

    public async Task<KnowledgeProjectionStatus> GetKnowledgeProjectionStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var atoms = await ScalarAsync(connection, "SELECT count(*) FROM memory_atoms", cancellationToken);
        var projected = await ScalarWithParameterAsync(connection,
            "SELECT count(*) FROM knowledge_projection_state WHERE projector_version=$version AND status='complete'",
            "$version", DeterministicKnowledgeExtractor.Version, cancellationToken);
        var failed = await ScalarWithParameterAsync(connection,
            "SELECT count(*) FROM knowledge_projection_state WHERE projector_version=$version AND status='failed'",
            "$version", DeterministicKnowledgeExtractor.Version, cancellationToken);
        var entities = await ScalarAsync(connection, "SELECT count(*) FROM knowledge_entities", cancellationToken);
        var relations = await ScalarAsync(connection, "SELECT count(*) FROM knowledge_edges", cancellationToken);
        var pending = Math.Max(0, atoms - projected);
        var status = failed > 0 ? "degraded" : pending > 0 ? "catching_up" : "ready";
        return new KnowledgeProjectionStatus(status, DeterministicKnowledgeExtractor.Version, atoms, projected, pending,
            failed, entities, relations);
    }

    public async Task<KnowledgeProjectionSnapshot?> GetKnowledgeProjectionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        await using var connection = await OpenAsync(cancellationToken);
        string? projectorVersion = null;
        DateTimeOffset projectedAt = default;
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = "SELECT projector_version,projected_at FROM knowledge_projection_state WHERE version_id=$version AND status='complete'";
            state.Parameters.AddWithValue("$version", versionId);
            await using var reader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            projectorVersion = reader.GetString(0);
            projectedAt = DateTimeOffset.Parse(reader.GetString(1));
        }

        var entities = new List<KnowledgeEntity>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT e.entity_id,e.entity_type,e.label
                FROM knowledge_entities e
                WHERE e.entity_id IN (SELECT entity_id FROM knowledge_mentions WHERE version_id=$version)
                   OR e.entity_id IN (SELECT from_entity_id FROM knowledge_edges WHERE source_version_id=$version)
                   OR e.entity_id IN (SELECT to_entity_id FROM knowledge_edges WHERE source_version_id=$version)
                ORDER BY e.entity_type,e.label
                """;
            command.Parameters.AddWithValue("$version", versionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entities.Add(new KnowledgeEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var relations = new List<KnowledgeRelation>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT relation_id,from_entity_id,to_entity_id,relation_type,evidence_class,confidence,source_version_id
                FROM knowledge_edges WHERE source_version_id=$version ORDER BY relation_type,relation_id
                """;
            command.Parameters.AddWithValue("$version", versionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                relations.Add(new KnowledgeRelation(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetString(6)));
        }
        return new KnowledgeProjectionSnapshot(versionId, projectorVersion!, projectedAt, entities, relations);
    }

    public async Task RebuildKnowledgeProjectionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM knowledge_edges", [], cancellationToken);
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM knowledge_mentions", [], cancellationToken);
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM knowledge_entities", [], cancellationToken);
            await ExecuteInTransactionAsync(connection, transaction, "DELETE FROM knowledge_projection_state", [], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<MemoryScaleStatus> GetScaleStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var atoms = await ScalarAsync(connection, "SELECT count(*) FROM memory_atoms", cancellationToken);
        var fts = await ScalarAsync(connection, "SELECT count(*) FROM memory_fts", cancellationToken);
        var pages = await PragmaScalarAsync(connection, "page_count", cancellationToken);
        var freePages = await PragmaScalarAsync(connection, "freelist_count", cancellationToken);
        var projected = await ScalarWithParameterAsync(connection,
            "SELECT count(*) FROM knowledge_projection_state WHERE projector_version=$version AND status='complete'",
            "$version", DeterministicKnowledgeExtractor.Version, cancellationToken);
        var pending = Math.Max(0, atoms - projected);
        var databaseBytes = File.Exists(layout.DatabasePath) ? new FileInfo(layout.DatabasePath).Length : 0;
        var walPath = layout.DatabasePath + "-wal";
        var walBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        var semanticWindow = Math.Min(_recentSemanticCandidateLimit, atoms > int.MaxValue ? int.MaxValue : (int)atoms);
        var semanticCoverage = atoms == 0 ? 1d : Math.Round(Math.Min(1d, semanticWindow / (double)atoms), 4);
        var annRecommended = atoms >= Math.Max(100_000, _recentSemanticCandidateLimit * 20L);
        var status = fts != atoms ? "degraded" : pending > 0 ? "catching_up" : annRecommended ? "ann_evaluation_recommended" : "ready";
        return new MemoryScaleStatus(status, atoms, databaseBytes, walBytes, pages, freePages, fts == atoms,
            semanticWindow, semanticCoverage, pending, annRecommended);
    }

    public async Task RunScaleMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);", cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<OperationalDiagnostics> GetOperationalDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var atoms = await ScalarAsync(connection, "SELECT count(*) FROM memory_atoms", cancellationToken);
        var vectors = await ScalarAsync(connection, "SELECT count(*) FROM memory_vectors", cancellationToken);
        var audits = await ScalarAsync(connection, "SELECT count(*) FROM audit_log", cancellationToken);
        var fts = await ScalarAsync(connection, "SELECT count(*) FROM memory_fts", cancellationToken);
        var turns = await ScalarAsync(connection, "SELECT count(*) FROM memory_turn_indexed", cancellationToken);
        var projected = await ScalarWithParameterAsync(connection,
            "SELECT count(*) FROM knowledge_projection_state WHERE projector_version=$version AND status='complete'",
            "$version", DeterministicKnowledgeExtractor.Version, cancellationToken);
        var failed = await ScalarWithParameterAsync(connection,
            "SELECT count(*) FROM knowledge_projection_state WHERE projector_version=$version AND status='failed'",
            "$version", DeterministicKnowledgeExtractor.Version, cancellationToken);
        var entities = await ScalarAsync(connection, "SELECT count(*) FROM knowledge_entities", cancellationToken);
        var relations = await ScalarAsync(connection, "SELECT count(*) FROM knowledge_edges", cancellationToken);
        var problems = new List<string>();
        if (atoms != vectors) problems.Add($"Atom/vector count mismatch: {atoms}/{vectors}.");
        if (atoms != audits) problems.Add($"Atom/audit count mismatch: {atoms}/{audits}.");
        if (atoms != fts) problems.Add($"Atom/FTS count mismatch: {atoms}/{fts}.");
        if (turns > atoms) problems.Add($"Turn index exceeds atom count: {turns}/{atoms}.");
        if (projected > atoms) problems.Add($"Knowledge projection exceeds atom count: {projected}/{atoms}.");
        if (failed > 0) problems.Add($"Knowledge projection failures: {failed}.");

        long? lastSequence = null;
        DateTimeOffset? lastOccurred = null;
        DateTimeOffset? lastStored = null;
        await using (var latest = connection.CreateCommand())
        {
            latest.CommandText = "SELECT sequence,occurred_at,stored_at FROM memory_atoms ORDER BY sequence DESC LIMIT 1";
            await using var reader = await latest.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                lastSequence = reader.GetInt64(0);
                lastOccurred = DateTimeOffset.Parse(reader.GetString(1));
                lastStored = DateTimeOffset.Parse(reader.GetString(2));
            }
        }
        var databaseBytes = File.Exists(layout.DatabasePath) ? new FileInfo(layout.DatabasePath).Length : 0;
        var walPath = layout.DatabasePath + "-wal";
        var walBytes = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        var turnPending = Math.Max(0, atoms - turns);
        var knowledgePending = Math.Max(0, atoms - projected);
        var status = problems.Count > 0 ? "degraded" : turnPending > 0 || knowledgePending > 0 ? "catching_up" : "ready";
        return new OperationalDiagnostics(status, atoms, vectors, audits, fts, turns, turnPending, projected,
            knowledgePending, failed, entities, relations, lastSequence, lastOccurred, lastStored,
            databaseBytes, walBytes, problems);
    }

    private static async Task<long> PragmaScalarAsync(SqliteConnection connection, string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<long> ScalarWithParameterAsync(SqliteConnection connection, string sql, string parameter,
        object value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string NormalizeKnowledgeLabel(string value) => string.Join(' ', Regex.Matches(
        value.Normalize(NormalizationForm.FormKD).ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(match => match.Value));

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

        var hasPrimarySource = !string.IsNullOrWhiteSpace(candidate.Atom.SourceUri) &&
            !candidate.Atom.SourceUri.StartsWith("hermes://", StringComparison.OrdinalIgnoreCase);
        var isConversation = string.Equals(candidate.Atom.Source, "hermes-auto", StringComparison.OrdinalIgnoreCase);
        var confidence = candidate.Atom.StatedConfidence ?? (hasPrimarySource ? 0.9 :
            !string.IsNullOrWhiteSpace(candidate.Atom.Source) ? 0.72 : 0.55);
        confidence *= 0.65 + 0.35 * Math.Clamp(score, 0, 1);
        if (contradictions.Count > 0) confidence *= 0.7;
        if (superseded) confidence *= 0.65;
        confidence = Math.Round(Math.Clamp(confidence, 0, 1), 3);

        var status = contradictions.Count > 0 ? "contradictory" : superseded ? "possibly_obsolete" :
            hasPrimarySource ? "source_confirmed" : isConversation ? "conversation_record" : "stored_context";
        var label = candidate.Atom.SourceTitle ?? candidate.Atom.Source ?? candidate.Atom.Project ?? "HyperMemory record";
        var citation = new MemoryCitation(candidate.Atom.VersionId, label, candidate.Atom.SourceUri,
            candidate.Atom.OccurredAt, candidate.Atom.ContentHash);
        var evidence = new MemoryEvidence(status, confidence, hasPrimarySource, superseded, contradictions);
        var knowledge = candidate.Knowledge > 0
            ? new KnowledgeRetrievalEvidence(Math.Round(candidate.Knowledge, 3), candidate.KnowledgeReasons ?? [])
            : null;
        return new MemoryHit(candidate.Atom, score, candidate.Text, candidate.Semantic, citation, evidence, knowledge);
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
        if (File.Exists(path))
        {
            await VerifyExistingEnvelopeAsync(path, envelope, cancellationToken);
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
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                await VerifyExistingEnvelopeAsync(path, envelope, cancellationToken);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task VerifyExistingEnvelopeAsync(string path, ArchivedEvent envelope, CancellationToken cancellationToken)
    {
        try
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
        catch (JsonException error)
        {
            throw new InvalidOperationException($"Immutable archive is incomplete or corrupt for event '{envelope.VersionId}'.", error);
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
    private sealed record Candidate(MemoryAtom Atom, double Text, double Semantic, double Knowledge = 0,
        IReadOnlyList<string>? KnowledgeReasons = null);
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
