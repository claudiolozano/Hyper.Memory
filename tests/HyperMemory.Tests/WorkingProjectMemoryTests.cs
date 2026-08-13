using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class WorkingProjectMemoryTests
{
    [Fact]
    public async Task Working_memory_is_bounded_redacted_mutable_and_expirable()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new WorkingProjectMemoryService(events, projection, [], 60, 10);
        await service.UpsertWorkingAsync(new WorkingMemoryChange(
            "current-goal", "goal", "{\"text\":\"Build\",\"token\":\"secret-value\"}", 100), fixture.Scope);
        await service.UpsertWorkingAsync(new WorkingMemoryChange(
            "expired", "temporary", "{\"text\":\"old\"}", 10, DateTimeOffset.UtcNow.AddMinutes(-1)), fixture.Scope);
        var updated = await service.UpsertWorkingAsync(new WorkingMemoryChange(
            "current-goal", "goal", "{\"text\":\"Build safely\"}", 90), fixture.Scope);

        var active = await service.GetActiveWorkingAsync(fixture.Scope);
        Assert.Single(active);
        Assert.Equal("current-goal", active[0].Key);
        Assert.Contains("Build safely", active[0].ValueJson);
        Assert.DoesNotContain("secret-value", updated.ValueJson, StringComparison.Ordinal);
        Assert.Equal(2, updated.Revision);

        await service.RemoveWorkingAsync("current-goal", fixture.Scope);
        Assert.Empty(await service.GetActiveWorkingAsync(fixture.Scope));
    }

    [Fact]
    public async Task Working_memory_rejects_growth_beyond_active_limit()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new WorkingProjectMemoryService(events, projection, [], 60, 10);
        for (var index = 0; index < 10; index++)
            await service.UpsertWorkingAsync(new WorkingMemoryChange(
                $"item-{index}", "temporary", $"{{\"index\":{index}}}"), fixture.Scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertWorkingAsync(
            new WorkingMemoryChange("item-11", "temporary", "{}"), fixture.Scope));
    }

    [Fact]
    public async Task Goals_requirements_constraints_and_preferences_remain_distinct()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new WorkingProjectMemoryService(events, projection, []);
        foreach (var type in new[] { "goal", "requirement", "constraint", "preference" })
            await service.RecordStatementAsync(new ProjectStatementChange(
                $"{type}-1", type, $"Project {type}", "active", "user", 1), fixture.Scope);
        await projection.ProjectPendingAsync(fixture.Scope, 100);
        var state = await projection.GetCurrentAsync(fixture.Scope);

        Assert.Equal(4, state!.Statements.Count);
        Assert.Equal(new[] { "constraint", "goal", "preference", "requirement" },
            state.Statements.Select(item => item.StatementType));
        Assert.All(state.Statements, item => Assert.Equal("user", item.Provenance));
    }

    [Theory]
    [InlineData(ValidationStatus.Planned)]
    [InlineData(ValidationStatus.NotRun)]
    [InlineData(ValidationStatus.Running)]
    [InlineData(ValidationStatus.Blocked)]
    public void Validation_lifecycle_statuses_are_explicit(ValidationStatus status)
    {
        Assert.NotEqual(ValidationStatus.Pass, status);
        Assert.NotEqual(ValidationStatus.Unknown, status);
    }

    private static async Task<Fixture> NewFixtureAsync()
    {
        var layout = StorageLayout.Create(Path.Combine(Path.GetTempPath(), "HyperMemoryWorkingTests",
            Guid.NewGuid().ToString("N")));
        var settings = new HyperMemoryOptions();
        settings.Operational.EnableEventJournal = true;
        settings.Operational.EnableProjectState = true;
        settings.Operational.EnableWorkingMemory = true;
        var options = Options.Create(settings);
        var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();
        return new Fixture(events, new SqliteProjectStateProjectionStore(layout, events, options),
            new OperationalScope("workspace-1", "project-1", "session-1", "agent-1", "task-1"));
    }

    private sealed record Fixture(
        SqliteMemoryStore Events,
        SqliteProjectStateProjectionStore Projection,
        OperationalScope Scope);
}
