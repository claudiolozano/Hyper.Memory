namespace HyperMemory.Core;

public interface IOperationalEventStore
{
    Task<OperationalEventWriteResult> AppendAsync(
        OperationalEventWriteRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationalEvent>> ReadAsync(
        OperationalEventQuery query,
        CancellationToken cancellationToken = default);
}

public interface IProjectStateProjectionStore
{
    Task<int> ProjectPendingAsync(
        OperationalScope scope,
        int batchSize = 200,
        CancellationToken cancellationToken = default);

    Task<ProjectStateSnapshot?> GetCurrentAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task RebuildAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Technology-specific validators implement this outside Core. Unsupported validation must
/// return Unknown rather than infer success.
/// </summary>
public interface IValidationAdapter
{
    string ValidatorId { get; }
    bool CanValidate(ValidationRequest request);
    Task<ValidationResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IValidationRegistry
{
    Task<IReadOnlyList<CapabilityDescriptor>> DiscoverAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

public interface IValidationMemoryService
{
    Task<ValidationMemoryResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken cancellationToken = default);

    Task<ValidationRecord> MarkStaleAsync(
        ValidationRecord current,
        OperationalScope scope,
        string explanation,
        string? invalidationId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvidenceRecord>> GetEvidenceAsync(
        OperationalScope scope,
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default);
}

public interface IErrorDecisionMemoryService
{
    Task<ErrorMemoryResult> RecordErrorAsync(
        ErrorStateChange error,
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task<ErrorMemoryResult> RecordRepairAttemptAsync(
        string errorId,
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task<ErrorRecord> ResolveErrorAsync(
        string errorId,
        OperationalScope scope,
        IReadOnlyList<string> evidenceIds,
        CancellationToken cancellationToken = default);

    Task<DecisionRecord> RecordDecisionAsync(
        DecisionStateChange decision,
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

public interface IContractInvalidationService
{
    Task<ContractRecord> UpsertContractAsync(
        ContractStateChange contract,
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task<ArtifactChangeResult> ObserveArtifactChangeAsync(
        ArtifactStateChange artifact,
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

public interface ICheckpointService
{
    Task<CheckpointRecord> CreateAsync(
        CheckpointRequest request,
        CancellationToken cancellationToken = default);

    Task<CheckpointRecord?> GetLatestAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task<CheckpointVerification> VerifyAsync(
        CheckpointRecord checkpoint,
        CancellationToken cancellationToken = default);
}

public interface ICompletionEvaluator
{
    Task<CompletionAssessment> EvaluateAsync(
        CompletionAssessmentRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWorkingProjectMemoryService
{
    Task<WorkingMemoryItem> UpsertWorkingAsync(
        WorkingMemoryChange change,
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task RemoveWorkingAsync(
        string key,
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkingMemoryItem>> GetActiveWorkingAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);

    Task<ProjectStatementRecord> RecordStatementAsync(
        ProjectStatementChange change,
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

public interface ICapabilityRegistry
{
    Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

public interface ICapabilityProvider
{
    string ProviderId { get; }

    Task<IReadOnlyList<CapabilityDescriptor>> DiscoverAsync(
        OperationalScope scope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Selects capabilities but never grants permission or executes them. Hermes remains the
/// authority that activates a skill or tool.
/// </summary>
public interface ICapabilityRouter
{
    Task<CapabilityRoute> ResolveAsync(
        OperationalScope scope,
        IReadOnlyList<CapabilityRequirement> requirements,
        CancellationToken cancellationToken = default);
}

public interface IOperationalMemoryRouter
{
    Task<MemoryContextSlice> BuildContextAsync(
        MemoryContextRequest request,
        CancellationToken cancellationToken = default);
}
