namespace HyperMemory.Core;

/// <summary>
/// Known object types. Storage and APIs deliberately accept arbitrary non-empty strings so
/// integrations can add domains without requiring a Core release.
/// </summary>
public static class OperationalObjectTypes
{
    public const string Workspace = "workspace";
    public const string Project = "project";
    public const string Session = "session";
    public const string Artifact = "artifact";
    public const string Relationship = "relationship";
    public const string Dependency = "dependency";
    public const string Contract = "contract";
    public const string Task = "task";
    public const string Goal = "goal";
    public const string Requirement = "requirement";
    public const string Constraint = "constraint";
    public const string Decision = "decision";
    public const string Observation = "observation";
    public const string Error = "error";
    public const string Validation = "validation";
    public const string Evidence = "evidence";
    public const string State = "state";
    public const string Event = "event";
    public const string Checkpoint = "checkpoint";
    public const string Snapshot = "snapshot";
}

public enum ValidationStatus
{
    Unknown = 0,
    Pass = 1,
    Fail = 2,
    Stale = 3,
    Planned = 4,
    NotRun = 5,
    Running = 6,
    Blocked = 7
}

public sealed record OperationalScope(
    string WorkspaceId,
    string? ProjectId = null,
    string? SessionId = null,
    string? AgentId = null,
    string? TaskId = null);

public sealed record OperationalObjectRef(string ObjectType, string ObjectId);

public sealed record OperationalEventWriteRequest(
    string EventType,
    OperationalObjectRef Subject,
    OperationalScope Scope,
    string DataJson,
    string? EventId = null,
    string? CausationId = null,
    string? CorrelationId = null,
    long? ExpectedRevision = null,
    DateTimeOffset? OccurredAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record OperationalEvent(
    string EventId,
    long Sequence,
    long Revision,
    string EventType,
    OperationalObjectRef Subject,
    OperationalScope Scope,
    string DataJson,
    string ContentHash,
    string? CausationId,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    DateTimeOffset StoredAt,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record OperationalEventWriteResult(
    string EventId,
    long Sequence,
    long Revision,
    bool Created);

public sealed record OperationalEventQuery(
    string WorkspaceId,
    string? ProjectId = null,
    string? SessionId = null,
    string? AgentId = null,
    string? TaskId = null,
    string? ObjectType = null,
    string? ObjectId = null,
    string? EventType = null,
    long? AfterSequence = null,
    int Limit = 200);

public sealed record OperationalRelationship(
    string RelationshipId,
    OperationalObjectRef From,
    OperationalObjectRef To,
    string RelationshipType,
    string SourceEventId,
    long Revision,
    bool IsActive,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ArtifactState(
    string ArtifactId,
    string Uri,
    string ArtifactType,
    string? ContentHash,
    string? Revision,
    bool IsSourceOfTruth,
    DateTimeOffset ObservedAt,
    string SourceEventId,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public bool IsDeleted { get; init; }
}

public sealed record ArtifactStateChange(
    string ArtifactId,
    string Uri,
    string ArtifactType,
    string? ContentHash = null,
    string? Revision = null,
    bool IsSourceOfTruth = false,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? ObservationId = null,
    DateTimeOffset? ObservedAt = null,
    bool IsDeleted = false);

public sealed record EvidenceRecord(
    string EvidenceId,
    string EvidenceType,
    string SourceEventId,
    string? SourceUri,
    string? ContentHash,
    string Producer,
    DateTimeOffset CapturedAt,
    string DataJson,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ValidationRecord(
    string ValidationId,
    OperationalObjectRef Subject,
    string ValidatorId,
    ValidationStatus Status,
    string ScopeJson,
    IReadOnlyList<string> EvidenceIds,
    string SourceEventId,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset? StaleAt = null,
    string? Explanation = null);

public sealed record ContractRecord(
    string ContractId,
    OperationalObjectRef Subject,
    string ContractType,
    string DefinitionJson,
    long Revision,
    string SourceEventId,
    bool IsActive,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<OperationalObjectRef> Dependencies { get; init; } = [];
}

public sealed record ContractStateChange(
    string ContractId,
    OperationalObjectRef Subject,
    string ContractType,
    string DefinitionJson,
    bool IsActive = true,
    IReadOnlyList<OperationalObjectRef>? Dependencies = null);

public sealed record TaskRecord(
    string TaskId,
    string Title,
    string Status,
    string? ParentTaskId,
    IReadOnlyList<string> RequiredEvidenceIds,
    long Revision,
    string SourceEventId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record TaskStateChange(
    string TaskId,
    string Title,
    string Status,
    string? ParentTaskId = null,
    IReadOnlyList<string>? RequiredEvidenceIds = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record TaskDependency(
    string FromTaskId,
    string ToTaskId,
    string DependencyType,
    string SourceEventId,
    bool IsActive);

public sealed record RelationshipStateChange(
    string RelationshipId,
    OperationalObjectRef From,
    OperationalObjectRef To,
    string RelationshipType,
    bool IsActive = true,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record TaskDependencyStateChange(
    string FromTaskId,
    string ToTaskId,
    string DependencyType,
    bool IsActive = true);

public sealed record ValidationStateChange(
    string ValidationId,
    OperationalObjectRef Subject,
    string ValidatorId,
    ValidationStatus Status,
    string ScopeJson,
    IReadOnlyList<string>? EvidenceIds = null,
    DateTimeOffset? StaleAt = null,
    string? Explanation = null);

public sealed record ErrorRecord(
    string ErrorId,
    string ErrorType,
    string Message,
    string Fingerprint,
    string Status,
    IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<string> EvidenceIds,
    int RepairAttempts,
    int MaxRepairAttempts,
    long Revision,
    string SourceEventId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public int Occurrences { get; init; } = 1;
    public DateTimeOffset FirstSeenAt { get; init; } = UpdatedAt;
    public DateTimeOffset LastSeenAt { get; init; } = UpdatedAt;
}

public sealed record ErrorStateChange(
    string ErrorId,
    string ErrorType,
    string Message,
    string Fingerprint,
    string Status,
    IReadOnlyList<string>? ArtifactIds = null,
    IReadOnlyList<string>? EvidenceIds = null,
    int RepairAttempts = 0,
    int MaxRepairAttempts = 3,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public int Occurrences { get; init; } = 1;
    public DateTimeOffset? FirstSeenAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

public sealed record ErrorMemoryResult(ErrorRecord Record, bool RepairAllowed);

public sealed record DecisionRecord(
    string DecisionId,
    string Title,
    string Outcome,
    string Rationale,
    string Status,
    string? SupersedesDecisionId,
    IReadOnlyList<string> EvidenceIds,
    long Revision,
    string SourceEventId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record DecisionStateChange(
    string DecisionId,
    string Title,
    string Outcome,
    string Rationale,
    string Status = "active",
    string? SupersedesDecisionId = null,
    IReadOnlyList<string>? EvidenceIds = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ProjectStateSnapshot(
    OperationalScope Scope,
    long ThroughSequence,
    IReadOnlyList<ArtifactState> Artifacts,
    IReadOnlyList<OperationalRelationship> Relationships,
    IReadOnlyList<ContractRecord> Contracts,
    IReadOnlyList<TaskRecord> Tasks,
    IReadOnlyList<TaskDependency> TaskDependencies,
    IReadOnlyList<ValidationRecord> Validations,
    DateTimeOffset ProjectedAt)
{
    public IReadOnlyList<ErrorRecord> Errors { get; init; } = [];
    public IReadOnlyList<DecisionRecord> Decisions { get; init; } = [];
    public IReadOnlyList<WorkingMemoryItem> WorkingMemory { get; init; } = [];
    public IReadOnlyList<ProjectStatementRecord> Statements { get; init; } = [];
}

public sealed record WorkingMemoryItem(
    string Key,
    string ItemType,
    string ValueJson,
    int Priority,
    DateTimeOffset? ExpiresAt,
    long Revision,
    string SourceEventId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record WorkingMemoryChange(
    string Key,
    string ItemType,
    string ValueJson,
    int Priority = 50,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ProjectStatementRecord(
    string StatementId,
    string StatementType,
    string Text,
    string Status,
    string Provenance,
    double Confidence,
    IReadOnlyList<string> EvidenceIds,
    long Revision,
    string SourceEventId,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ProjectStatementChange(
    string StatementId,
    string StatementType,
    string Text,
    string Status,
    string Provenance,
    double Confidence,
    IReadOnlyList<string>? EvidenceIds = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record WorkingMemoryRequest(
    OperationalScope Scope,
    WorkingMemoryChange Change);

public sealed record WorkingMemoryRemoveRequest(
    OperationalScope Scope,
    string Key);

public sealed record ProjectStatementRequest(
    OperationalScope Scope,
    ProjectStatementChange Change);

public sealed record ArtifactChangeResult(
    ArtifactState Artifact,
    IReadOnlyList<string> InvalidatedValidationIds);

public sealed record ArtifactObservationRequest(
    OperationalScope Scope,
    ArtifactStateChange Artifact);

public sealed record CheckpointRecord(
    string CheckpointId,
    OperationalScope Scope,
    long ThroughSequence,
    string StateHash,
    string SnapshotJson,
    string SourceEventId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> EvidenceIds)
{
    public string Label { get; init; } = string.Empty;
}

public sealed record CheckpointRequest(
    OperationalScope Scope,
    string Label,
    IReadOnlyList<string>? EvidenceIds = null,
    string? CheckpointId = null);

public sealed record CheckpointVerification(
    string CheckpointId,
    bool IsValid,
    string ExpectedStateHash,
    string ActualStateHash,
    IReadOnlyList<string> Problems);

public enum CompletionAssessmentStatus
{
    Unknown = 0,
    Ready = 1,
    NotReady = 2
}

public enum CompletionDisposition
{
    UnverifiedComplete = 0,
    VerifiedComplete = 1,
    Incomplete = 2,
    Blocked = 3
}

public sealed record CompletionAssessmentRequest(
    OperationalScope Scope,
    string? TaskId = null,
    IReadOnlyList<string>? RequiredValidationIds = null,
    IReadOnlyList<string>? RequiredEvidenceIds = null,
    bool RequireAllProjectTasksComplete = false,
    bool RequireActiveContractsValidated = false);

public sealed record CompletionAssessment(
    CompletionAssessmentStatus Status,
    bool IsAdvisory,
    long ThroughSequence,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> BlockingTaskIds,
    IReadOnlyList<string> BlockingErrorIds,
    IReadOnlyList<string> InvalidValidationIds,
    IReadOnlyList<string> MissingEvidenceIds,
    DateTimeOffset EvaluatedAt)
{
    public CompletionDisposition Disposition { get; init; } = CompletionDisposition.UnverifiedComplete;
}

public sealed record ValidationRequest(
    OperationalObjectRef Subject,
    OperationalScope Scope,
    string ValidatorKind,
    string InputJson,
    IReadOnlyList<ArtifactState> Artifacts,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? ValidationId = null);

public sealed record ValidationResult(
    string ValidatorId,
    ValidationStatus Status,
    string Explanation,
    IReadOnlyList<EvidenceRecord> Evidence,
    DateTimeOffset EvaluatedAt,
    string? ValidationId = null);

public sealed record ValidationMemoryResult(
    ValidationRecord Record,
    bool AdapterAvailable);

public sealed record CapabilityDescriptor(
    string CapabilityId,
    string Kind,
    string Provider,
    bool IsAvailable,
    bool RequiresAuthorization,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record CapabilityRequirement(
    string RequirementId,
    string Kind,
    IReadOnlyList<string> RequiredTags,
    bool IsMandatory = true);

public sealed record CapabilityRoute(
    IReadOnlyList<CapabilityDescriptor> Selected,
    IReadOnlyList<CapabilityRequirement> Missing,
    bool RequiresAuthorization,
    string Explanation);

public sealed record CapabilityActivationSuggestion(
    string CapabilityId,
    string Provider,
    string Kind,
    bool RequiresAuthorization,
    IReadOnlyDictionary<string, string>? ActivationMetadata);

public sealed record CapabilityRouteRequest(
    OperationalScope Scope,
    IReadOnlyList<CapabilityRequirement> Requirements);

public sealed record MemoryContextRequest(
    OperationalScope Scope,
    string Intent,
    int CharacterBudget,
    IReadOnlyList<string>? PreferredObjectTypes = null,
    bool IncludeHistorical = true);

public sealed record MemoryContextSlice(
    string Context,
    int CharacterCount,
    long ThroughSequence,
    IReadOnlyList<string> SourceEventIds,
    IReadOnlyList<string> Warnings);
