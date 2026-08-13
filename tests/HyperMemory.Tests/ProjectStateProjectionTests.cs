using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class ProjectStateProjectionTests
{
    [Fact]
    public void Project_state_cannot_be_enabled_without_its_event_journal()
    {
        var options = new HyperMemoryOptions();
        options.Operational.EnableProjectState = true;

        Assert.Throws<InvalidOperationException>(() =>
            new SqliteMemoryStore(NewLayout(), Options.Create(options)));
    }

    [Fact]
    public async Task Project_state_migration_is_additive_and_reaches_schema_six()
    {
        var layout = NewLayout();
        var options = EnabledOptions();
        await using var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();

        await using var connection = new SqliteConnection($"Data Source={layout.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM memory_schema WHERE key='version'";
        Assert.Equal("6", Convert.ToString(await command.ExecuteScalarAsync()));
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version IN (4,5,6)";
        Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Projection_reduces_project_events_and_is_rebuildable()
    {
        var layout = NewLayout();
        var options = EnabledOptions();
        await using var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();
        using var projection = new SqliteProjectStateProjectionStore(layout, events, options);
        var scope = new OperationalScope("workspace-1", "project-1", "session-a", "agent-a", "task-1");

        await AppendAsync(events, scope, "artifact.observed", OperationalObjectTypes.Artifact, "artifact-1",
            new ArtifactStateChange("artifact-1", "src/game.cs", "source", "hash-1", "rev-1", true));
        await AppendAsync(events, scope, "task.created", OperationalObjectTypes.Task, "task-1",
            new TaskStateChange("task-1", "Build game", "active"));
        await AppendAsync(events, scope, "validation.recorded", OperationalObjectTypes.Validation, "validation-1",
            new ValidationStateChange("validation-1",
                new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-1"),
                "test-validator", ValidationStatus.Pass, "{\"scope\":\"targeted\"}", ["evidence-1"]));
        await AppendAsync(events, scope, "artifact.changed", OperationalObjectTypes.Artifact, "artifact-1",
            new ArtifactStateChange("artifact-1", "src/game.cs", "source", "hash-2", "rev-2", true));
        await AppendAsync(events, scope, "task.completed", OperationalObjectTypes.Task, "task-1",
            new TaskStateChange("task-1", "Build game", "active", RequiredEvidenceIds: ["evidence-1"]));

        Assert.Equal(2, await projection.ProjectPendingAsync(scope, 2));
        Assert.Equal(2, await projection.ProjectPendingAsync(scope, 2));
        Assert.Equal(1, await projection.ProjectPendingAsync(scope, 2));
        Assert.Equal(0, await projection.ProjectPendingAsync(scope, 2));
        var before = await projection.GetCurrentAsync(scope);

        Assert.NotNull(before);
        Assert.Single(before.Artifacts);
        Assert.Equal("hash-2", before.Artifacts[0].ContentHash);
        Assert.Single(before.Tasks);
        Assert.Equal("completed", before.Tasks[0].Status);
        Assert.Single(before.Validations);
        Assert.Equal(ValidationStatus.Pass, before.Validations[0].Status);

        await projection.RebuildAsync(scope);
        var rebuilt = await projection.GetCurrentAsync(scope);
        Assert.NotNull(rebuilt);
        Assert.Equal(before.ThroughSequence, rebuilt.ThroughSequence);
        Assert.Equal(JsonSerializer.Serialize(before.Artifacts), JsonSerializer.Serialize(rebuilt.Artifacts));
        Assert.Equal(JsonSerializer.Serialize(before.Tasks), JsonSerializer.Serialize(rebuilt.Tasks));
        Assert.Equal(JsonSerializer.Serialize(before.Validations), JsonSerializer.Serialize(rebuilt.Validations));
    }

    [Fact]
    public async Task Projection_isolated_by_workspace_and_project()
    {
        var layout = NewLayout();
        var options = EnabledOptions();
        await using var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();
        using var projection = new SqliteProjectStateProjectionStore(layout, events, options);
        var first = new OperationalScope("workspace-1", "project-a");
        var second = new OperationalScope("workspace-1", "project-b");
        await AppendAsync(events, first, "task.created", OperationalObjectTypes.Task, "task-a",
            new TaskStateChange("task-a", "Project A", "active"));
        await AppendAsync(events, second, "task.created", OperationalObjectTypes.Task, "task-b",
            new TaskStateChange("task-b", "Project B", "active"));

        Assert.Equal(1, await projection.ProjectPendingAsync(first));
        Assert.Equal(1, await projection.ProjectPendingAsync(second));
        Assert.Equal("task-a", (await projection.GetCurrentAsync(first))!.Tasks.Single().TaskId);
        Assert.Equal("task-b", (await projection.GetCurrentAsync(second))!.Tasks.Single().TaskId);
    }

    private static async Task AppendAsync<T>(
        SqliteMemoryStore store,
        OperationalScope scope,
        string eventType,
        string objectType,
        string objectId,
        T data)
    {
        await store.AppendAsync(new OperationalEventWriteRequest(
            eventType,
            new OperationalObjectRef(objectType, objectId),
            scope,
            JsonSerializer.Serialize(data),
            EventId: Guid.NewGuid().ToString("N")));
    }

    private static IOptions<HyperMemoryOptions> EnabledOptions()
    {
        var options = new HyperMemoryOptions();
        options.Operational.EnableEventJournal = true;
        options.Operational.EnableProjectState = true;
        return Options.Create(options);
    }

    private static StorageLayout NewLayout() => StorageLayout.Create(
        Path.Combine(Path.GetTempPath(), "HyperMemoryProjectionTests", Guid.NewGuid().ToString("N")));
}
