using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class OperationalLongRunTests
{
    [Fact]
    public async Task Switching_model_or_agent_preserves_shared_project_state()
    {
        var layout = StorageLayout.Create(Path.Combine(Path.GetTempPath(), "HyperMemoryModelSwitchTests",
            Guid.NewGuid().ToString("N")));
        var settings = new HyperMemoryOptions();
        settings.Operational.EnableEventJournal = true;
        settings.Operational.EnableProjectState = true;
        var options = Options.Create(settings);
        await using var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();
        var firstScope = new OperationalScope("workspace-model", "project-model", "session-a", "model-a", "task-model");
        var secondScope = new OperationalScope("workspace-model", "project-model", "session-b", "model-b", "task-model");
        await events.AppendAsync(new OperationalEventWriteRequest(
            "task.created", new OperationalObjectRef(OperationalObjectTypes.Task, "task-model"), firstScope,
            JsonSerializer.Serialize(new TaskStateChange("task-model", "Cross-model task", "active")),
            EventId: "model-a-created", ExpectedRevision: 0));
        await events.AppendAsync(new OperationalEventWriteRequest(
            "task.completed", new OperationalObjectRef(OperationalObjectTypes.Task, "task-model"), secondScope,
            JsonSerializer.Serialize(new TaskStateChange("task-model", "Cross-model task", "completed")),
            EventId: "model-b-completed", ExpectedRevision: 0));

        using var projection = new SqliteProjectStateProjectionStore(layout, events, options);
        var sharedScope = new OperationalScope("workspace-model", "project-model");
        await projection.RebuildAsync(sharedScope);
        var state = await projection.GetCurrentAsync(sharedScope);
        var history = await events.ReadAsync(new OperationalEventQuery("workspace-model", ProjectId: "project-model"));

        Assert.Equal("completed", state!.Tasks.Single().Status);
        Assert.Equal(["model-a", "model-b"], history.Select(item => item.Scope.AgentId!).ToArray());
        Assert.Equal(["session-a", "session-b"], history.Select(item => item.Scope.SessionId!).ToArray());
    }

    [Fact]
    public async Task Multi_session_multi_agent_history_survives_restart_and_full_rebuild()
    {
        var layout = StorageLayout.Create(Path.Combine(Path.GetTempPath(), "HyperMemoryLongRunTests",
            Guid.NewGuid().ToString("N")));
        var settings = new HyperMemoryOptions();
        settings.Operational.EnableEventJournal = true;
        settings.Operational.EnableProjectState = true;
        var options = Options.Create(settings);
        const int taskCount = 100;
        const int updatesPerTask = 5;
        await using (var writer = new SqliteMemoryStore(layout, options))
        {
            await writer.InitializeAsync();
            for (var taskIndex = 0; taskIndex < taskCount; taskIndex++)
            {
                for (var update = 0; update < updatesPerTask; update++)
                {
                    var taskId = $"task-{taskIndex:D3}";
                    var scope = new OperationalScope("workspace-long", "project-long",
                        $"session-{taskIndex % 10:D2}", $"agent-{taskIndex % 4:D2}", taskId);
                    await writer.AppendAsync(new OperationalEventWriteRequest(
                        update == 0 ? "task.created" : update == updatesPerTask - 1 ? "task.completed" : "task.updated",
                        new OperationalObjectRef(OperationalObjectTypes.Task, taskId), scope,
                        JsonSerializer.Serialize(new TaskStateChange(taskId, $"Task {taskIndex}",
                            update == updatesPerTask - 1 ? "completed" : "active")),
                        EventId: $"long-{taskIndex:D3}-{update:D2}", ExpectedRevision: update));
                }
            }
        }

        await using var restarted = new SqliteMemoryStore(layout, options);
        await restarted.InitializeAsync();
        using var projection = new SqliteProjectStateProjectionStore(layout, restarted, options);
        var projectScope = new OperationalScope("workspace-long", "project-long");
        await projection.RebuildAsync(projectScope);
        var state = await projection.GetCurrentAsync(projectScope);
        var events = await restarted.ReadAsync(new OperationalEventQuery(
            "workspace-long", ProjectId: "project-long", Limit: 1_000));

        Assert.Equal(taskCount * updatesPerTask, events.Count);
        Assert.NotNull(state);
        Assert.Equal(events[^1].Sequence, state.ThroughSequence);
        Assert.Equal(taskCount, state.Tasks.Count);
        Assert.All(state.Tasks, task => Assert.Equal("completed", task.Status));
        Assert.Equal(10, events.Select(item => item.Scope.SessionId).Distinct().Count());
        Assert.Equal(4, events.Select(item => item.Scope.AgentId).Distinct().Count());

        var before = JsonSerializer.Serialize(state.Tasks);
        await projection.RebuildAsync(projectScope);
        var rebuilt = await projection.GetCurrentAsync(projectScope);
        Assert.Equal(before, JsonSerializer.Serialize(rebuilt!.Tasks));
    }
}
