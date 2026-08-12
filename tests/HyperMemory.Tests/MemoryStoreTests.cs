using HyperMemory.Core;
using HyperMemory.Infrastructure;

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

    private static StorageLayout NewLayout() => StorageLayout.Create(
        Path.Combine(Path.GetTempPath(), "HyperMemoryTests", Guid.NewGuid().ToString("N")));

    private static EmbeddingVector Vector(params float[] values) => new(values, "test", "fixed-v1");
}
