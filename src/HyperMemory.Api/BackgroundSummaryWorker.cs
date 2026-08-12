using System.Threading.Channels;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

public sealed class BackgroundSummaryQueue(IOptions<HyperMemoryOptions> options)
{
    private readonly Channel<SummaryRequest> _channel = Channel.CreateBounded<SummaryRequest>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public ChannelReader<SummaryRequest> Reader => _channel.Reader;

    public bool TryQueue(MemoryWriteRequest request, MemoryWriteResult stored)
    {
        if (!options.Value.EnableBackgroundSummaries) return false;
        if (request.Content.Length < options.Value.BackgroundSummaryThresholdCharacters) return false;
        var metadata = new Dictionary<string, string>(request.Metadata ?? new Dictionary<string, string>())
        {
            ["summary.origin_version"] = stored.VersionId,
            ["summary.mode"] = "background"
        };
        return _channel.Writer.TryWrite(new SummaryRequest(request.Content, request.Project, true,
            $"summary:{stored.LogicalId}", metadata));
    }
}

public sealed class BackgroundSummaryWorker(
    BackgroundSummaryQueue queue,
    IServiceScopeFactory scopes,
    ILogger<BackgroundSummaryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMemoryService>().SummarizeAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Background summary failed; original memory remains safely persisted.");
            }
        }
    }
}

public sealed class IndexMaintenanceWorker(
    SqliteMemoryStore store,
    ILogger<IndexMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var indexed = await store.BackfillTurnIndexBatchAsync(500, stoppingToken);
                if (indexed == 0) return;
                await Task.Delay(25, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Incremental turn-index maintenance failed."); }
    }
}

public sealed class KnowledgeProjectionWorker(
    IKnowledgeProjectionStore projection,
    IOptions<HyperMemoryOptions> options,
    ILogger<KnowledgeProjectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableKnowledgeProjection) return;
        var batchSize = Math.Clamp(options.Value.KnowledgeProjectionBatchSize, 1, 5_000);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var projected = await projection.ProjectPendingKnowledgeAsync(batchSize, stoppingToken);
                await Task.Delay(projected > 0 ? 25 : 1_000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Knowledge projection failed; immutable memory remains unaffected.");
                await Task.Delay(5_000, stoppingToken);
            }
        }
    }
}

public sealed class ScaleMaintenanceWorker(
    IScaleMaintenanceStore maintenance,
    IOptions<HyperMemoryOptions> options,
    ILogger<ScaleMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Clamp(options.Value.ScaleMaintenanceIntervalMinutes, 15, 10_080);
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Min(10, intervalMinutes)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
            do
            {
                await maintenance.RunScaleMaintenanceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Non-destructive SQLite scale maintenance failed."); }
    }
}
