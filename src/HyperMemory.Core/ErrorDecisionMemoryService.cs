using System.Text.Json;

namespace HyperMemory.Core;

public sealed class ErrorDecisionMemoryService(
    IOperationalEventStore events,
    IProjectStateProjectionStore projections,
    int maxRepairAttempts = 3) : IErrorDecisionMemoryService
{
    private readonly int _maxRepairAttempts = Math.Clamp(maxRepairAttempts, 1, 100);

    public async Task<ErrorMemoryResult> RecordErrorAsync(
        ErrorStateChange error,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(error.ErrorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(error.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(error.Fingerprint);
        await projections.ProjectPendingAsync(scope, cancellationToken: cancellationToken);
        var state = await projections.GetCurrentAsync(scope, cancellationToken);
        var current = state?.Errors.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(error.ErrorId) && item.ErrorId == error.ErrorId) ||
            item.Fingerprint == error.Fingerprint.Trim());
        var errorId = current?.ErrorId ?? (string.IsNullOrWhiteSpace(error.ErrorId) ? Guid.NewGuid().ToString("N") : error.ErrorId.Trim());
        var now = DateTimeOffset.UtcNow;
        var change = error with
        {
            ErrorId = errorId,
            ErrorType = error.ErrorType.Trim(),
            Message = SensitiveDataRedactor.Redact(error.Message).Value,
            Fingerprint = error.Fingerprint.Trim(),
            Status = current?.Status == "resolved" ? "reopened" : "open",
            ArtifactIds = error.ArtifactIds?.Distinct(StringComparer.Ordinal).ToArray() ?? current?.ArtifactIds ?? [],
            EvidenceIds = error.EvidenceIds?.Distinct(StringComparer.Ordinal).ToArray() ?? current?.EvidenceIds ?? [],
            RepairAttempts = current?.RepairAttempts ?? 0,
            MaxRepairAttempts = _maxRepairAttempts,
            Metadata = OperationalDataSanitizer.RedactMetadata(error.Metadata),
            Occurrences = (current?.Occurrences ?? 0) + 1,
            FirstSeenAt = current?.FirstSeenAt ?? now,
            LastSeenAt = now
        };
        var eventId = $"error:{errorId}:observed:{Guid.NewGuid():N}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            current is null ? "error.observed" : "error.updated",
            new OperationalObjectRef(OperationalObjectTypes.Error, errorId), scope,
            JsonSerializer.Serialize(change), EventId: eventId,
            CorrelationId: errorId, ExpectedRevision: current?.Revision ?? 0), cancellationToken);
        var record = ToRecord(change, written, eventId);
        return new ErrorMemoryResult(record, record.RepairAttempts < record.MaxRepairAttempts);
    }

    public async Task<ErrorMemoryResult> RecordRepairAttemptAsync(
        string errorId,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorId);
        await projections.ProjectPendingAsync(scope, cancellationToken: cancellationToken);
        var current = (await projections.GetCurrentAsync(scope, cancellationToken))?.Errors
            .SingleOrDefault(item => item.ErrorId == errorId.Trim()) ??
            throw new InvalidOperationException($"Error '{errorId}' does not exist in the project state.");
        if (current.Status == "resolved") return new ErrorMemoryResult(current, false);
        if (current.RepairAttempts >= current.MaxRepairAttempts)
            return new ErrorMemoryResult(current with { Status = "repair_limit_reached" }, false);

        var attempts = current.RepairAttempts + 1;
        var status = attempts >= current.MaxRepairAttempts ? "repair_limit_reached" : "repairing";
        var change = new ErrorStateChange(current.ErrorId, current.ErrorType, current.Message, current.Fingerprint,
            status, current.ArtifactIds, current.EvidenceIds, attempts, current.MaxRepairAttempts, current.Metadata)
        {
            Occurrences = current.Occurrences,
            FirstSeenAt = current.FirstSeenAt,
            LastSeenAt = current.LastSeenAt
        };
        var eventId = $"error:{current.ErrorId}:repair:{attempts}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            "error.repair-attempted", new OperationalObjectRef(OperationalObjectTypes.Error, current.ErrorId), scope,
            JsonSerializer.Serialize(change), EventId: eventId, CorrelationId: current.ErrorId,
            ExpectedRevision: current.Revision), cancellationToken);
        var record = ToRecord(change, written, eventId);
        return new ErrorMemoryResult(record, record.RepairAttempts < record.MaxRepairAttempts);
    }

    public async Task<ErrorRecord> ResolveErrorAsync(
        string errorId,
        OperationalScope scope,
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorId);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        if (evidenceIds.Count == 0 || evidenceIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Resolving an error requires durable evidence.");
        await EnsureEvidenceExistsAsync(scope, evidenceIds, cancellationToken);
        await projections.ProjectPendingAsync(scope, cancellationToken: cancellationToken);
        var current = (await projections.GetCurrentAsync(scope, cancellationToken))?.Errors
            .SingleOrDefault(item => item.ErrorId == errorId.Trim()) ??
            throw new InvalidOperationException($"Error '{errorId}' does not exist in the project state.");
        var mergedEvidence = current.EvidenceIds.Concat(evidenceIds).Distinct(StringComparer.Ordinal).ToArray();
        var change = new ErrorStateChange(current.ErrorId, current.ErrorType, current.Message, current.Fingerprint,
            "resolved", current.ArtifactIds, mergedEvidence, current.RepairAttempts,
            current.MaxRepairAttempts, current.Metadata)
        {
            Occurrences = current.Occurrences,
            FirstSeenAt = current.FirstSeenAt,
            LastSeenAt = current.LastSeenAt
        };
        var eventId = $"error:{current.ErrorId}:resolved:{Guid.NewGuid():N}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            "error.resolved", new OperationalObjectRef(OperationalObjectTypes.Error, current.ErrorId), scope,
            JsonSerializer.Serialize(change), EventId: eventId, CorrelationId: current.ErrorId,
            ExpectedRevision: current.Revision), cancellationToken);
        return ToRecord(change, written, eventId);
    }

    public async Task<DecisionRecord> RecordDecisionAsync(
        DecisionStateChange decision,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.Rationale);
        await projections.ProjectPendingAsync(scope, cancellationToken: cancellationToken);
        var state = await projections.GetCurrentAsync(scope, cancellationToken);
        var decisionId = string.IsNullOrWhiteSpace(decision.DecisionId) ? Guid.NewGuid().ToString("N") : decision.DecisionId.Trim();
        var current = state?.Decisions.SingleOrDefault(item => item.DecisionId == decisionId);
        if (!string.IsNullOrWhiteSpace(decision.SupersedesDecisionId) &&
            state?.Decisions.All(item => item.DecisionId != decision.SupersedesDecisionId) != false)
            throw new InvalidOperationException($"Superseded decision '{decision.SupersedesDecisionId}' does not exist.");
        var change = decision with
        {
            DecisionId = decisionId,
            Title = SensitiveDataRedactor.Redact(decision.Title).Value,
            Outcome = SensitiveDataRedactor.Redact(decision.Outcome).Value,
            Rationale = SensitiveDataRedactor.Redact(decision.Rationale).Value,
            Status = "active",
            EvidenceIds = decision.EvidenceIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [],
            Metadata = OperationalDataSanitizer.RedactMetadata(decision.Metadata)
        };
        var eventId = $"decision:{decisionId}:recorded:{Guid.NewGuid():N}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            "decision.recorded", new OperationalObjectRef(OperationalObjectTypes.Decision, decisionId), scope,
            JsonSerializer.Serialize(change), EventId: eventId, CorrelationId: decisionId,
            ExpectedRevision: current?.Revision ?? 0), cancellationToken);
        return new DecisionRecord(decisionId, change.Title, change.Outcome, change.Rationale, change.Status,
            change.SupersedesDecisionId, change.EvidenceIds ?? [], written.Revision, eventId,
            DateTimeOffset.UtcNow, change.Metadata);
    }

    private static ErrorRecord ToRecord(ErrorStateChange change, OperationalEventWriteResult written, string eventId)
    {
        var now = DateTimeOffset.UtcNow;
        return new(change.ErrorId, change.ErrorType, change.Message, change.Fingerprint, change.Status,
            change.ArtifactIds ?? [], change.EvidenceIds ?? [], change.RepairAttempts, change.MaxRepairAttempts,
            written.Revision, eventId, now, change.Metadata)
        {
            Occurrences = Math.Max(1, change.Occurrences),
            FirstSeenAt = change.FirstSeenAt ?? now,
            LastSeenAt = change.LastSeenAt ?? now
        };
    }

    private async Task EnsureEvidenceExistsAsync(
        OperationalScope scope,
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken)
    {
        var wanted = evidenceIds.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        long? after = null;
        while (found.Count < wanted.Count)
        {
            var batch = await events.ReadAsync(new OperationalEventQuery(
                scope.WorkspaceId, ProjectId: scope.ProjectId, ObjectType: OperationalObjectTypes.Evidence,
                EventType: "evidence.recorded", AfterSequence: after, Limit: 1_000), cancellationToken);
            if (batch.Count == 0) break;
            foreach (var item in batch)
                if (wanted.Contains(item.Subject.ObjectId)) found.Add(item.Subject.ObjectId);
            after = batch[^1].Sequence;
            if (batch.Count < 1_000) break;
        }
        var missing = wanted.Except(found, StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Cannot resolve error: durable evidence was not found: {string.Join(", ", missing)}.");
    }

    private static void ValidateScope(OperationalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
    }
}
