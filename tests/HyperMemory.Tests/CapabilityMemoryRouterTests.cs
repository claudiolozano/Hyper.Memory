using HyperMemory.Core;

namespace HyperMemory.Tests;

public sealed class CapabilityMemoryRouterTests
{
    [Fact]
    public async Task Capability_route_is_deterministic_prefers_no_authorization_and_reports_missing()
    {
        var registry = new CapabilityRegistry([
            new StaticProvider("provider-b", [
                new CapabilityDescriptor("skill-z", "skill", "hermes", true, true, ["pdf", "read"]),
                new CapabilityDescriptor("skill-a", "skill", "hermes", true, false, ["pdf", "read"],
                    new Dictionary<string, string> { ["token"] = "sk-abcdefghijklmnop" })]),
            new ThrowingProvider()
        ]);
        var router = new CapabilityRouter(registry);
        var route = await router.ResolveAsync(new OperationalScope("workspace-1"), [
            new CapabilityRequirement("read-pdf", "skill", ["pdf", "read"]),
            new CapabilityRequirement("edit-image", "skill", ["image", "edit"])
        ]);

        Assert.Single(route.Selected);
        Assert.Equal("skill-a", route.Selected[0].CapabilityId);
        Assert.False(route.RequiresAuthorization);
        Assert.Single(route.Missing);
        Assert.Equal("edit-image", route.Missing[0].RequirementId);
        Assert.Equal("[REDACTED]", route.Selected[0].Metadata!["token"]);
    }

    [Fact]
    public async Task Capability_registry_rejects_conflicting_identity()
    {
        var registry = new CapabilityRegistry([
            new StaticProvider("one", [new CapabilityDescriptor("same", "skill", "hermes", true, false, ["a"])]),
            new StaticProvider("two", [new CapabilityDescriptor("same", "tool", "hermes", true, false, ["b"])])
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ListAsync(new OperationalScope("workspace-1")));
    }

    [Fact]
    public async Task Memory_router_prioritizes_operational_state_and_never_exceeds_budget()
    {
        var scope = new OperationalScope("workspace-1", "project-1");
        var state = new ProjectStateSnapshot(scope, 42,
            [new ArtifactState("artifact-1", "src/game.cs", "source", "HASH", "rev-2", true,
                DateTimeOffset.UtcNow, "artifact-event")], [], [],
            [new TaskRecord("task-1", "Build game", "active", null, [], 1, "task-event", DateTimeOffset.UtcNow)],
            [], [new ValidationRecord("validation-1", new OperationalObjectRef("task", "task-1"),
                "test", ValidationStatus.Stale, "{}", [], "validation-event", DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow)
        {
            Errors = [new ErrorRecord("error-1", "compile", "Build failed", "fp", "open", [], [], 1, 3,
                1, "error-event", DateTimeOffset.UtcNow)]
        };
        var historical = new FakeMemoryService([
            Hit("memory-1", new string('H', 2_000)),
            Hit("memory-2", "Older relevant decision")
        ]);
        var router = new OperationalMemoryRouter(historical, new FakeProjection(state), []);

        var context = await router.BuildContextAsync(new MemoryContextRequest(scope, "build game", 700));

        Assert.True(context.CharacterCount <= 700);
        Assert.Equal(context.CharacterCount, context.Context.Length);
        Assert.Contains("Blocking errors", context.Context);
        Assert.Contains("error-1", context.Context);
        Assert.True(context.Context.IndexOf("Blocking errors", StringComparison.Ordinal) <
            context.Context.IndexOf("Tasks", StringComparison.Ordinal));
        Assert.Equal(42, context.ThroughSequence);
        Assert.Contains("error-event", context.SourceEventIds);
    }

    [Fact]
    public async Task Memory_router_respects_object_filter_and_degrades_when_history_fails()
    {
        var scope = new OperationalScope("workspace-1", "project-1");
        var state = new ProjectStateSnapshot(scope, 5,
            [new ArtifactState("artifact-1", "file.cs", "source", null, null, false,
                DateTimeOffset.UtcNow, "artifact-event")], [], [],
            [new TaskRecord("task-1", "Task", "active", null, [], 1, "task-event", DateTimeOffset.UtcNow)],
            [], [], DateTimeOffset.UtcNow);
        var router = new OperationalMemoryRouter(new ThrowingMemoryService(), new FakeProjection(state), []);

        var context = await router.BuildContextAsync(new MemoryContextRequest(
            scope, "task", 1_000, [OperationalObjectTypes.Task]));

        Assert.Contains("## Tasks", context.Context);
        Assert.DoesNotContain("## Artifacts", context.Context);
        Assert.Contains(context.Warnings, item => item.Contains("Historical memory unavailable", StringComparison.Ordinal));
    }

    private static MemoryHit Hit(string id, string content)
    {
        var atom = new MemoryAtom(id, id, 1, content, "HASH", "project-1", "test", "{}",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return new MemoryHit(atom, 1, 1, 0);
    }

    private sealed class StaticProvider(string id, IReadOnlyList<CapabilityDescriptor> capabilities) : ICapabilityProvider
    {
        public string ProviderId => id;
        public Task<IReadOnlyList<CapabilityDescriptor>> DiscoverAsync(OperationalScope scope,
            CancellationToken cancellationToken = default) => Task.FromResult(capabilities);
    }

    private sealed class ThrowingProvider : ICapabilityProvider
    {
        public string ProviderId => "throwing";
        public Task<IReadOnlyList<CapabilityDescriptor>> DiscoverAsync(OperationalScope scope,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("unavailable");
    }

    private sealed class FakeProjection(ProjectStateSnapshot state) : IProjectStateProjectionStore
    {
        public Task<int> ProjectPendingAsync(OperationalScope scope, int batchSize = 200,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<ProjectStateSnapshot?> GetCurrentAsync(OperationalScope scope,
            CancellationToken cancellationToken = default) => Task.FromResult<ProjectStateSnapshot?>(state);
        public Task RebuildAsync(OperationalScope scope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeMemoryService(IReadOnlyList<MemoryHit> hits) : IMemoryService
    {
        public Task<MemoryWriteResult> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(hits);
        public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingMemoryService : IMemoryService
    {
        public Task<MemoryWriteResult> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("offline");
        public Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
