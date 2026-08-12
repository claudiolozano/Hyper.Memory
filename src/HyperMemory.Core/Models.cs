using System.Text.Json;

namespace HyperMemory.Core;

public sealed record MemoryWriteRequest(
    string Content,
    string? LogicalId = null,
    string? EventId = null,
    string? Project = null,
    string? Source = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? OccurredAt = null,
    string? SourceUri = null,
    string? SourceTitle = null,
    string? Author = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidTo = null,
    string? SupersedesVersionId = null,
    string? ClaimKey = null,
    double? StatedConfidence = null);

public sealed record MemoryAtom(
    string VersionId,
    string LogicalId,
    long Sequence,
    string Content,
    string ContentHash,
    string? Project,
    string? Source,
    string MetadataJson,
    DateTimeOffset OccurredAt,
    DateTimeOffset StoredAt,
    string? SourceUri = null,
    string? SourceTitle = null,
    string? Author = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidTo = null,
    string? SupersedesVersionId = null,
    string? ClaimKey = null,
    double? StatedConfidence = null)
{
    public IReadOnlyDictionary<string, string> Metadata =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(MetadataJson) ?? [];
}

public sealed record EmbeddingVector(float[] Values, string Provider, string Model)
{
    public int Dimensions => Values.Length;
}

public sealed record MemoryQuery(
    string Text,
    int Limit = 12,
    string? Project = null,
    double TextWeight = 0.45,
    double SemanticWeight = 0.55,
    long? BeforeSequence = null,
    DateTimeOffset? OccurredFrom = null,
    DateTimeOffset? OccurredTo = null,
    DateTimeOffset? ValidAt = null,
    bool IncludeSuperseded = true,
    string? PreferredWorkspace = null);

public sealed record MemoryCitation(
    string VersionId,
    string Label,
    string? SourceUri,
    DateTimeOffset OccurredAt,
    string ContentHash);

public sealed record MemoryEvidence(
    string Status,
    double Confidence,
    bool HasPrimarySource,
    bool IsSuperseded,
    IReadOnlyList<string> Contradicts);

public sealed record MemoryHit(
    MemoryAtom Atom,
    double Score,
    double TextScore,
    double SemanticScore,
    MemoryCitation? Citation = null,
    MemoryEvidence? Evidence = null,
    KnowledgeRetrievalEvidence? Knowledge = null);

public sealed record KnowledgeRetrievalEvidence(
    double Score,
    IReadOnlyList<string> Reasons);

public sealed record MemoryWriteResult(string VersionId, string LogicalId, long Sequence, bool Created);

public sealed record SummaryRequest(
    string Text,
    string? Project = null,
    bool Persist = true,
    string? LogicalId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record SummaryResult(string Summary, MemoryWriteResult? StoredMemory, string Model);

public sealed record IntegrityReport(
    bool IsValid,
    long AtomCount,
    long VectorCount,
    long AuditCount,
    IReadOnlyList<string> Problems);

public sealed record MemoryStatus(
    string Status,
    string StorageRoot,
    long AtomCount,
    long VectorCount,
    long AuditCount);

public sealed record KnowledgeEntity(
    string EntityId,
    string EntityType,
    string Label);

public sealed record KnowledgeRelation(
    string RelationId,
    string FromEntityId,
    string ToEntityId,
    string RelationType,
    string EvidenceClass,
    double Confidence,
    string SourceVersionId);

public sealed record KnowledgeProjectionSnapshot(
    string VersionId,
    string ProjectorVersion,
    DateTimeOffset ProjectedAt,
    IReadOnlyList<KnowledgeEntity> Entities,
    IReadOnlyList<KnowledgeRelation> Relations);

public sealed record KnowledgeProjectionStatus(
    string Status,
    string ProjectorVersion,
    long AtomCount,
    long ProjectedCount,
    long PendingCount,
    long FailedCount,
    long EntityCount,
    long RelationCount);

public sealed record MemoryScaleStatus(
    string Status,
    long AtomCount,
    long DatabaseBytes,
    long WalBytes,
    long PageCount,
    long FreePageCount,
    bool FullTextCoversAllHistory,
    int SemanticWindowSize,
    double EstimatedSemanticCoverage,
    long KnowledgePendingCount,
    bool AnnEvaluationRecommended);

public sealed record OperationalDiagnostics(
    string Status,
    long AtomCount,
    long VectorCount,
    long AuditCount,
    long FullTextCount,
    long TurnIndexCount,
    long TurnIndexPendingCount,
    long KnowledgeProjectedCount,
    long KnowledgePendingCount,
    long KnowledgeFailedCount,
    long EntityCount,
    long RelationCount,
    long? LastSequence,
    DateTimeOffset? LastOccurredAt,
    DateTimeOffset? LastStoredAt,
    long DatabaseBytes,
    long WalBytes,
    IReadOnlyList<string> Problems);

public sealed record ExternalGraphImportRequest(
    JsonElement Graph,
    string SourceName,
    string SourceUri,
    string? Project = null,
    string Format = "graphify-networkx-v1",
    bool Commit = false,
    string? ExpectedSha256 = null);

public sealed record ExternalGraphImportReport(
    bool Valid,
    bool Committed,
    string Format,
    string SourceSha256,
    int NodeCount,
    int EdgeCount,
    int CreatedCount,
    int ExistingCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Problems);
