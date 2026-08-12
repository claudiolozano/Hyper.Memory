namespace HyperMemory.Core;

public sealed class MemoryService(
    IMemoryStore store,
    IEmbeddingGenerator embeddings,
    ITextSummarizer summarizer) : IMemoryService
{
    public async Task<MemoryWriteResult> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        if (request.ValidFrom is not null && request.ValidTo is not null && request.ValidFrom > request.ValidTo)
            throw new ArgumentException("ValidFrom cannot be later than ValidTo.");
        if (request.StatedConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(request.StatedConfidence), "Confidence must be between 0 and 1.");
        var sanitized = Sanitize(request);
        var vector = await embeddings.GenerateAsync(sanitized.Content, cancellationToken);
        return await store.AppendAsync(sanitized, vector, cancellationToken);
    }

    private static MemoryWriteRequest Sanitize(MemoryWriteRequest request)
    {
        var (content, contentRedactions) = SensitiveDataRedactor.Redact(request.Content);
        var metadata = request.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(request.Metadata);
        var metadataRedactions = 0;
        foreach (var key in metadata.Keys.ToArray())
        {
            var (value, count) = SensitiveDataRedactor.Redact(metadata[key]);
            metadata[key] = value;
            metadataRedactions += count;
        }
        var total = contentRedactions + metadataRedactions;
        var prior = metadata.TryGetValue("privacy.redactions", out var encoded) && int.TryParse(encoded, out var parsed)
            ? Math.Max(0, parsed) : 0;
        metadata["privacy.redactions"] = (prior + total).ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["privacy.classification"] = prior + total > 0 ? "restricted-redacted" :
            metadata.GetValueOrDefault("privacy.classification") ?? "standard";
        metadata["privacy.enforcement"] = "api-central-redaction-v1";
        return request with { Content = content, Metadata = metadata };
    }

    public async Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Text);
        if (query.Limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(query.Limit));
        if (query.TextWeight < 0 || query.SemanticWeight < 0 || query.TextWeight + query.SemanticWeight <= 0)
            throw new ArgumentException("Search weights must be non-negative and at least one must be positive.");
        if (query.OccurredFrom is not null && query.OccurredTo is not null && query.OccurredFrom > query.OccurredTo)
            throw new ArgumentException("OccurredFrom cannot be later than OccurredTo.");

        var vector = await embeddings.GenerateAsync(query.Text, cancellationToken);
        var effectiveQuery = vector.Provider == "local"
            ? query with { TextWeight = 0.8, SemanticWeight = 0.2 }
            : query;
        return await store.QueryAsync(effectiveQuery, vector, cancellationToken);
    }

    public async Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var (text, model) = await summarizer.SummarizeAsync(request.Text, cancellationToken);
        MemoryWriteResult? stored = null;
        if (request.Persist)
        {
            var metadata = request.Metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(request.Metadata);
            metadata["memory.kind"] = "summary";
            metadata["summary.model"] = model;
            stored = await UpsertAsync(new MemoryWriteRequest(
                text, request.LogicalId, Project: request.Project, Source: "summary", Metadata: metadata), cancellationToken);
        }
        return new SummaryResult(text, stored, model);
    }
}
