using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyperMemory.Core;

public sealed class CheckpointService(
    IOperationalEventStore events,
    IProjectStateProjectionStore projections,
    IValidationMemoryService validations) : ICheckpointService
{
    public async Task<CheckpointRecord> CreateAsync(
        CheckpointRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScope(request.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Label);
        var evidenceIds = (request.EvidenceIds ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (evidenceIds.Length != (request.EvidenceIds?.Count ?? 0))
            throw new InvalidOperationException("Checkpoint evidence ids must be non-empty and unique.");
        if (evidenceIds.Length > 0)
        {
            var evidence = await validations.GetEvidenceAsync(request.Scope, evidenceIds, cancellationToken);
            var missing = evidenceIds.Except(evidence.Select(item => item.EvidenceId), StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Checkpoint evidence does not exist: {string.Join(", ", missing)}.");
        }

        await projections.ProjectPendingAsync(request.Scope, 10_000, cancellationToken);
        var state = await projections.GetCurrentAsync(request.Scope, cancellationToken) ??
            throw new InvalidOperationException("A checkpoint requires existing project state.");
        var canonicalState = state with { ProjectedAt = DateTimeOffset.UnixEpoch };
        var snapshotJson = JsonSerializer.Serialize(canonicalState);
        var stateHash = Hash(snapshotJson);
        var checkpointId = string.IsNullOrWhiteSpace(request.CheckpointId)
            ? Guid.NewGuid().ToString("N")
            : request.CheckpointId.Trim();
        var eventId = $"checkpoint:{checkpointId}:created";
        var checkpoint = new CheckpointRecord(
            checkpointId, request.Scope, state.ThroughSequence, stateHash, snapshotJson, eventId,
            DateTimeOffset.UtcNow, evidenceIds)
        {
            Label = SensitiveDataRedactor.Redact(request.Label).Value
        };
        await events.AppendAsync(new OperationalEventWriteRequest(
            "checkpoint.created", new OperationalObjectRef(OperationalObjectTypes.Checkpoint, checkpointId),
            request.Scope, JsonSerializer.Serialize(checkpoint), EventId: eventId,
            CorrelationId: checkpointId, ExpectedRevision: 0), cancellationToken);
        return checkpoint;
    }

    public async Task<CheckpointRecord?> GetLatestAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        CheckpointRecord? latest = null;
        long? after = null;
        while (true)
        {
            var batch = await events.ReadAsync(new OperationalEventQuery(
                scope.WorkspaceId, ProjectId: scope.ProjectId, ObjectType: OperationalObjectTypes.Checkpoint,
                EventType: "checkpoint.created", AfterSequence: after, Limit: 1_000), cancellationToken);
            if (batch.Count == 0) break;
            foreach (var item in batch)
                latest = JsonSerializer.Deserialize<CheckpointRecord>(item.DataJson) ??
                    throw new InvalidOperationException($"Checkpoint event '{item.EventId}' is invalid.");
            after = batch[^1].Sequence;
            if (batch.Count < 1_000) break;
        }
        return latest;
    }

    public Task<CheckpointVerification> VerifyAsync(
        CheckpointRecord checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        var problems = new List<string>();
        var actual = Hash(checkpoint.SnapshotJson);
        if (!string.Equals(actual, checkpoint.StateHash, StringComparison.Ordinal))
            problems.Add("Checkpoint state hash does not match its snapshot.");
        try
        {
            var snapshot = JsonSerializer.Deserialize<ProjectStateSnapshot>(checkpoint.SnapshotJson);
            if (snapshot is null) problems.Add("Checkpoint snapshot is empty.");
            else if (snapshot.ThroughSequence != checkpoint.ThroughSequence)
                problems.Add("Checkpoint sequence does not match its snapshot.");
        }
        catch (JsonException)
        {
            problems.Add("Checkpoint snapshot is not valid project-state JSON.");
        }
        return Task.FromResult(new CheckpointVerification(
            checkpoint.CheckpointId, problems.Count == 0, checkpoint.StateHash, actual, problems));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateScope(OperationalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
    }
}

public sealed class CompletionEvaluator(
    IProjectStateProjectionStore projections,
    IValidationMemoryService validations) : ICompletionEvaluator
{
    public async Task<CompletionAssessment> EvaluateAsync(
        CompletionAssessmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Scope.WorkspaceId);
        await projections.ProjectPendingAsync(request.Scope, 10_000, cancellationToken);
        var state = await projections.GetCurrentAsync(request.Scope, cancellationToken);
        if (state is null)
            return Assessment(CompletionAssessmentStatus.Unknown, 0, ["No project state is available."], [], [], [], []);

        var reasons = new List<string>();
        var blockingTasks = new HashSet<string>(StringComparer.Ordinal);
        var blockingErrors = state.Errors.Where(item => item.Status != "resolved")
            .Select(item => item.ErrorId).ToHashSet(StringComparer.Ordinal);
        var invalidValidations = new HashSet<string>(StringComparer.Ordinal);
        var missingEvidence = new HashSet<string>(StringComparer.Ordinal);
        var unknown = false;
        var hasExplicitRequirement = !string.IsNullOrWhiteSpace(request.TaskId) ||
            (request.RequiredValidationIds?.Count ?? 0) > 0 || (request.RequiredEvidenceIds?.Count ?? 0) > 0 ||
            request.RequireAllProjectTasksComplete || request.RequireActiveContractsValidated;
        if (!hasExplicitRequirement)
        {
            unknown = true;
            reasons.Add("No completion requirements were supplied.");
        }

        var tasks = new List<TaskRecord>();
        if (!string.IsNullOrWhiteSpace(request.TaskId))
        {
            var task = state.Tasks.SingleOrDefault(item => item.TaskId == request.TaskId.Trim());
            if (task is null)
            {
                unknown = true;
                reasons.Add($"Required task '{request.TaskId}' is absent from project state.");
            }
            else tasks.Add(task);
        }
        if (request.RequireAllProjectTasksComplete) tasks.AddRange(state.Tasks);
        var selectedTaskIds = tasks.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        var dependencies = state.TaskDependencies.Where(item => item.IsActive &&
            selectedTaskIds.Contains(item.FromTaskId)).ToArray();
        foreach (var dependency in dependencies)
        {
            var prerequisite = state.Tasks.SingleOrDefault(item => item.TaskId == dependency.ToTaskId);
            if (prerequisite is null)
            {
                unknown = true;
                reasons.Add($"Task dependency '{dependency.FromTaskId}' -> '{dependency.ToTaskId}' is absent from project state.");
            }
            else if (!string.Equals(prerequisite.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                blockingTasks.Add(prerequisite.TaskId);
                reasons.Add($"Task '{dependency.FromTaskId}' depends on incomplete task '{prerequisite.TaskId}'.");
            }
            else
            {
                tasks.Add(prerequisite);
                selectedTaskIds.Add(prerequisite.TaskId);
            }
        }
        foreach (var task in tasks.DistinctBy(item => item.TaskId))
        {
            if (!string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase))
                blockingTasks.Add(task.TaskId);
        }

        var requiredValidationIds = (request.RequiredValidationIds ?? []).ToHashSet(StringComparer.Ordinal);
        var relevantValidations = state.Validations.Where(item => requiredValidationIds.Contains(item.ValidationId) ||
            tasks.Any(task => item.Subject.ObjectType == OperationalObjectTypes.Task && item.Subject.ObjectId == task.TaskId)).ToArray();
        foreach (var required in requiredValidationIds)
            if (relevantValidations.All(item => item.ValidationId != required))
            {
                unknown = true;
                reasons.Add($"Required validation '{required}' is missing.");
            }
        foreach (var validation in relevantValidations)
        {
            if (validation.Status is ValidationStatus.Fail or ValidationStatus.Stale or ValidationStatus.Blocked)
                invalidValidations.Add(validation.ValidationId);
            else if (validation.Status == ValidationStatus.Unknown)
            {
                unknown = true;
                reasons.Add($"Validation '{validation.ValidationId}' is UNKNOWN.");
            }
            else if (validation.Status != ValidationStatus.Pass)
            {
                invalidValidations.Add(validation.ValidationId);
                reasons.Add($"Validation '{validation.ValidationId}' is {validation.Status.ToString().ToUpperInvariant()} and has not passed.");
            }
        }

        if (request.RequireActiveContractsValidated)
        {
            foreach (var contract in state.Contracts.Where(item => item.IsActive))
            {
                var contractReference = new OperationalObjectRef(OperationalObjectTypes.Contract, contract.ContractId);
                var covered = state.Validations.Any(item => item.Status == ValidationStatus.Pass &&
                    (item.Subject == contractReference || item.Subject == contract.Subject));
                if (!covered)
                {
                    unknown = true;
                    reasons.Add($"Active contract '{contract.ContractId}' has no current PASS validation.");
                }
            }
        }

        var requiredEvidence = (request.RequiredEvidenceIds ?? []).
            Concat(tasks.SelectMany(item => item.RequiredEvidenceIds)).
            Concat(relevantValidations.SelectMany(item => item.EvidenceIds)).
            Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
        var foundEvidence = await validations.GetEvidenceAsync(request.Scope, requiredEvidence, cancellationToken);
        foreach (var missing in requiredEvidence.Except(foundEvidence.Select(item => item.EvidenceId), StringComparer.Ordinal))
            missingEvidence.Add(missing);
        if (missingEvidence.Count > 0)
        {
            unknown = true;
            reasons.Add("One or more required evidence records are missing.");
        }

        foreach (var task in tasks.DistinctBy(item => item.TaskId))
        {
            var hasTaskEvidence = task.RequiredEvidenceIds.Count > 0 &&
                task.RequiredEvidenceIds.All(id => foundEvidence.Any(item => item.EvidenceId == id));
            var hasPassValidation = state.Validations.Any(item => item.Status == ValidationStatus.Pass &&
                item.Subject.ObjectType == OperationalObjectTypes.Task && item.Subject.ObjectId == task.TaskId);
            if (!hasTaskEvidence && !hasPassValidation)
            {
                unknown = true;
                reasons.Add($"Completed task '{task.TaskId}' has no durable completion evidence.");
            }
        }

        if (blockingTasks.Count > 0) reasons.Add("One or more required tasks are not complete.");
        if (blockingErrors.Count > 0) reasons.Add("The project has unresolved errors.");
        if (invalidValidations.Count > 0) reasons.Add("One or more required validations are FAIL or STALE.");
        var status = blockingTasks.Count > 0 || blockingErrors.Count > 0 || invalidValidations.Count > 0
            ? CompletionAssessmentStatus.NotReady
            : unknown ? CompletionAssessmentStatus.Unknown : CompletionAssessmentStatus.Ready;
        if (status == CompletionAssessmentStatus.Ready) reasons.Add("All explicit completion requirements are supported by current evidence.");
        var disposition = status switch
        {
            CompletionAssessmentStatus.Ready => CompletionDisposition.VerifiedComplete,
            CompletionAssessmentStatus.NotReady when blockingErrors.Count > 0 => CompletionDisposition.Blocked,
            CompletionAssessmentStatus.NotReady => CompletionDisposition.Incomplete,
            _ => CompletionDisposition.UnverifiedComplete
        };
        return Assessment(status, state.ThroughSequence, reasons, blockingTasks, blockingErrors,
            invalidValidations, missingEvidence) with { Disposition = disposition };
    }

    private static CompletionAssessment Assessment(
        CompletionAssessmentStatus status,
        long throughSequence,
        IEnumerable<string> reasons,
        IEnumerable<string> tasks,
        IEnumerable<string> errors,
        IEnumerable<string> validations,
        IEnumerable<string> evidence) => new(
            status, true, throughSequence, reasons.Distinct(StringComparer.Ordinal).ToArray(),
            tasks.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            errors.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            validations.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            evidence.OrderBy(item => item, StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow);
}
