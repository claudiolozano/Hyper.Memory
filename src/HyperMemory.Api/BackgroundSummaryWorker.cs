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
