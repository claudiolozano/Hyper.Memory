using HyperMemory.Core;
using HyperMemory.Infrastructure;
using System.Security.Cryptography;
using System.Text;

namespace HyperMemory.Tests;

public sealed class MemoryStoreTests
{
    [Fact]
    public async Task Append_is_versioned_searchable_and_integral()
    {
        var layout = NewLayout();
        await using var store = new SqliteMemoryStore(layout);
        await store.InitializeAsync();

        var first = await store.AppendAsync(
            new MemoryWriteRequest("SQLite stores the durable authentication decision.", "auth", "event-1", "apollo"),
            Vector(1, 0, 0));
        var second = await store.AppendAsync(
            new MemoryWriteRequest("Authentication now uses rotating refresh tokens.", "auth", "event-2", "apollo"),
            Vector(.9f, .1f, 0));

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.True(second.Sequence > first.Sequence);
        var hits = await store.QueryAsync(new MemoryQuery("authentication tokens", Project: "apollo"), Vector(1, 0, 0));
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, x => x.Atom.VersionId == "event-1");
        Assert.Contains(hits, x => x.Atom.VersionId == "event-2");
        var integrity = await store.VerifyIntegrityAsync();
        Assert.True(integrity.IsValid, string.Join(Environment.NewLine, integrity.Problems));
        Assert.Equal(2, integrity.AtomCount);
    }

    [Fact]
    public async Task Event_retry_is_idempotent_but_mutation_is_rejected()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        var request = new MemoryWriteRequest("Immutable fact", EventId: "stable-event");
        var created = await store.AppendAsync(request, Vector(1, 0));
        var retried = await store.AppendAsync(request, Vector(1, 0));

        Assert.True(created.Created);
        Assert.False(retried.Created);
        Assert.Equal(created.Sequence, retried.Sequence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(request with { Content = "Mutated fact" }, Vector(0, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(request with { SourceUri = "file:///different-source.md" }, Vector(1, 0)));
        Assert.Equal(1, (await store.VerifyIntegrityAsync()).AtomCount);
    }

    [Fact]
    public async Task Restart_preserves_all_versions()
    {
        var layout = NewLayout();
        await using (var firstProcess = new SqliteMemoryStore(layout))
        {
            await firstProcess.InitializeAsync();
            await firstProcess.AppendAsync(new MemoryWriteRequest("Version one", "topic"), Vector(1, 0));
            await firstProcess.AppendAsync(new MemoryWriteRequest("Version two", "topic"), Vector(0, 1));
        }

        await using var restarted = new SqliteMemoryStore(layout);
        await restarted.InitializeAsync();
        var report = await restarted.VerifyIntegrityAsync();
        Assert.True(report.IsValid);
        Assert.Equal(2, report.AtomCount);
        var hits = await restarted.QueryAsync(new MemoryQuery("Version", Limit: 10), Vector(1, 0));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Layout_always_uses_named_folder()
    {
        var parent = Path.Combine(Path.GetTempPath(), "HyperMemoryTests", Guid.NewGuid().ToString("N"));
        var layout = StorageLayout.Create(parent);
        Assert.Equal("Hyper_Memory", Path.GetFileName(layout.Root));
        Assert.StartsWith(Path.GetFullPath(parent), layout.DatabasePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Temporal_provenance_and_contradictions_are_additive()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        await store.AppendAsync(new MemoryWriteRequest(
            "The deployment region is Madrid.", "deployment-region", "region-2025", "atlas", "meeting",
            OccurredAt: new DateTimeOffset(2025, 3, 10, 9, 0, 0, TimeSpan.Zero),
            SourceUri: "file:///decisions/2025-03-10.md", SourceTitle: "Architecture meeting",
            Author: "Team", ValidFrom: new DateTimeOffset(2025, 3, 10, 0, 0, 0, TimeSpan.Zero),
            ClaimKey: "atlas.deployment.region", StatedConfidence: 0.98), Vector(1, 0));
        await store.AppendAsync(new MemoryWriteRequest(
            "The deployment region is Paris.", "deployment-region", "region-2026", "atlas", "decision",
            OccurredAt: new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero),
            SourceUri: "file:///decisions/2026-02-01.md", SourceTitle: "Migration decision",
            ValidFrom: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), SupersedesVersionId: "region-2025",
            ClaimKey: "atlas.deployment.region", StatedConfidence: 0.99), Vector(1, 0));

        var historical = await store.QueryAsync(new MemoryQuery("deployment region", OccurredFrom:
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), OccurredTo:
            new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero)), Vector(1, 0));
        var old = Assert.Single(historical);
        Assert.Equal("region-2025", old.Atom.VersionId);
        Assert.Equal("Architecture meeting", old.Citation?.Label);
        Assert.Equal("contradictory", old.Evidence?.Status);
        Assert.True(old.Evidence?.IsSuperseded);
        Assert.Contains("region-2026", old.Evidence!.Contradicts);

        var current = await store.QueryAsync(new MemoryQuery("deployment region", IncludeSuperseded: false), Vector(1, 0));
        var latest = Assert.Single(current);
        Assert.Equal("region-2026", latest.Atom.VersionId);
        Assert.True(latest.Evidence?.HasPrimarySource);
    }

    [Fact]
    public async Task Initializing_an_empty_legacy_schema_only_adds_new_schema()
    {
        var layout = NewLayout();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={layout.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE memory_atoms (sequence INTEGER PRIMARY KEY AUTOINCREMENT,version_id TEXT NOT NULL UNIQUE,logical_id TEXT NOT NULL,content TEXT NOT NULL,content_hash TEXT NOT NULL,project TEXT NULL,source TEXT NULL,metadata_json TEXT NOT NULL,occurred_at TEXT NOT NULL,stored_at TEXT NOT NULL);
                CREATE VIRTUAL TABLE memory_fts USING fts5(version_id UNINDEXED,content,project);
                CREATE TABLE memory_vectors (version_id TEXT PRIMARY KEY REFERENCES memory_atoms(version_id),provider TEXT NOT NULL,model TEXT NOT NULL,dimensions INTEGER NOT NULL,vector BLOB NOT NULL);
                CREATE TABLE audit_log (audit_sequence INTEGER PRIMARY KEY AUTOINCREMENT,operation TEXT NOT NULL,version_id TEXT NOT NULL,content_hash TEXT NOT NULL,created_at TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteMemoryStore(layout);
        await store.InitializeAsync();
        var written = await store.AppendAsync(new MemoryWriteRequest("Legacy-compatible append", EventId: "after-migration"), Vector(1, 0));
        Assert.True(written.Created);
        Assert.True((await store.VerifyIntegrityAsync()).IsValid);
    }

    [Fact]
    public async Task Historical_recall_prefers_the_substantive_original_over_recent_noise_and_mistakes()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        var paragraphs = string.Join("\n\n", Enumerable.Range(1, 10).Select(index =>
            $"Párrafo {index}: Elías conservaba segundos perdidos en frascos de cristal dentro de su taller."));
        var original = await store.AppendAsync(new MemoryWriteRequest(
            $"User request:\nescríbeme un cuento de 10 párrafos para adultos\n\nHermes response:\n" +
            $"El Coleccionista de Segundos Perdidos\n\n{paragraphs}",
            EventId: "original-story", Project: "hermes", Source: "hermes-auto"), Vector(1, 0, 0));

        for (var index = 0; index < 30; index++)
            await store.AppendAsync(new MemoryWriteRequest(
                $"User request:\ndale con la fase {index} del juego\n\nHermes response:\n" +
                $"Se completó la fase {index} del motor, colisiones, animación, cámara y enemigos del juego.",
                EventId: $"game-{index}", Project: "hermes", Source: "hermes-auto"), Vector(1, 0, 0));

        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\nrepíteme el cuento de 10 párrafos\n\nHermes response:\nNo existe ningún cuento; solo trabajamos en el juego.",
            EventId: "mistaken-recall", Project: "hermes", Source: "hermes-auto",
            Metadata: new Dictionary<string, string> { ["memory.recalledVersionIds"] = "game-29" }), Vector(1, 0, 0));

        var direct = await store.QueryAsync(new MemoryQuery(
            "repíteme el cuento de 10 párrafos", Limit: 5, Project: "hermes"), Vector(1, 0, 0));
        Assert.Equal(original.VersionId, direct[0].Atom.VersionId);
        Assert.Contains("El Coleccionista de Segundos Perdidos", direct[0].Atom.Content);

        var vague = await store.QueryAsync(new MemoryQuery(
            "¿hicimos este año algún cuento?", Limit: 5, Project: "hermes"), Vector(1, 0, 0));
        Assert.Equal(original.VersionId, vague[0].Atom.VersionId);
    }

    [Fact]
    public async Task Structured_identifier_recalls_an_old_record_beyond_the_text_candidate_limit()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        for (var index = 0; index < 100; index++)
            await store.AppendAsync(new MemoryWriteRequest(
                $"User request:\nrecord anchor-{index:D8}\n\nHermes response:\nStored record {index}.",
                EventId: $"anchor-event-{index:D8}", Project: "scale"), Vector(1, 0));

        var hits = await store.QueryAsync(new MemoryQuery("anchor-00000000", 5, "scale"), Vector(1, 0));
        Assert.NotEmpty(hits);
        Assert.Equal("anchor-event-00000000", hits[0].Atom.VersionId);
    }

    [Fact]
    public async Task Lightweight_status_reports_counts_without_full_archive_audit()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        await store.AppendAsync(new MemoryWriteRequest("Status test", EventId: "status-test"), Vector(1, 0));

        var status = await store.GetStatusAsync();
        Assert.Equal("healthy", status.Status);
        Assert.Equal(1, status.AtomCount);
        Assert.Equal(1, status.VectorCount);
        Assert.Equal(1, status.AuditCount);
    }

    [Fact]
    public async Task Incomplete_immutable_envelope_never_creates_a_database_memory()
    {
        var layout = NewLayout();
        await using var store = new SqliteMemoryStore(layout);
        await store.InitializeAsync();
        const string eventId = "interrupted-event";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId)));
        var eventDirectory = Path.Combine(layout.Root, "events", key[..2]);
        Directory.CreateDirectory(eventDirectory);
        await File.WriteAllTextAsync(Path.Combine(eventDirectory, key + ".json"), "{incomplete");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(new MemoryWriteRequest("Must not be partially committed", EventId: eventId), Vector(1, 0)));
        Assert.Equal(0, (await store.GetStatusAsync()).AtomCount);
    }

    [Fact]
    public async Task Knowledge_projection_separates_requests_responses_and_unverified_artifacts()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\nescribe un cuento de diez párrafos\n\nHermes response:\n# El Faro de las Horas\n\nEn la costa había un reloj detenido.",
            EventId: "story-turn", Project: "hermes", Source: "hermes-auto",
            Metadata: new Dictionary<string, string> { ["kind"] = "conversation-turn", ["sessionId"] = "session-42" },
            SourceUri: "hermes://session/session-42", Author: "Hermes Agent"), Vector(1, 0));

        Assert.Equal(1, await store.ProjectPendingKnowledgeAsync());
        var status = await store.GetKnowledgeProjectionStatusAsync();
        Assert.Equal("ready", status.Status);
        Assert.Equal(1, status.ProjectedCount);
        Assert.Equal(0, status.PendingCount);

        var projection = await store.GetKnowledgeProjectionAsync("story-turn");
        Assert.NotNull(projection);
        Assert.Contains(projection.Entities, entity => entity.EntityType == "request");
        Assert.Contains(projection.Entities, entity => entity.EntityType == "response");
        Assert.Contains(projection.Entities, entity => entity.EntityType == "artifact" && entity.Label == "El Faro de las Horas");
        Assert.Contains(projection.Entities, entity => entity.EntityType == "project" && entity.Label == "hermes");
        Assert.Contains(projection.Entities, entity => entity.EntityType == "session" && entity.Label == "session-42");
        var produced = Assert.Single(projection.Relations, relation => relation.RelationType == "PRODUCED_ARTIFACT");
        Assert.Equal("INFERRED", produced.EvidenceClass);
        Assert.DoesNotContain(projection.Relations, relation =>
            relation.RelationType == "PRODUCED_ARTIFACT" && relation.EvidenceClass == "VERIFIED");
    }

    [Fact]
    public async Task Knowledge_projection_can_be_rebuilt_without_changing_immutable_memory()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\ncrea un juego\n\nHermes response:\n## Proyecto Laberinto\n\nHe escrito una propuesta.",
            EventId: "game-turn", Project: "hermes"), Vector(1, 0));
        await store.ProjectPendingKnowledgeAsync();
        var before = await store.GetKnowledgeProjectionStatusAsync();
        var integrityBefore = await store.VerifyIntegrityAsync();

        await store.RebuildKnowledgeProjectionAsync();
        var cleared = await store.GetKnowledgeProjectionStatusAsync();
        Assert.Equal(1, cleared.PendingCount);
        Assert.Equal(0, cleared.EntityCount);
        Assert.Equal(0, cleared.RelationCount);
        Assert.Equal(integrityBefore.AtomCount, (await store.VerifyIntegrityAsync()).AtomCount);

        Assert.Equal(1, await store.ProjectPendingKnowledgeAsync());
        var rebuilt = await store.GetKnowledgeProjectionStatusAsync();
        Assert.Equal(before.EntityCount, rebuilt.EntityCount);
        Assert.Equal(before.RelationCount, rebuilt.RelationCount);
        Assert.NotNull(await store.GetKnowledgeProjectionAsync("game-turn"));
    }

    [Fact]
    public async Task Knowledge_projection_keeps_distinct_artifact_titles_separate()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\nescribe un cuento\n\nHermes response:\n# La Casa de Sal\n\nPrimer relato.",
            EventId: "story-a", Project: "hermes"), Vector(1, 0));
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\nescribe otro cuento\n\nHermes response:\n# El Jardín Inmóvil\n\nSegundo relato.",
            EventId: "story-b", Project: "hermes"), Vector(1, 0));

        Assert.Equal(2, await store.ProjectPendingKnowledgeAsync());
        var first = await store.GetKnowledgeProjectionAsync("story-a");
        var second = await store.GetKnowledgeProjectionAsync("story-b");
        var firstArtifact = Assert.Single(first!.Entities, entity => entity.EntityType == "artifact");
        var secondArtifact = Assert.Single(second!.Entities, entity => entity.EntityType == "artifact");
        Assert.Equal("La Casa de Sal", firstArtifact.Label);
        Assert.Equal("El Jardín Inmóvil", secondArtifact.Label);
        Assert.NotEqual(firstArtifact.EntityId, secondArtifact.EntityId);
    }

    [Fact]
    public async Task Knowledge_projection_records_storage_people_dates_and_corrections_without_inventing_verification()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\nPersona: Ana Torres revisará el cambio el 12/08/2026.\n\nHermes response:\n" +
            "Responsable: Luis Pérez. La corrección queda documentada para 2026-08-13.",
            LogicalId: "release-decision", EventId: "decision-v1", Project: "hermes",
            OccurredAt: new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero),
            ClaimKey: "release.owner"), Vector(1, 0));
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\ncorrige la decisión\n\nHermes response:\nResponsable: Marta Ruiz.",
            LogicalId: "release-decision", EventId: "decision-v2", Project: "hermes",
            OccurredAt: new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero),
            SupersedesVersionId: "decision-v1", ClaimKey: "release.owner"), Vector(1, 0));

        Assert.Equal(2, await store.ProjectPendingKnowledgeAsync());
        var first = await store.GetKnowledgeProjectionAsync("decision-v1");
        Assert.NotNull(first);
        Assert.Contains(first.Entities, entity => entity.EntityType == "person" && entity.Label == "Ana Torres");
        Assert.Contains(first.Entities, entity => entity.EntityType == "person" && entity.Label == "Luis Pérez");
        Assert.Contains(first.Entities, entity => entity.EntityType == "date" && entity.Label == "2026-08-12");
        Assert.Contains(first.Entities, entity => entity.EntityType == "date" && entity.Label == "2026-08-13");
        Assert.Contains(first.Relations, relation => relation.RelationType == "STORED_AS_VERSION" && relation.EvidenceClass == "VERIFIED");

        var correction = await store.GetKnowledgeProjectionAsync("decision-v2");
        Assert.NotNull(correction);
        Assert.Contains(correction.Relations, relation => relation.RelationType == "SUPERSEDES" && relation.EvidenceClass == "EXTRACTED");
        Assert.Contains(correction.Relations, relation => relation.RelationType == "CORRECTS_VERSION" && relation.EvidenceClass == "EXTRACTED");
        Assert.DoesNotContain(correction.Relations, relation => relation.RelationType == "SUPERSEDES" && relation.EvidenceClass == "VERIFIED");
    }

    [Fact]
    public async Task Verified_file_metadata_produces_verified_file_and_hash_relations()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = "conversation-turn",
            ["workspace"] = "D:/work/game",
            ["artifacts.verifiedFiles"] = """[{"path":"src/game.js","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","size":42,"tool":"write_file","verification":"workspace-file-hash"}]"""
        };
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\ncrea el juego\n\nHermes response:\nHe creado el archivo.",
            EventId: "verified-file-turn", Project: "hermes", Metadata: metadata), Vector(1, 0));

        await store.ProjectPendingKnowledgeAsync();
        var projection = await store.GetKnowledgeProjectionAsync("verified-file-turn");
        Assert.NotNull(projection);
        Assert.Contains(projection.Entities, entity => entity.EntityType == "file" && entity.Label == "src/game.js");
        Assert.Contains(projection.Entities, entity => entity.EntityType == "content_hash" && entity.Label.StartsWith("SHA256:"));
        Assert.Contains(projection.Relations, relation => relation.RelationType == "PRODUCED_FILE" && relation.EvidenceClass == "VERIFIED");
        Assert.Contains(projection.Relations, relation => relation.RelationType == "HAS_CONTENT_HASH" && relation.EvidenceClass == "VERIFIED");
    }

    [Fact]
    public async Task Hybrid_query_expands_from_a_text_seed_through_verified_file_knowledge()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        static Dictionary<string, string> FileMetadata(string hash) => new()
        {
            ["kind"] = "conversation-turn",
            ["workspace"] = "D:/work/game",
            ["artifacts.verifiedFiles"] = $"[{{\"path\":\"src/engine.js\",\"sha256\":\"{hash}\",\"size\":42,\"tool\":\"write_file\",\"verification\":\"workspace-file-hash\"}}]"
        };
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\ncrea el motor AlphaNebula\n\nHermes response:\nPrimera implementación.",
            EventId: "engine-origin", Project: "hermes", Metadata: FileMetadata(new string('A', 64))), Vector(1, 0));
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\najusta las colisiones\n\nHermes response:\nCorrección aplicada.",
            EventId: "engine-correction", Project: "hermes", Metadata: FileMetadata(new string('B', 64))), Vector(0, 1));
        await store.ProjectPendingKnowledgeAsync();

        var hits = await store.QueryAsync(new MemoryQuery("AlphaNebula", Limit: 5, Project: "hermes"), Vector(1, 0));
        Assert.True(hits[0].Atom.VersionId == "engine-correction",
            string.Join(" | ", hits.Select(hit => $"{hit.Atom.VersionId}:{hit.Score:F4}/k={hit.Knowledge?.Score:F4}/t={hit.TextScore:F4}/s={hit.SemanticScore:F4}")));
        var correction = Assert.Single(hits, hit => hit.Atom.VersionId == "engine-correction");
        Assert.NotNull(correction.Knowledge);
        Assert.Contains(correction.Knowledge.Reasons, reason => reason == "file:src/engine.js");
        Assert.Equal(0, correction.TextScore);
        Assert.Equal(0, correction.SemanticScore);
    }

    [Fact]
    public async Task Execution_and_targeted_test_evidence_are_projected_without_overstating_scope()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        var outputHash = new string('C', 64);
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = "conversation-turn",
            ["workspace"] = "D:/work/game",
            ["execution.events"] = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new
                {
                    command = "dotnet test tests/Game.Tests.csproj", exitCode = 0, status = "succeeded",
                    workdir = ".", outputSha256 = outputHash, privacyRedactions = 0,
                    verification = new { status = "passed", kind = "test", scope = "targeted", canonicalCommand = "dotnet test" }
                }
            })
        };
        await store.AppendAsync(new MemoryWriteRequest(
            "User request:\nejecuta las pruebas del juego\n\nHermes response:\nLas pruebas pasaron.",
            EventId: "test-evidence-turn", Project: "hermes", Metadata: metadata), Vector(1, 0));
        await store.ProjectPendingKnowledgeAsync();

        var projection = await store.GetKnowledgeProjectionAsync("test-evidence-turn");
        Assert.NotNull(projection);
        Assert.Contains(projection.Entities, entity => entity.EntityType == "execution" && entity.Label.StartsWith("succeeded:"));
        Assert.Contains(projection.Entities, entity => entity.EntityType == "verification" && entity.Label.Contains("passed targeted test"));
        Assert.Contains(projection.Relations, relation => relation.RelationType == "EXECUTION_SUCCEEDED" && relation.EvidenceClass == "VERIFIED");
        Assert.Contains(projection.Relations, relation => relation.RelationType == "PASSED_CHECK" && relation.EvidenceClass == "VERIFIED");
        Assert.DoesNotContain(projection.Entities, entity => entity.Label.Contains("full test"));
    }

    [Fact]
    public async Task Scale_diagnostics_and_non_destructive_maintenance_preserve_memory()
    {
        await using var store = new SqliteMemoryStore(NewLayout());
        await store.InitializeAsync();
        for (var index = 0; index < 3; index++)
            await store.AppendAsync(new MemoryWriteRequest($"Scale record {index}", EventId: $"scale-{index}"), Vector(1, 0));

        var catchingUp = await store.GetScaleStatusAsync();
        Assert.Equal("catching_up", catchingUp.Status);
        Assert.Equal(3, catchingUp.KnowledgePendingCount);
        Assert.True(catchingUp.FullTextCoversAllHistory);
        Assert.True(catchingUp.DatabaseBytes > 0);
        var diagnosticsBefore = await store.GetOperationalDiagnosticsAsync();
        Assert.Equal("catching_up", diagnosticsBefore.Status);
        Assert.Equal(3, diagnosticsBefore.KnowledgePendingCount);
        Assert.Empty(diagnosticsBefore.Problems);

        await store.ProjectPendingKnowledgeAsync();
        await store.RunScaleMaintenanceAsync();
        var ready = await store.GetScaleStatusAsync();
        Assert.Equal("ready", ready.Status);
        Assert.Equal(0, ready.KnowledgePendingCount);
        Assert.Equal(1, ready.EstimatedSemanticCoverage);
        Assert.False(ready.AnnEvaluationRecommended);
        var diagnostics = await store.GetOperationalDiagnosticsAsync();
        Assert.Equal("ready", diagnostics.Status);
        Assert.Equal(3, diagnostics.LastSequence);
        Assert.NotNull(diagnostics.LastStoredAt);
        Assert.Equal(0, diagnostics.TurnIndexPendingCount);
        Assert.Equal(0, diagnostics.KnowledgePendingCount);
        Assert.Empty(diagnostics.Problems);
        Assert.True((await store.VerifyIntegrityAsync()).IsValid);
    }

    private static StorageLayout NewLayout() => StorageLayout.Create(
        Path.Combine(Path.GetTempPath(), "HyperMemoryTests", Guid.NewGuid().ToString("N")));

    private static EmbeddingVector Vector(params float[] values) => new(values, "test", "fixed-v1");
}
