using HyperMemory.Core;
using HyperMemory.Infrastructure;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var storageArgument = GetArgument(args, "--storage-root") ?? Environment.GetEnvironmentVariable("HYPERMEMORY_STORAGE");
var authTokenFile = GetArgument(args, "--auth-token-file");
if (!string.IsNullOrWhiteSpace(storageArgument))
    builder.Configuration[$"{HyperMemoryOptions.SectionName}:StorageBasePath"] = storageArgument;

builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5077");
builder.Services.AddProblemDetails();
builder.Services.AddHyperMemory(builder.Configuration);
builder.Services.AddSingleton<BackgroundSummaryQueue>();
builder.Services.AddHostedService<BackgroundSummaryWorker>();
builder.Services.AddHostedService<IndexMaintenanceWorker>();
builder.Services.AddHostedService<KnowledgeProjectionWorker>();
builder.Services.AddHostedService<ScaleMaintenanceWorker>();

var app = builder.Build();
var authToken = !string.IsNullOrWhiteSpace(authTokenFile) && File.Exists(authTokenFile)
    ? File.ReadAllText(authTokenFile).Trim()
    : app.Configuration["HyperMemory:AuthToken"] ?? string.Empty;
var store = app.Services.GetRequiredService<IMemoryStore>();
await store.InitializeAsync();

app.UseExceptionHandler();
if (!string.IsNullOrWhiteSpace(authToken))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/memory"))
        {
            var supplied = context.Request.Headers["X-HyperMemory-Token"].ToString();
            var expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(authToken));
            var suppliedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        await next();
    });
}
app.MapGet("/live", () => Results.Ok(new { product = "HyperMemory", status = "alive", apiVersion = "2.0.0", processId = Environment.ProcessId }));
app.MapGet("/health", async (IMemoryStore memoryStore, CancellationToken ct) =>
{
    var status = await memoryStore.GetStatusAsync(ct);
    return status.Status == "healthy"
        ? Results.Ok(new
        {
            product = "HyperMemory",
            status = status.Status,
            apiVersion = "2.0.0",
            processId = Environment.ProcessId,
            storageRoot = status.StorageRoot,
            counts = status,
            integrity = new { isValid = true, atomCount = status.AtomCount, vectorCount = status.VectorCount, auditCount = status.AuditCount, problems = Array.Empty<string>() }
        })
        : Results.Problem("Memory counts are inconsistent", statusCode: 503, extensions: new Dictionary<string, object?> { ["status"] = status });
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
app.MapGet("/memory/knowledge/status", async (IKnowledgeProjectionStore projection, CancellationToken ct) =>
    Results.Ok(await projection.GetKnowledgeProjectionStatusAsync(ct)));
app.MapGet("/memory/knowledge/{versionId}", async (string versionId, IKnowledgeProjectionStore projection, CancellationToken ct) =>
{
    var snapshot = await projection.GetKnowledgeProjectionAsync(versionId, ct);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});
app.MapPost("/memory/knowledge/rebuild", async (IKnowledgeProjectionStore projection, CancellationToken ct) =>
{
    await projection.RebuildKnowledgeProjectionAsync(ct);
    return Results.Accepted(value: new { status = "rebuild_queued", sourceOfTruth = "memory_atoms" });
});
app.MapGet("/memory/scale", async (IScaleMaintenanceStore scale, CancellationToken ct) =>
    Results.Ok(await scale.GetScaleStatusAsync(ct)));
app.MapPost("/memory/maintenance", async (IScaleMaintenanceStore scale, CancellationToken ct) =>
{
    await scale.RunScaleMaintenanceAsync(ct);
    return Results.Ok(new { status = "optimized", destructive = false });
});
app.MapGet("/memory/diagnostics", async (IOperationalDiagnosticsStore diagnostics, CancellationToken ct) =>
    Results.Ok(await diagnostics.GetOperationalDiagnosticsAsync(ct)));
app.MapPost("/memory/import/graph", async (ExternalGraphImportRequest request, IExternalGraphImportService importer, CancellationToken ct) =>
{
    var report = await importer.ImportAsync(request, ct);
    return report.Valid ? Results.Ok(report) : Results.BadRequest(report);
});
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal"))
{
    app.MapPost("/memory/operational/events", async (
        OperationalEventWriteRequest request, IOperationalEventStore events, CancellationToken ct) =>
        Results.Ok(await events.AppendAsync(request, ct)));
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableProjectState"))
{
    app.MapPost("/memory/operational/project", async (
        OperationalScope scope, IProjectStateProjectionStore projection, CancellationToken ct) =>
    {
        await projection.ProjectPendingAsync(scope, 10_000, ct);
        var state = await projection.GetCurrentAsync(scope, ct);
        return state is null ? Results.NotFound() : Results.Ok(state);
    });
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableProjectState") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableSelectiveMemoryRouter"))
{
    app.MapPost("/memory/operational/context", async (
        MemoryContextRequest request, IOperationalMemoryRouter router, CancellationToken ct) =>
        Results.Ok(await router.BuildContextAsync(request, ct)));
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableCapabilityRouting"))
{
    app.MapPost("/memory/operational/capabilities/route", async (
        CapabilityRouteRequest request, ICapabilityRouter router, CancellationToken ct) =>
        Results.Ok(await router.ResolveAsync(request.Scope, request.Requirements, ct)));
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableProjectState") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableValidationMemory") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableCheckpoints"))
{
    app.MapPost("/memory/operational/checkpoints", async (
        CheckpointRequest request, ICheckpointService checkpoints, CancellationToken ct) =>
        Results.Ok(await checkpoints.CreateAsync(request, ct)));
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableProjectState") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableValidationMemory") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableTaskGraph"))
{
    app.MapPost("/memory/operational/completion", async (
        CompletionAssessmentRequest request, ICompletionEvaluator evaluator, CancellationToken ct) =>
        Results.Ok(await evaluator.EvaluateAsync(request, ct)));
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableProjectState") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableValidationMemory") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableContracts"))
{
    app.MapPost("/memory/operational/artifacts/observe", async (
        ArtifactObservationRequest request, IContractInvalidationService invalidation, CancellationToken ct) =>
        Results.Ok(await invalidation.ObserveArtifactChangeAsync(request.Artifact, request.Scope, ct)));
}
if (app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableEventJournal") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableProjectState") &&
    app.Configuration.GetValue<bool>("HyperMemory:Operational:EnableWorkingMemory"))
{
    app.MapPost("/memory/operational/working/upsert", async (
        WorkingMemoryRequest request, IWorkingProjectMemoryService working, CancellationToken ct) =>
        Results.Ok(await working.UpsertWorkingAsync(request.Change, request.Scope, ct)));
    app.MapPost("/memory/operational/working/remove", async (
        WorkingMemoryRemoveRequest request, IWorkingProjectMemoryService working, CancellationToken ct) =>
    {
        await working.RemoveWorkingAsync(request.Key, request.Scope, ct);
        return Results.Ok(new { removed = true });
    });
    app.MapPost("/memory/operational/working", async (
        OperationalScope scope, IWorkingProjectMemoryService working, CancellationToken ct) =>
        Results.Ok(await working.GetActiveWorkingAsync(scope, ct)));
    app.MapPost("/memory/operational/statements", async (
        ProjectStatementRequest request, IWorkingProjectMemoryService working, CancellationToken ct) =>
        Results.Ok(await working.RecordStatementAsync(request.Change, request.Scope, ct)));
}
await app.RunAsync();

static string? GetArgument(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

public partial class Program;
