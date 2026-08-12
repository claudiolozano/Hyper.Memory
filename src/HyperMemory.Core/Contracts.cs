namespace HyperMemory.Core;

public interface IEmbeddingGenerator
{
    Task<EmbeddingVector> GenerateAsync(string text, CancellationToken cancellationToken = default);
}

public interface ITextSummarizer
{
    Task<(string Text, string Model)> SummarizeAsync(string text, CancellationToken cancellationToken = default);
}

public interface IMemoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<MemoryWriteResult> AppendAsync(MemoryWriteRequest request, EmbeddingVector embedding, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, EmbeddingVector embedding, CancellationToken cancellationToken = default);
    Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}

public interface IMemoryService
{
    Task<MemoryWriteResult> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default);
    Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default);
}
