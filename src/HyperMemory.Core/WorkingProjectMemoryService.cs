using System.Text.Json;

namespace HyperMemory.Core;

public sealed class WorkingProjectMemoryService(
    IOperationalEventStore events,
    IProjectStateProjectionStore projections,
    IEnumerable<IValidationMemoryService> validationServices,
    int defaultTtlMinutes = 1_440,
    int maxItems = 200) : IWorkingProjectMemoryService
{
    private readonly TimeSpan _defaultTtl = TimeSpan.FromMinutes(Math.Clamp(defaultTtlMinutes, 1, 525_600));
    private readonly int _maxItems = Math.Clamp(maxItems, 10, 10_000);

    public async Task<WorkingMemoryItem> UpsertWorkingAsync(
        WorkingMemoryChange change,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.ItemType);
        var value = OperationalDataSanitizer.RedactJson(change.ValueJson);
        if (change.Priority is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(change), "Working-memory priority must be between 0 and 100.");
        await projections.ProjectPendingAsync(scope, 10_000, cancellationToken);
        var state = await projections.GetCurrentAsync(scope, cancellationToken);
        var current = state?.WorkingMemory.SingleOrDefault(item => item.Key == change.Key.Trim());
        var activeCount = state?.WorkingMemory.Count(item => item.ExpiresAt is null || item.ExpiresAt > DateTimeOffset.UtcNow) ?? 0;
        if (current is null && activeCount >= _maxItems)
            throw new InvalidOperationException($"Working-memory limit of {_maxItems} active items was reached.");
        var expiresAt = (change.ExpiresAt ?? DateTimeOffset.UtcNow.Add(_defaultTtl)).ToUniversalTime();
        var normalized = change with
        {
            Key = change.Key.Trim(),
            ItemType = change.ItemType.Trim(),
            ValueJson = value,
            ExpiresAt = expiresAt,
            Metadata = OperationalDataSanitizer.RedactMetadata(change.Metadata)
        };
        var eventId = $"working:{normalized.Key}:upserted:{Guid.NewGuid():N}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            "working.upserted", new OperationalObjectRef("working-memory", normalized.Key), scope,
            JsonSerializer.Serialize(normalized), EventId: eventId, CorrelationId: normalized.Key,
            ExpectedRevision: current?.Revision ?? 0), cancellationToken);
        return new WorkingMemoryItem(normalized.Key, normalized.ItemType, normalized.ValueJson,
            normalized.Priority, expiresAt, written.Revision, eventId, DateTimeOffset.UtcNow, normalized.Metadata);
    }

    public async Task RemoveWorkingAsync(
        string key,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await projections.ProjectPendingAsync(scope, 10_000, cancellationToken);
        var current = (await projections.GetCurrentAsync(scope, cancellationToken))?.WorkingMemory
            .SingleOrDefault(item => item.Key == key.Trim());
        if (current is null) return;
        var change = new WorkingMemoryChange(current.Key, current.ItemType, "{}", current.Priority,
            current.ExpiresAt, current.Metadata);
        await events.AppendAsync(new OperationalEventWriteRequest(
            "working.removed", new OperationalObjectRef("working-memory", current.Key), scope,
            JsonSerializer.Serialize(change), EventId: $"working:{current.Key}:removed:{Guid.NewGuid():N}",
            CorrelationId: current.Key, ExpectedRevision: current.Revision), cancellationToken);
    }

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetActiveWorkingAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        await projections.ProjectPendingAsync(scope, 10_000, cancellationToken);
        var state = await projections.GetCurrentAsync(scope, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return (state?.WorkingMemory ?? []).Where(item => item.ExpiresAt is null || item.ExpiresAt > now)
            .OrderByDescending(item => item.Priority).ThenByDescending(item => item.UpdatedAt).Take(_maxItems).ToArray();
    }

    public async Task<ProjectStatementRecord> RecordStatementAsync(
        ProjectStatementChange change,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.StatementType);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Status);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Provenance);
        if (change.Confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(change), "Statement confidence must be between 0 and 1.");
        var evidenceIds = (change.EvidenceIds ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (evidenceIds.Length != (change.EvidenceIds?.Count ?? 0))
            throw new InvalidOperationException("Statement evidence ids must be non-empty and unique.");
        if (evidenceIds.Length > 0)
        {
            var validationMemory = validationServices.FirstOrDefault() ??
                throw new InvalidOperationException("Evidence-backed statements require validation memory.");
            var found = await validationMemory.GetEvidenceAsync(scope, evidenceIds, cancellationToken);
            var missing = evidenceIds.Except(found.Select(item => item.EvidenceId), StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Statement evidence does not exist: {string.Join(", ", missing)}.");
        }
        await projections.ProjectPendingAsync(scope, 10_000, cancellationToken);
        var statementId = string.IsNullOrWhiteSpace(change.StatementId) ? Guid.NewGuid().ToString("N") : change.StatementId.Trim();
        var current = (await projections.GetCurrentAsync(scope, cancellationToken))?.Statements
            .SingleOrDefault(item => item.StatementId == statementId);
        var normalized = change with
        {
            StatementId = statementId,
            StatementType = change.StatementType.Trim(),
            Text = SensitiveDataRedactor.Redact(change.Text).Value,
            Status = change.Status.Trim(),
            Provenance = change.Provenance.Trim(),
            EvidenceIds = evidenceIds,
            Metadata = OperationalDataSanitizer.RedactMetadata(change.Metadata)
        };
        var eventId = $"statement:{statementId}:recorded:{Guid.NewGuid():N}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            "statement.recorded", new OperationalObjectRef(normalized.StatementType, statementId), scope,
            JsonSerializer.Serialize(normalized), EventId: eventId, CorrelationId: statementId,
            ExpectedRevision: current?.Revision ?? 0), cancellationToken);
        return new ProjectStatementRecord(statementId, normalized.StatementType, normalized.Text,
            normalized.Status, normalized.Provenance, normalized.Confidence, evidenceIds, written.Revision,
            eventId, DateTimeOffset.UtcNow, normalized.Metadata);
    }

    private static void ValidateScope(OperationalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
    }
}
