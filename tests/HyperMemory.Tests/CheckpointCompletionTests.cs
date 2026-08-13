using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

namespace HyperMemory.Tests;

public sealed class CheckpointCompletionTests
{
    [Fact]
    public async Task Checkpoint_is_hashed_retrievable_and_tamper_evident()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validationMemory = new ValidationMemoryService(events, [fixture.Validator]);
        var checkpointService = new CheckpointService(events, projection, validationMemory);
        await AppendTaskAsync(events, fixture.Scope, "task-1", "completed", ["evidence-validation-1"]);
        await validationMemory.ValidateAsync(Request("validation-1", "task-1", fixture.Scope));

        await Assert.ThrowsAsync<InvalidOperationException>(() => checkpointService.CreateAsync(
            new CheckpointRequest(fixture.Scope, "bad", ["missing-evidence"])));
        var checkpoint = await checkpointService.CreateAsync(
            new CheckpointRequest(fixture.Scope, "Milestone one", ["evidence-validation-1"], "checkpoint-1"));
        var latest = await checkpointService.GetLatestAsync(fixture.Scope);
        var valid = await checkpointService.VerifyAsync(checkpoint);
        var invalid = await checkpointService.VerifyAsync(checkpoint with { SnapshotJson = "{}" });

        Assert.Equal("checkpoint-1", latest!.CheckpointId);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Problems));
        Assert.False(invalid.IsValid);
        Assert.NotEmpty(invalid.Problems);
    }

    [Fact]
    public async Task Completion_is_ready_only_with_complete_task_pass_validation_and_evidence()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validationMemory = new ValidationMemoryService(events, [fixture.Validator]);
        var evaluator = new CompletionEvaluator(projection, validationMemory);
        await AppendTaskAsync(events, fixture.Scope, "task-1", "completed", ["evidence-validation-1"]);
        var validation = await validationMemory.ValidateAsync(Request("validation-1", "task-1", fixture.Scope));

        var ready = await evaluator.EvaluateAsync(new CompletionAssessmentRequest(
            fixture.Scope, "task-1", ["validation-1"], ["evidence-validation-1"]));

        Assert.Equal(CompletionAssessmentStatus.Ready, ready.Status);
        Assert.Equal(CompletionDisposition.VerifiedComplete, ready.Disposition);
        Assert.True(ready.IsAdvisory);
        Assert.Empty(ready.BlockingTaskIds);
        Assert.Empty(ready.MissingEvidenceIds);

        await validationMemory.MarkStaleAsync(validation.Record, fixture.Scope, "Artifact changed", "change-1");
        var stale = await evaluator.EvaluateAsync(new CompletionAssessmentRequest(
            fixture.Scope, "task-1", ["validation-1"], ["evidence-validation-1"]));
        Assert.Equal(CompletionAssessmentStatus.NotReady, stale.Status);
        Assert.Equal(CompletionDisposition.Incomplete, stale.Disposition);
        Assert.Contains("validation-1", stale.InvalidValidationIds);
    }

    [Fact]
    public async Task Completion_is_unknown_without_evidence_and_not_ready_with_open_errors()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validationMemory = new ValidationMemoryService(events, [fixture.Validator]);
        var evaluator = new CompletionEvaluator(projection, validationMemory);
        var errors = new ErrorDecisionMemoryService(events, projection);
        await AppendTaskAsync(events, fixture.Scope, "task-1", "completed", []);

        var unknown = await evaluator.EvaluateAsync(new CompletionAssessmentRequest(fixture.Scope, "task-1"));
        Assert.Equal(CompletionAssessmentStatus.Unknown, unknown.Status);
        Assert.Equal(CompletionDisposition.UnverifiedComplete, unknown.Disposition);
        Assert.Contains(unknown.Reasons, reason => reason.Contains("no durable completion evidence", StringComparison.OrdinalIgnoreCase));

        await errors.RecordErrorAsync(new ErrorStateChange(
            "error-1", "test", "Test failed", "fingerprint-1", "open"), fixture.Scope);
        var blocked = await evaluator.EvaluateAsync(new CompletionAssessmentRequest(fixture.Scope, "task-1"));
        Assert.Equal(CompletionAssessmentStatus.NotReady, blocked.Status);
        Assert.Equal(CompletionDisposition.Blocked, blocked.Disposition);
        Assert.Contains("error-1", blocked.BlockingErrorIds);
    }

    [Fact]
    public async Task Completed_task_is_not_verified_when_an_active_dependency_is_incomplete()
    {
        var fixture = await NewFixtureAsync();
        await using var events = fixture.Events;
        using var projection = fixture.Projection;
        var validationMemory = new ValidationMemoryService(events, [fixture.Validator]);
        var evaluator = new CompletionEvaluator(projection, validationMemory);
        await AppendTaskAsync(events, fixture.Scope, "task-main", "completed", ["evidence-validation-main"]);
        await AppendTaskAsync(events, fixture.Scope, "task-prerequisite", "active", []);
        await events.AppendAsync(new OperationalEventWriteRequest(
            "task.dependency.upserted", new OperationalObjectRef(OperationalObjectTypes.Task, "task-main"), fixture.Scope,
            JsonSerializer.Serialize(new TaskDependencyStateChange("task-main", "task-prerequisite", "depends_on")),
            EventId: "dependency:main-prerequisite"));
        await validationMemory.ValidateAsync(Request("validation-main", "task-main", fixture.Scope));
        await projection.ProjectPendingAsync(fixture.Scope, 100);
        var projected = await projection.GetCurrentAsync(fixture.Scope);
        Assert.Contains(projected!.TaskDependencies, item =>
            item.FromTaskId == "task-main" && item.ToTaskId == "task-prerequisite" && item.IsActive);

        var assessment = await evaluator.EvaluateAsync(new CompletionAssessmentRequest(
            fixture.Scope, "task-main", ["validation-main"], ["evidence-validation-main"]));

        Assert.True(assessment.Status == CompletionAssessmentStatus.NotReady,
            $"Actual: {assessment.Status}; reasons: {string.Join(" | ", assessment.Reasons)}; blocking: {string.Join(",", assessment.BlockingTaskIds)}");
        Assert.Equal(CompletionDisposition.Incomplete, assessment.Disposition);
        Assert.Contains("task-prerequisite", assessment.BlockingTaskIds);
    }

    private static ValidationRequest Request(string validationId, string taskId, OperationalScope scope) => new(
        new OperationalObjectRef(OperationalObjectTypes.Task, taskId), scope, "test",
        "{\"scope\":\"targeted\"}", [], ValidationId: validationId);

    private static Task AppendTaskAsync(
        SqliteMemoryStore events,
        OperationalScope scope,
        string taskId,
        string status,
        IReadOnlyList<string> evidenceIds) => events.AppendAsync(new OperationalEventWriteRequest(
            string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? "task.completed" : "task.updated",
            new OperationalObjectRef(OperationalObjectTypes.Task, taskId), scope,
            JsonSerializer.Serialize(new TaskStateChange(taskId, "Task", status, RequiredEvidenceIds: evidenceIds)),
            EventId: $"task:{taskId}:{Guid.NewGuid():N}"));

    private static async Task<Fixture> NewFixtureAsync()
    {
        var layout = StorageLayout.Create(Path.Combine(Path.GetTempPath(), "HyperMemoryCheckpointTests",
            Guid.NewGuid().ToString("N")));
        var settings = new HyperMemoryOptions();
        settings.Operational.EnableEventJournal = true;
        settings.Operational.EnableProjectState = true;
        settings.Operational.EnableValidationMemory = true;
        settings.Operational.EnableErrorMemory = true;
        settings.Operational.EnableTaskGraph = true;
        settings.Operational.EnableCheckpoints = true;
        var options = Options.Create(settings);
        var events = new SqliteMemoryStore(layout, options);
        await events.InitializeAsync();
        return new Fixture(events, new SqliteProjectStateProjectionStore(layout, events, options),
            new CompletionValidator(), new OperationalScope("workspace-1", "project-1", "session-1", "agent-1", "task-1"));
    }

    private sealed record Fixture(
        SqliteMemoryStore Events,
        SqliteProjectStateProjectionStore Projection,
        CompletionValidator Validator,
        OperationalScope Scope);

    private sealed class CompletionValidator : IValidationAdapter
    {
        public string ValidatorId => "completion-validator";
        public bool CanValidate(ValidationRequest request) => true;
        public Task<ValidationResult> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValidationResult(ValidatorId, ValidationStatus.Pass, "Verified",
                [new EvidenceRecord($"evidence-{request.ValidationId}", "test", "ignored", null, "hash",
                    ValidatorId, DateTimeOffset.UtcNow, "{\"passed\":true}")], DateTimeOffset.UtcNow));
    }
}
