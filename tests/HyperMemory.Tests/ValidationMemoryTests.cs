using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class ValidationMemoryTests
{
    [Fact]
    public async Task Missing_validator_is_persisted_as_unknown_never_pass()
    {
        var fixture = await NewFixtureAsync([]);
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new ValidationMemoryService(events, []);
        var request = Request("unknown-validation");

        var result = await service.ValidateAsync(request);
        await projection.ProjectPendingAsync(request.Scope);
        var state = await projection.GetCurrentAsync(request.Scope);

        Assert.False(result.AdapterAvailable);
        Assert.Equal(ValidationStatus.Unknown, result.Record.Status);
        Assert.Equal(ValidationStatus.Unknown, state!.Validations.Single().Status);
        Assert.StartsWith("unavailable:", result.Record.ValidatorId);
    }

    [Fact]
    public async Task Pass_without_evidence_is_downgraded_to_unknown()
    {
        var adapter = new StubValidator(_ => new ValidationResult(
            "empty-validator", ValidationStatus.Pass, "Looks fine", [], DateTimeOffset.UtcNow));
        var fixture = await NewFixtureAsync([adapter]);
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new ValidationMemoryService(events, [adapter]);

        var result = await service.ValidateAsync(Request("no-evidence"));

        Assert.True(result.AdapterAvailable);
        Assert.Equal(ValidationStatus.Unknown, result.Record.Status);
        Assert.Contains("no durable evidence", result.Record.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Evidence_is_redacted_retrievable_and_can_become_stale()
    {
        var adapter = new StubValidator(_ => new ValidationResult(
            "test-validator",
            ValidationStatus.Pass,
            "Targeted test passed",
            [new EvidenceRecord(
                "evidence-1", "test-output", "ignored", "file:///workspace/results.json", "ABC123",
                "test-validator", DateTimeOffset.UtcNow,
                "{\"password\":\"super-secret\",\"message\":\"Authorization: Bearer abcdefghijklmnop\"}",
                new Dictionary<string, string> { ["token"] = "sk-abcdefghijklmnop" })],
            DateTimeOffset.UtcNow));
        var fixture = await NewFixtureAsync([adapter]);
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new ValidationMemoryService(events, [adapter]);
        var request = Request("pass-with-evidence");

        var passed = await service.ValidateAsync(request);
        var evidence = await service.GetEvidenceAsync(request.Scope, passed.Record.EvidenceIds);
        await projection.ProjectPendingAsync(request.Scope);
        var passState = await projection.GetCurrentAsync(request.Scope);

        Assert.Equal(ValidationStatus.Pass, passed.Record.Status);
        Assert.Single(evidence);
        Assert.DoesNotContain("super-secret", evidence[0].DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", evidence[0].DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", evidence[0].Metadata!["token"], StringComparison.Ordinal);
        Assert.Equal(ValidationStatus.Pass, passState!.Validations.Single().Status);

        var stale = await service.MarkStaleAsync(passed.Record, request.Scope, "Artifact changed after validation");
        await projection.ProjectPendingAsync(request.Scope);
        var staleState = await projection.GetCurrentAsync(request.Scope);
        Assert.Equal(ValidationStatus.Stale, stale.Status);
        Assert.Equal(ValidationStatus.Stale, staleState!.Validations.Single().Status);
        Assert.NotNull(staleState.Validations.Single().StaleAt);
    }

    [Fact]
    public async Task Validator_exception_becomes_unknown_without_leaking_message()
    {
        var adapter = new ThrowingValidator();
        var fixture = await NewFixtureAsync([adapter]);
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var service = new ValidationMemoryService(events, [adapter]);

        var result = await service.ValidateAsync(Request("validator-error"));

        Assert.Equal(ValidationStatus.Unknown, result.Record.Status);
        Assert.Contains(nameof(InvalidOperationException), result.Record.Explanation);
        Assert.DoesNotContain("sensitive failure detail", result.Record.Explanation, StringComparison.Ordinal);
    }

    private static ValidationRequest Request(string id) => new(
        new OperationalObjectRef(OperationalObjectTypes.Artifact, "artifact-1"),
        new OperationalScope("workspace-1", "project-1", "session-1", "agent-1", "task-1"),
        "test",
        JsonSerializer.Serialize(new { command = "test", password = "must-not-persist" }),
        [],
        ValidationId: id);

    private static async Task<Fixture> NewFixtureAsync(IReadOnlyList<IValidationAdapter> adapters)
    {
        var layout = StorageLayout.Create(Path.Combine(Path.GetTempPath(), "HyperMemoryValidationTests",
            Guid.NewGuid().ToString("N")));
        var settings = new HyperMemoryOptions();
        settings.Operational.EnableEventJournal = true;
        settings.Operational.EnableProjectState = true;
        settings.Operational.EnableValidationMemory = true;
        var options = Options.Create(settings);
        var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();
        return new Fixture(events, new SqliteProjectStateProjectionStore(layout, events, options));
    }

    private sealed record Fixture(SqliteMemoryStore Events, SqliteProjectStateProjectionStore Projection);

    private sealed class StubValidator(Func<ValidationRequest, ValidationResult> validate) : IValidationAdapter
    {
        public string ValidatorId => "stub-validator";
        public bool CanValidate(ValidationRequest request) => request.ValidatorKind == "test";
        public Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(validate(request));
    }

    private sealed class ThrowingValidator : IValidationAdapter
    {
        public string ValidatorId => "throwing-validator";
        public bool CanValidate(ValidationRequest request) => true;
        public Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("sensitive failure detail");
    }
}
