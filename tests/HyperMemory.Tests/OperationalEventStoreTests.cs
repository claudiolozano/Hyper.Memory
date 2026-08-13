using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class OperationalEventStoreTests
{
    [Fact]
    public async Task Disabled_journal_preserves_legacy_schema_and_creates_no_operational_tables()
    {
        var layout = NewLayout();
        await using var store = new SqliteMemoryStore(layout);
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();
        Assert.Equal("4", await ScalarStringAsync(connection,
            "SELECT value FROM memory_schema WHERE key='version'"));
        Assert.Equal("0", await ScalarStringAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('schema_migrations','operational_events')"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(new OperationalEventQuery("workspace-1")));
    }

    [Fact]
    public async Task Enabled_journal_applies_additive_idempotent_migration()
    {
        var layout = NewLayout();
        await using var store = NewEnabledStore(layout);

        await store.InitializeAsync();
        await store.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();
        Assert.Equal("5", await ScalarStringAsync(connection,
            "SELECT value FROM memory_schema WHERE key='version'"));
        Assert.Equal("2", await ScalarStringAsync(connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE version IN (4,5)"));
        Assert.Equal("0", await ScalarStringAsync(connection, "SELECT COUNT(*) FROM operational_events"));
    }

    [Fact]
    public async Task Enabling_journal_preserves_existing_legacy_memory()
    {
        var layout = NewLayout();
        await using (var legacy = new SqliteMemoryStore(layout))
        {
            await legacy.InitializeAsync();
            await legacy.AppendAsync(new MemoryWriteRequest("Legacy memory one", EventId: "legacy-1"), Vector(1, 0));
            await legacy.AppendAsync(new MemoryWriteRequest("Legacy memory two", EventId: "legacy-2"), Vector(0, 1));
        }

        await using var upgraded = NewEnabledStore(layout);
        await upgraded.InitializeAsync();

        var integrity = await upgraded.VerifyIntegrityAsync();
        var hits = await upgraded.QueryAsync(new MemoryQuery("Legacy memory", Limit: 10), Vector(1, 0));
        Assert.True(integrity.IsValid, string.Join(Environment.NewLine, integrity.Problems));
        Assert.Equal(2, integrity.AtomCount);
        Assert.Equal(2, hits.Count);
        Assert.Empty(await upgraded.ReadAsync(new OperationalEventQuery("workspace-1")));
    }

    [Fact]
    public async Task Feature_flag_rollback_keeps_legacy_memory_and_operational_history_intact()
    {
        var layout = NewLayout();
        await using (var enabled = NewEnabledStore(layout))
        {
            await enabled.InitializeAsync();
            await enabled.AppendAsync(new MemoryWriteRequest("Legacy-compatible memory", EventId: "legacy-rollback"), Vector(1, 0));
            await enabled.AppendAsync(new OperationalEventWriteRequest(
                "task.created", new OperationalObjectRef(OperationalObjectTypes.Task, "task-rollback"),
                new OperationalScope("workspace-rollback", "project-rollback"),
                "{\"taskId\":\"task-rollback\",\"title\":\"Rollback\",\"status\":\"active\"}",
                EventId: "operational-before-rollback"));
        }

        await using (var disabled = new SqliteMemoryStore(layout))
        {
            await disabled.InitializeAsync();
            var hits = await disabled.QueryAsync(new MemoryQuery("Legacy-compatible", Limit: 5), Vector(1, 0));
            Assert.Single(hits);
            await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.ReadAsync(
                new OperationalEventQuery("workspace-rollback")));
        }

        await using var reenabled = NewEnabledStore(layout);
        await reenabled.InitializeAsync();
        var history = await reenabled.ReadAsync(new OperationalEventQuery("workspace-rollback"));
        Assert.Single(history);
        Assert.Equal("operational-before-rollback", history[0].EventId);
    }

    [Fact]
    public async Task Operational_append_is_idempotent_versioned_scoped_and_archived()
    {
        var layout = NewLayout();
        await using var store = NewEnabledStore(layout);
        await store.InitializeAsync();
        var scope = new OperationalScope("workspace-1", "project-1", "session-1", "agent-1", "task-1");
        var firstRequest = new OperationalEventWriteRequest(
            "artifact.observed",
            new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-1"),
            scope,
            "{\"hash\":\"abc\"}",
            EventId: "operational-event-1",
            ExpectedRevision: 0,
            Metadata: new Dictionary<string, string> { ["source"] = "test" });

        var first = await store.AppendAsync(firstRequest);
        var retry = await store.AppendAsync(firstRequest);
        var second = await store.AppendAsync(firstRequest with
        {
            EventId = "operational-event-2",
            EventType = "artifact.changed",
            DataJson = "{\"hash\":\"def\"}",
            ExpectedRevision = 1
        });

        Assert.True(first.Created);
        Assert.False(retry.Created);
        Assert.Equal(first.Sequence, retry.Sequence);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        var events = await store.ReadAsync(new OperationalEventQuery("workspace-1", ProjectId: "project-1"));
        Assert.Equal(2, events.Count);
        Assert.Equal(["operational-event-1", "operational-event-2"], events.Select(item => item.EventId));
        Assert.All(events, item => Assert.Equal("artifact-1", item.Subject.ObjectId));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(layout.Root, "operational-events"), "*.json",
            SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Operational_append_rejects_mutation_revision_conflicts_and_invalid_json()
    {
        await using var store = NewEnabledStore(NewLayout());
        await store.InitializeAsync();
        var request = new OperationalEventWriteRequest(
            "task.created",
            new OperationalObjectRef(OperationalObjectTypes.Task, "task-1"),
            new OperationalScope("workspace-1"),
            "{\"title\":\"Build\"}",
            EventId: "event-1",
            ExpectedRevision: 0);
        await store.AppendAsync(request);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(request with { DataJson = "{\"title\":\"Mutated\"}" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(request with { EventId = "event-2", ExpectedRevision = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendAsync(request with { EventId = "event-3", DataJson = "not-json", ExpectedRevision = 1 }));
    }

    [Fact]
    public async Task Concurrent_store_instances_serialize_revisions_without_loss()
    {
        var layout = NewLayout();
        await using var first = NewEnabledStore(layout);
        await using var second = NewEnabledStore(layout);
        await first.InitializeAsync();
        await second.InitializeAsync();
        var scope = new OperationalScope("workspace-1", "project-1");
        var writes = Enumerable.Range(0, 40).Select(index =>
            (index % 2 == 0 ? first : second).AppendAsync(new OperationalEventWriteRequest(
                "task.updated", new OperationalObjectRef(OperationalObjectTypes.Task, "shared-task"), scope,
                $"{{\"index\":{index}}}", EventId: $"concurrent-{index:D2}")));

        var results = await Task.WhenAll(writes);
        var stored = await first.ReadAsync(new OperationalEventQuery(
            "workspace-1", ProjectId: "project-1", ObjectType: OperationalObjectTypes.Task,
            ObjectId: "shared-task", Limit: 100));

        Assert.Equal(40, results.Length);
        Assert.Equal(40, stored.Count);
        Assert.Equal(Enumerable.Range(1, 40).Select(value => (long)value), stored.Select(item => item.Revision));
        Assert.Equal(40, stored.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Optimistic_revision_allows_only_one_concurrent_writer()
    {
        var layout = NewLayout();
        await using var first = NewEnabledStore(layout);
        await using var second = NewEnabledStore(layout);
        await first.InitializeAsync();
        await second.InitializeAsync();
        var scope = new OperationalScope("workspace-1");
        async Task<bool> TryWrite(SqliteMemoryStore store, string id)
        {
            try
            {
                await store.AppendAsync(new OperationalEventWriteRequest(
                    "task.created", new OperationalObjectRef("task", "task-1"), scope,
                    "{}", EventId: id, ExpectedRevision: 0));
                return true;
            }
            catch (InvalidOperationException error) when (error.Message.Contains("revision conflict", StringComparison.Ordinal))
            {
                return false;
            }
        }

        var outcomes = await Task.WhenAll(TryWrite(first, "writer-1"), TryWrite(second, "writer-2"));
        Assert.Single(outcomes, value => value);
        Assert.Single(await first.ReadAsync(new OperationalEventQuery("workspace-1")));
    }

    private static SqliteMemoryStore NewEnabledStore(StorageLayout layout)
    {
        var options = new HyperMemoryOptions();
        options.Operational.EnableEventJournal = true;
        return new SqliteMemoryStore(layout, Options.Create(options));
    }

    private static StorageLayout NewLayout() => StorageLayout.Create(
        Path.Combine(Path.GetTempPath(), "HyperMemoryOperationalTests", Guid.NewGuid().ToString("N")));

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static EmbeddingVector Vector(params float[] values) => new(values, "test", "fixed-v1");
}
