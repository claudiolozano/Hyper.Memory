using HyperMemory.Core;
using HyperMemory.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var storageArgument = GetArgument(args, "--storage-root") ?? Environment.GetEnvironmentVariable("HYPERMEMORY_STORAGE");
if (!string.IsNullOrWhiteSpace(storageArgument))
    builder.Configuration[$"{HyperMemoryOptions.SectionName}:StorageBasePath"] = storageArgument;

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5077");
builder.Services.AddProblemDetails();
builder.Services.AddHyperMemory(builder.Configuration);
builder.Services.AddSingleton<BackgroundSummaryQueue>();
builder.Services.AddHostedService<BackgroundSummaryWorker>();

var app = builder.Build();
var store = app.Services.GetRequiredService<IMemoryStore>();
await store.InitializeAsync();

app.UseExceptionHandler();
app.MapGet("/health", async (IMemoryStore memoryStore, CancellationToken ct) =>
{
    var integrity = await memoryStore.VerifyIntegrityAsync(ct);
    return integrity.IsValid ? Results.Ok(new { product = "HyperMemory", status = "healthy", integrity }) : Results.Problem("Integrity verification failed", statusCode: 503, extensions: new Dictionary<string, object?> { ["integrity"] = integrity });
});
app.MapPost("/memory/upsert", async (MemoryWriteRequest request, IMemoryService service, BackgroundSummaryQueue summaries, CancellationToken ct) =>
{
    var result = await service.UpsertAsync(request, ct);
    summaries.TryQueue(request, result);
    return Results.Ok(result);
});
app.MapPost("/memory/query", async (MemoryQuery request, IMemoryService service, CancellationToken ct) =>
    Results.Ok(await service.QueryAsync(request, ct)));
app.MapPost("/memory/summarize", async (SummaryRequest request, IMemoryService service, CancellationToken ct) =>
    Results.Ok(await service.SummarizeAsync(request, ct)));
app.MapGet("/memory/integrity", async (IMemoryStore memoryStore, CancellationToken ct) =>
    Results.Ok(await memoryStore.VerifyIntegrityAsync(ct)));
await app.RunAsync();

static string? GetArgument(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

public partial class Program;
