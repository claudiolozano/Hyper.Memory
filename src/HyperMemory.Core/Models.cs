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
    bool IncludeSuperseded = true);

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
    MemoryEvidence? Evidence = null);

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
