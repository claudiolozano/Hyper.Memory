using System.Text.Json;

namespace HyperMemory.Core;

public sealed class ValidationMemoryService(
    IOperationalEventStore events,
    IEnumerable<IValidationAdapter> adapters) : IValidationMemoryService
{
    public async Task<ValidationMemoryResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var validationId = string.IsNullOrWhiteSpace(request.ValidationId)
            ? Guid.NewGuid().ToString("N")
            : request.ValidationId.Trim();
        var adapter = adapters
            .OrderBy(item => item.ValidatorId, StringComparer.Ordinal)
            .FirstOrDefault(item => CanValidate(item, request));

        ValidationResult result;
        if (adapter is null)
        {
            result = new ValidationResult(
                $"unavailable:{request.ValidatorKind.Trim()}",
                ValidationStatus.Unknown,
                $"No authorized validator is available for '{request.ValidatorKind.Trim()}'.",
                [],
                DateTimeOffset.UtcNow,
                validationId);
        }
        else
        {
            try
            {
                result = await adapter.ValidateAsync(request, cancellationToken) ??
                    throw new InvalidOperationException("Validator returned no result.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                result = new ValidationResult(
                    adapter.ValidatorId,
                    ValidationStatus.Unknown,
                    $"Validator failed without a trustworthy result ({error.GetType().Name}).",
                    [],
                    DateTimeOffset.UtcNow,
                    validationId);
            }
        }

        var evidence = NormalizeEvidence(result.Evidence, validationId);
        var status = result.Status;
        var explanation = SensitiveDataRedactor.Redact(result.Explanation).Value;
        if (status is (ValidationStatus.Pass or ValidationStatus.Fail) && evidence.Count == 0)
        {
            status = ValidationStatus.Unknown;
            explanation = "Validator returned no durable evidence; result downgraded to UNKNOWN.";
        }

        foreach (var item in evidence)
        {
            await events.AppendAsync(new OperationalEventWriteRequest(
                "evidence.recorded",
                new OperationalObjectRef(OperationalObjectTypes.Evidence, item.EvidenceId),
                request.Scope,
                JsonSerializer.Serialize(item),
                EventId: item.SourceEventId,
                CorrelationId: validationId,
                ExpectedRevision: 0), cancellationToken);
        }

        var validationEventId = $"validation:{validationId}:recorded";
        var state = new ValidationStateChange(
            validationId,
            request.Subject,
            Required(result.ValidatorId, "validator id"),
            status,
            BuildScopeJson(request),
            evidence.Select(item => item.EvidenceId).ToArray(),
            Explanation: explanation);
        await events.AppendAsync(new OperationalEventWriteRequest(
            "validation.recorded",
            new OperationalObjectRef(OperationalObjectTypes.Validation, validationId),
            request.Scope,
            JsonSerializer.Serialize(state),
            EventId: validationEventId,
            CorrelationId: validationId,
            ExpectedRevision: 0,
            OccurredAt: result.EvaluatedAt), cancellationToken);

        return new ValidationMemoryResult(new ValidationRecord(
            validationId, request.Subject, state.ValidatorId, status, state.ScopeJson,
            state.EvidenceIds ?? [], validationEventId, result.EvaluatedAt, Explanation: explanation), adapter is not null);
    }

    public async Task<ValidationRecord> MarkStaleAsync(
        ValidationRecord current,
        OperationalScope scope,
        string explanation,
        string? invalidationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        var at = DateTimeOffset.UtcNow;
        var redactedExplanation = SensitiveDataRedactor.Redact(explanation).Value;
        var suffix = string.IsNullOrWhiteSpace(invalidationId) ? Guid.NewGuid().ToString("N") : invalidationId.Trim();
        var eventId = $"validation:{current.ValidationId}:stale:{suffix}";
        var state = new ValidationStateChange(
            current.ValidationId, current.Subject, current.ValidatorId, ValidationStatus.Stale,
            current.ScopeJson, current.EvidenceIds, Explanation: redactedExplanation);
        await events.AppendAsync(new OperationalEventWriteRequest(
            "validation.stale",
            new OperationalObjectRef(OperationalObjectTypes.Validation, current.ValidationId),
            scope,
            JsonSerializer.Serialize(state),
            EventId: eventId,
            CorrelationId: current.ValidationId,
            ExpectedRevision: null), cancellationToken);
        return current with
        {
            Status = ValidationStatus.Stale,
            SourceEventId = eventId,
            StaleAt = at,
            Explanation = redactedExplanation
        };
    }

    public async Task<IReadOnlyList<EvidenceRecord>> GetEvidenceAsync(
        OperationalScope scope,
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(evidenceIds);
        if (evidenceIds.Count == 0) return [];
        var wanted = evidenceIds.Where(item => !string.IsNullOrWhiteSpace(item)).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, EvidenceRecord>(StringComparer.Ordinal);
        long? after = null;
        while (found.Count < wanted.Count)
        {
            var batch = await events.ReadAsync(new OperationalEventQuery(
                scope.WorkspaceId,
                ProjectId: scope.ProjectId,
                ObjectType: OperationalObjectTypes.Evidence,
                EventType: "evidence.recorded",
                AfterSequence: after,
                Limit: 1_000), cancellationToken);
            if (batch.Count == 0) break;
            foreach (var item in batch)
            {
                if (!wanted.Contains(item.Subject.ObjectId)) continue;
                var evidence = JsonSerializer.Deserialize<EvidenceRecord>(item.DataJson) ??
                    throw new InvalidOperationException($"Evidence event '{item.EventId}' is invalid.");
                found[evidence.EvidenceId] = evidence;
            }
            after = batch[^1].Sequence;
            if (batch.Count < 1_000) break;
        }
        return evidenceIds.Where(found.ContainsKey).Select(item => found[item]).ToArray();
    }

    private static IReadOnlyList<EvidenceRecord> NormalizeEvidence(
        IReadOnlyList<EvidenceRecord> source,
        string validationId)
    {
        var output = new List<EvidenceRecord>(source.Count);
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index];
            var evidenceId = string.IsNullOrWhiteSpace(item.EvidenceId)
                ? $"{validationId}-evidence-{index + 1}"
                : item.EvidenceId.Trim();
            if (!identifiers.Add(evidenceId))
                throw new InvalidOperationException($"Duplicate evidence id '{evidenceId}'.");
            var sourceEventId = $"validation:{validationId}:evidence:{evidenceId}";
            output.Add(item with
            {
                EvidenceId = evidenceId,
                EvidenceType = Required(item.EvidenceType, "evidence type"),
                SourceEventId = sourceEventId,
                SourceUri = SensitiveDataRedactor.Redact(item.SourceUri ?? string.Empty).Value.NullIfEmpty(),
                Producer = Required(item.Producer, "evidence producer"),
                DataJson = OperationalDataSanitizer.RedactJson(item.DataJson),
                Metadata = OperationalDataSanitizer.RedactMetadata(item.Metadata)
            });
        }
        return output;
    }

    private static void ValidateRequest(ValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject.ObjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject.ObjectId);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Scope.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ValidatorKind);
        OperationalDataSanitizer.RedactJson(request.InputJson);
    }

    private static bool CanValidate(IValidationAdapter adapter, ValidationRequest request)
    {
        try { return adapter.CanValidate(request); }
        catch { return false; }
    }

    private static string BuildScopeJson(ValidationRequest request) => JsonSerializer.Serialize(new
    {
        validatorKind = request.ValidatorKind.Trim(),
        artifacts = request.Artifacts.Select(item => new { item.ArtifactId, item.Uri, item.Revision, item.ContentHash })
    });

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} cannot be empty.") : value.Trim();
}

internal static class ValidationStringExtensions
{
    public static string? NullIfEmpty(this string value) => string.IsNullOrEmpty(value) ? null : value;
}
