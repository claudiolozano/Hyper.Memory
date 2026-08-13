using System.Text.Json;

namespace HyperMemory.Core;

public sealed class ContractInvalidationService(
    IOperationalEventStore events,
    IProjectStateProjectionStore projections,
    IValidationMemoryService validations) : IContractInvalidationService
{
    private static readonly HashSet<string> ImpactRelationships = new(StringComparer.OrdinalIgnoreCase)
    {
        "depends_on", "requires", "imports", "calls", "reads", "consumes", "configures",
        "implements", "references", "contains", "validates"
    };
    public async Task<ContractRecord> UpsertContractAsync(
        ContractStateChange contract,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract.ContractType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract.DefinitionJson);
        ArgumentNullException.ThrowIfNull(contract.Subject);
        ValidateReference(contract.Subject);
        var dependencies = (contract.Dependencies ?? []).Distinct().ToArray();
        foreach (var dependency in dependencies) ValidateReference(dependency);
        await projections.ProjectPendingAsync(scope, cancellationToken: cancellationToken);
        var state = await projections.GetCurrentAsync(scope, cancellationToken);
        var contractId = string.IsNullOrWhiteSpace(contract.ContractId) ? Guid.NewGuid().ToString("N") : contract.ContractId.Trim();
        var current = state?.Contracts.SingleOrDefault(item => item.ContractId == contractId);
        var change = contract with
        {
            ContractId = contractId,
            ContractType = contract.ContractType.Trim(),
            DefinitionJson = OperationalDataSanitizer.RedactJson(contract.DefinitionJson),
            Dependencies = dependencies
        };
        var eventId = $"contract:{contractId}:upserted:{Guid.NewGuid():N}";
        var written = await events.AppendAsync(new OperationalEventWriteRequest(
            "contract.upserted", new OperationalObjectRef(OperationalObjectTypes.Contract, contractId), scope,
            JsonSerializer.Serialize(change), EventId: eventId, CorrelationId: contractId,
            ExpectedRevision: current?.Revision ?? 0), cancellationToken);
        return new ContractRecord(contractId, change.Subject, change.ContractType, change.DefinitionJson,
            written.Revision, eventId, change.IsActive, DateTimeOffset.UtcNow) { Dependencies = dependencies };
    }

    public async Task<ArtifactChangeResult> ObserveArtifactChangeAsync(
        ArtifactStateChange artifact,
        OperationalScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ArtifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ArtifactType);
        await projections.ProjectPendingAsync(scope, cancellationToken: cancellationToken);
        var before = await projections.GetCurrentAsync(scope, cancellationToken);
        var previous = before?.Artifacts.SingleOrDefault(item => item.ArtifactId == artifact.ArtifactId.Trim());
        var sanitized = artifact with
        {
            ArtifactId = artifact.ArtifactId.Trim(),
            Uri = SensitiveDataRedactor.Redact(artifact.Uri).Value,
            ArtifactType = artifact.ArtifactType.Trim(),
            Metadata = OperationalDataSanitizer.RedactMetadata(artifact.Metadata)
        };
        if (previous is not null &&
            string.Equals(previous.Uri, sanitized.Uri, StringComparison.Ordinal) &&
            string.Equals(previous.ArtifactType, sanitized.ArtifactType, StringComparison.Ordinal) &&
            string.Equals(previous.ContentHash, sanitized.ContentHash, StringComparison.Ordinal) &&
            string.Equals(previous.Revision, sanitized.Revision, StringComparison.Ordinal) &&
            previous.IsDeleted == sanitized.IsDeleted)
            return new ArtifactChangeResult(previous, []);
        var observationId = string.IsNullOrWhiteSpace(sanitized.ObservationId)
            ? Guid.NewGuid().ToString("N")
            : sanitized.ObservationId.Trim();
        var eventId = $"artifact:{sanitized.ArtifactId}:changed:{observationId}";
        await events.AppendAsync(new OperationalEventWriteRequest(
            sanitized.IsDeleted ? "artifact.deleted" : previous is null ? "artifact.observed" : "artifact.changed",
            new OperationalObjectRef(OperationalObjectTypes.Artifact, sanitized.ArtifactId), scope,
            JsonSerializer.Serialize(sanitized), EventId: eventId, CorrelationId: sanitized.ArtifactId,
            OccurredAt: sanitized.ObservedAt),
            cancellationToken);

        var artifactReference = new OperationalObjectRef(OperationalObjectTypes.Artifact, sanitized.ArtifactId);
        var impactedSubjects = new HashSet<OperationalObjectRef> { artifactReference };
        bool expanded;
        do
        {
            expanded = false;
            foreach (var relationship in (before?.Relationships ?? []).Where(item =>
                         item.IsActive && ImpactRelationships.Contains(item.RelationshipType) &&
                         impactedSubjects.Contains(item.To)))
                expanded |= impactedSubjects.Add(relationship.From);
            foreach (var contract in (before?.Contracts ?? []).Where(item =>
                         item.IsActive && item.Dependencies.Any(impactedSubjects.Contains)))
            {
                expanded |= impactedSubjects.Add(contract.Subject);
                expanded |= impactedSubjects.Add(
                    new OperationalObjectRef(OperationalObjectTypes.Contract, contract.ContractId));
            }
        } while (expanded);
        var affected = previous is null ? [] : (before?.Validations ?? [])
            .Where(item => item.Status != ValidationStatus.Stale && impactedSubjects.Contains(item.Subject))
            .OrderBy(item => item.ValidationId, StringComparer.Ordinal)
            .ToArray();
        var invalidated = new List<string>(affected.Length);
        foreach (var validation in affected)
        {
            await validations.MarkStaleAsync(validation, scope,
                $"Artifact '{sanitized.ArtifactId}' {(sanitized.IsDeleted ? "was deleted" : "changed")} after validation.", eventId, cancellationToken);
            invalidated.Add(validation.ValidationId);
        }
        await projections.ProjectPendingAsync(scope, 10_000, cancellationToken);
        return new ArtifactChangeResult(new ArtifactState(
            sanitized.ArtifactId, sanitized.Uri, sanitized.ArtifactType, sanitized.ContentHash, sanitized.Revision,
            sanitized.IsSourceOfTruth, sanitized.ObservedAt ?? DateTimeOffset.UtcNow, eventId, sanitized.Metadata)
        {
            IsDeleted = sanitized.IsDeleted
        }, invalidated);
    }

    private static void ValidateScope(OperationalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceId);
    }

    private static void ValidateReference(OperationalObjectRef reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.ObjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.ObjectId);
    }
}
