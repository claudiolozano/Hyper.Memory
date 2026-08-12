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
    Task<MemoryStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}

public interface IMemoryService
{
    Task<MemoryWriteResult> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryHit>> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default);
    Task<SummaryResult> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken = default);
}

public interface IKnowledgeProjectionStore
{
    Task<int> ProjectPendingKnowledgeAsync(int batchSize = 100, CancellationToken cancellationToken = default);
    Task<KnowledgeProjectionStatus> GetKnowledgeProjectionStatusAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeProjectionSnapshot?> GetKnowledgeProjectionAsync(string versionId, CancellationToken cancellationToken = default);
    Task RebuildKnowledgeProjectionAsync(CancellationToken cancellationToken = default);
}

public interface IScaleMaintenanceStore
{
    Task<MemoryScaleStatus> GetScaleStatusAsync(CancellationToken cancellationToken = default);
    Task RunScaleMaintenanceAsync(CancellationToken cancellationToken = default);
}

public interface IOperationalDiagnosticsStore
{
    Task<OperationalDiagnostics> GetOperationalDiagnosticsAsync(CancellationToken cancellationToken = default);
}

public interface IExternalGraphImportService
{
    Task<ExternalGraphImportReport> ImportAsync(ExternalGraphImportRequest request,
        CancellationToken cancellationToken = default);
}
