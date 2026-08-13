using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class ErrorDecisionContractTests
{
    [Fact]
    public async Task Repair_attempts_are_bounded_and_resolution_requires_evidence()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new ErrorDecisionMemoryService(events, projection, 3);

        var observed = await service.RecordErrorAsync(new ErrorStateChange(
            "error-1", "compile", "Build failed password=top-secret", "fingerprint-1", "open"), fixture.Scope);
        var first = await service.RecordRepairAttemptAsync("error-1", fixture.Scope);
        var second = await service.RecordRepairAttemptAsync("error-1", fixture.Scope);
        var third = await service.RecordRepairAttemptAsync("error-1", fixture.Scope);
        var rejected = await service.RecordRepairAttemptAsync("error-1", fixture.Scope);

        Assert.DoesNotContain("top-secret", observed.Record.Message, StringComparison.Ordinal);
        var repeated = await service.RecordErrorAsync(new ErrorStateChange(
            "different-id", "compile", "Build failed again", "fingerprint-1", "open"), fixture.Scope);
        Assert.Equal("error-1", repeated.Record.ErrorId);
        Assert.Equal(2, repeated.Record.Occurrences);
        Assert.Equal(observed.Record.FirstSeenAt, repeated.Record.FirstSeenAt);
        Assert.True(first.RepairAllowed);
        Assert.True(second.RepairAllowed);
        Assert.False(third.RepairAllowed);
        Assert.False(rejected.RepairAllowed);
        Assert.Equal(3, rejected.Record.RepairAttempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveErrorAsync("error-1", fixture.Scope, []));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveErrorAsync("error-1", fixture.Scope, ["missing-evidence"]));
        var evidence = new EvidenceRecord("evidence-fix", "test", "evidence-event", null, "hash", "test",
            DateTimeOffset.UtcNow, "{\"passed\":true}");
        await events.AppendAsync(new OperationalEventWriteRequest(
            "evidence.recorded", new OperationalObjectRef(OperationalObjectTypes.Evidence, evidence.EvidenceId),
            fixture.Scope, JsonSerializer.Serialize(evidence), EventId: evidence.SourceEventId, ExpectedRevision: 0));
        var resolved = await service.ResolveErrorAsync("error-1", fixture.Scope, ["evidence-fix"]);
        Assert.Equal("resolved", resolved.Status);
        Assert.Contains("evidence-fix", resolved.EvidenceIds);
    }

    [Fact]
    public async Task Decisions_preserve_history_and_supersede_explicitly()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new ErrorDecisionMemoryService(events, projection);

        await service.RecordDecisionAsync(new DecisionStateChange(
            "decision-1", "Storage", "Use SQLite", "Local durable storage"), fixture.Scope);
        await service.RecordDecisionAsync(new DecisionStateChange(
            "decision-2", "Storage v2", "Keep SQLite", "Measured performance remains sufficient",
            SupersedesDecisionId: "decision-1"), fixture.Scope);
        await projection.ProjectPendingAsync(fixture.Scope);
        var state = await projection.GetCurrentAsync(fixture.Scope);

        Assert.Equal(2, state!.Decisions.Count);
        Assert.Equal("superseded", state.Decisions.Single(item => item.DecisionId == "decision-1").Status);
        Assert.Equal("active", state.Decisions.Single(item => item.DecisionId == "decision-2").Status);
    }

    [Fact]
    public async Task Artifact_change_invalidates_direct_and_contract_dependent_validations()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validator = new EvidenceValidator();
        var validationMemory = new ValidationMemoryService(events, [validator]);
        var contracts = new ContractInvalidationService(events, projection, validationMemory);
        var artifact = new ArtifactStateChange("artifact-1", "src/game.cs", "source", "hash-1", "rev-1", true);

        await contracts.ObserveArtifactChangeAsync(artifact, fixture.Scope);
        await contracts.UpsertContractAsync(new ContractStateChange(
            "contract-1",
            new OperationalObjectRef(OperationalObjectTypes.Task, "task-1"),
            "artifact-consistency",
            "{\"rule\":\"artifact hash must remain stable\",\"password\":\"hidden\"}",
            Dependencies: [new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-1")]), fixture.Scope);
        var direct = await validationMemory.ValidateAsync(Request(
            "validation-direct", new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-1"), fixture.Scope));
        var dependent = await validationMemory.ValidateAsync(Request(
            "validation-dependent", new OperationalObjectRef(OperationalObjectTypes.Task, "task-1"), fixture.Scope));
        await projection.ProjectPendingAsync(fixture.Scope);

        var unchanged = await contracts.ObserveArtifactChangeAsync(artifact, fixture.Scope);
        await projection.ProjectPendingAsync(fixture.Scope);
        Assert.Empty(unchanged.InvalidatedValidationIds);
        Assert.All((await projection.GetCurrentAsync(fixture.Scope))!.Validations,
            item => Assert.Equal(ValidationStatus.Pass, item.Status));

        var changed = await contracts.ObserveArtifactChangeAsync(artifact with
        {
            ContentHash = "hash-2",
            Revision = "rev-2"
        }, fixture.Scope);
        var state = await projection.GetCurrentAsync(fixture.Scope);

        Assert.Equal(2, changed.InvalidatedValidationIds.Count);
        Assert.Contains(direct.Record.ValidationId, changed.InvalidatedValidationIds);
        Assert.Contains(dependent.Record.ValidationId, changed.InvalidatedValidationIds);
        Assert.All(state!.Validations, item => Assert.Equal(ValidationStatus.Stale, item.Status));
        Assert.DoesNotContain("hidden", state.Contracts.Single().DefinitionJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifact_change_propagates_through_transitive_dependency_graph_only()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validator = new EvidenceValidator();
        var validationMemory = new ValidationMemoryService(events, [validator]);
        var contracts = new ContractInvalidationService(events, projection, validationMemory);
        var a = new OperationalObjectRef(OperationalObjectTypes.Artifact, "A");
        var b = new OperationalObjectRef(OperationalObjectTypes.Artifact, "B");
        var c = new OperationalObjectRef(OperationalObjectTypes.Artifact, "C");
        var unrelated = new OperationalObjectRef(OperationalObjectTypes.Artifact, "D");
        foreach (var artifact in new[] { a, b, c, unrelated })
            await contracts.ObserveArtifactChangeAsync(new ArtifactStateChange(
                artifact.ObjectId, $"{artifact.ObjectId}.dat", "generic", "hash-1", "rev-1"), fixture.Scope);
        await events.AppendAsync(new OperationalEventWriteRequest(
            "relationship.upserted", new OperationalObjectRef("relationship", "A-B"), fixture.Scope,
            JsonSerializer.Serialize(new RelationshipStateChange("A-B", a, b, "depends_on")), EventId: "relation-A-B"));
        await events.AppendAsync(new OperationalEventWriteRequest(
            "relationship.upserted", new OperationalObjectRef("relationship", "B-C"), fixture.Scope,
            JsonSerializer.Serialize(new RelationshipStateChange("B-C", b, c, "depends_on")), EventId: "relation-B-C"));
        foreach (var subject in new[] { a, b, c, unrelated })
            await validationMemory.ValidateAsync(Request($"validation-{subject.ObjectId}", subject, fixture.Scope));
        await projection.ProjectPendingAsync(fixture.Scope, 100);

        var result = await contracts.ObserveArtifactChangeAsync(new ArtifactStateChange(
            "C", "C.dat", "generic", "hash-2", "rev-2"), fixture.Scope);
        var state = await projection.GetCurrentAsync(fixture.Scope);

        Assert.Equal(3, result.InvalidatedValidationIds.Count);
        Assert.All(new[] { "A", "B", "C" }, id =>
            Assert.Equal(ValidationStatus.Stale,
                state!.Validations.Single(item => item.ValidationId == $"validation-{id}").Status));
        Assert.Equal(ValidationStatus.Pass,
            state!.Validations.Single(item => item.ValidationId == "validation-D").Status);
    }

    [Fact]
    public async Task Artifact_deletion_is_historical_state_and_invalidates_prior_pass()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validationMemory = new ValidationMemoryService(events, [new EvidenceValidator()]);
        var contracts = new ContractInvalidationService(events, projection, validationMemory);
        await contracts.ObserveArtifactChangeAsync(new ArtifactStateChange(
            "artifact-deleted", "src/old.cs", "source", "hash-1", "rev-1"), fixture.Scope);
        await validationMemory.ValidateAsync(Request("validation-deleted",
            new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-deleted"), fixture.Scope));
        await projection.ProjectPendingAsync(fixture.Scope, 100);

        var deleted = await contracts.ObserveArtifactChangeAsync(new ArtifactStateChange(
            "artifact-deleted", "src/old.cs", "source", "hash-1", "rev-2", IsDeleted: true), fixture.Scope);
        var state = await projection.GetCurrentAsync(fixture.Scope);

        Assert.True(deleted.Artifact.IsDeleted);
        Assert.True(state!.Artifacts.Single().IsDeleted);
        Assert.Equal(ValidationStatus.Stale, state.Validations.Single().Status);
    }

    private static ValidationRequest Request(string id, OperationalObjectRef subject, OperationalScope scope) =>
        new(subject, scope, "test", "{\"scope\":\"targeted\"}", [], ValidationId: id);

    private static async Task<Fixture> NewFixtureAsync()
    {
        var layout = StorageLayout.Create(Path.Combine(Path.GetTempPath(), "HyperMemoryErrorContractTests",
            Guid.NewGuid().ToString("N")));
        var settings = new HyperMemoryOptions();
        settings.Operational.EnableEventJournal = true;
        settings.Operational.EnableProjectState = true;
        settings.Operational.EnableValidationMemory = true;
        settings.Operational.EnableErrorMemory = true;
        settings.Operational.EnableDecisionMemory = true;
        settings.Operational.EnableContracts = true;
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

    private sealed class EvidenceValidator : IValidationAdapter
    {
        public string ValidatorId => "evidence-validator";
        public bool CanValidate(ValidationRequest request) => true;
        public Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(ValidatorId, ValidationStatus.Pass, "Verified",
                [new EvidenceRecord($"evidence-{request.ValidationId}", "test", "ignored", null, "hash",
                    ValidatorId, DateTimeOffset.UtcNow, JsonSerializer.Serialize(new { passed = true }))],
                DateTimeOffset.UtcNow));
    }
}
