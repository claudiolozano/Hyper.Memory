using System.Diagnostics;
using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

var scaleExitCode = await ScaleProfileRunner.TryRunAsync(args);
if (scaleExitCode is not null)
{
    Environment.ExitCode = scaleExitCode.Value;
    return;
}

var externalExitCode = await ExternalBenchmarkRunner.TryRunAsync(args);
if (externalExitCode is not null)
{
    Environment.ExitCode = externalExitCode.Value;
    return;
}

var root = Path.Combine(Path.GetTempPath(), "HyperMemoryEvaluation", Guid.NewGuid().ToString("N"));
var layout = StorageLayout.Create(root);
var options = Options.Create(new HyperMemoryOptions { RecentSemanticCandidateLimit = 100 });
await using var store = new SqliteMemoryStore(layout, options);
await store.InitializeAsync();

var ingest = Stopwatch.StartNew();
await Append("old-anchor", "User request:\nrecuerda el código FaroAntiguo\n\nHermes response:\nEl código histórico era 7319.", "archive", Vector(4));
await Append("story-10", "User request:\nescribe un cuento de 10 párrafos\n\nHermes response:\n# El Coleccionista de Segundos Perdidos\n\nRelato completo.", "hermes", Vector(0));
await Append("story-20", "User request:\nescribe un cuento de 20 párrafos\n\nHermes response:\n# La Arquitectura del Instante Suspendido\n\nSegundo relato.", "hermes", Vector(0));
await Append("flight-argentina", "User request:\nbusca tres vuelos baratos a Argentina\n\nHermes response:\nSe compararon tres opciones de vuelo.", "hermes", Vector(2));
await Append("region-old", "La región de despliegue es Madrid.", "atlas", Vector(3), claimKey: "atlas.region");
await Append("region-new", "La región de despliegue es París.", "atlas", Vector(3), claimKey: "atlas.region", supersedes: "region-old");
await Append("engine-origin", "User request:\ncrea el motor AlphaNebula\n\nHermes response:\nPrimera implementación.", "hermes", Vector(5), metadata: FileMetadata('A'));
await Append("engine-correction", "User request:\najusta las colisiones\n\nHermes response:\nCorrección aplicada.", "hermes", Vector(6), metadata: FileMetadata('B'));
for (var index = 0; index < 120; index++)
    await Append($"game-noise-{index:D3}", $"User request:\ncontinúa el juego fase {index}\n\nHermes response:\nFase {index} ajustada.", "hermes", Vector(1));
ingest.Stop();

while (await store.ProjectPendingKnowledgeAsync(50) > 0) { }

var scenarios = new[]
{
    new Scenario("story_exact", "cuento de 10 párrafos", "hermes", Vector(0), "story-10", "story-"),
    new Scenario("flight_topic", "vuelos baratos Argentina", "hermes", Vector(2), "flight-argentina", "flight-"),
    new Scenario("current_decision", "región despliegue", "atlas", Vector(3), "region-new", "region-new", IncludeSuperseded: false),
    new Scenario("graph_correction", "AlphaNebula", "hermes", Vector(5), "engine-correction", "engine-"),
    new Scenario("old_fts_outside_semantic_window", "FaroAntiguo 7319", "archive", Vector(7), "old-anchor", "old-anchor"),
};

var results = new List<ScenarioResult>();
foreach (var scenario in scenarios)
{
    var timer = Stopwatch.StartNew();
    var hits = await store.QueryAsync(new MemoryQuery(scenario.Query, 5, scenario.Project,
        IncludeSuperseded: scenario.IncludeSuperseded), scenario.Vector);
    timer.Stop();
    var ids = hits.Select(hit => hit.Atom.VersionId).ToArray();
    var diagnostics = hits.Select(hit => new HitDiagnostic(hit.Atom.VersionId, Math.Round(hit.Score, 4),
        Math.Round(hit.TextScore, 4), Math.Round(hit.SemanticScore, 4),
        hit.Knowledge is null ? 0 : Math.Round(hit.Knowledge.Score, 4))).ToArray();
    var relevant = ids.Count(id => id.StartsWith(scenario.AllowedPrefix, StringComparison.Ordinal));
    var expectedRank = Array.IndexOf(ids, scenario.ExpectedVersion);
    results.Add(new ScenarioResult(scenario.Name, expectedRank >= 0, expectedRank == 0,
        expectedRank < 0 ? 0 : 1d / (expectedRank + 1),
        ids.Length == 0 ? 1 : relevant / (double)ids.Length, timer.Elapsed.TotalMilliseconds, ids,
        diagnostics, hits.Any(hit => hit.Atom.VersionId == scenario.ExpectedVersion && hit.Knowledge is not null)));
}

var scale = await store.GetScaleStatusAsync();
var integrity = await store.VerifyIntegrityAsync();
var latencies = results.Select(result => result.LatencyMs).Order().ToArray();
var recall = results.Count(result => result.Recalled) / (double)results.Count;
var top1Accuracy = results.Count(result => result.Top1Correct) / (double)results.Count;
var meanReciprocalRank = results.Average(result => result.ReciprocalRank);
var topicalPrecision = results.Average(result => result.TopicalPrecision);
var report = new
{
    schemaVersion = 1,
    generatedAt = DateTimeOffset.UtcNow,
    corpus = new { atoms = scale.AtomCount, ingestMs = Math.Round(ingest.Elapsed.TotalMilliseconds, 2), databaseBytes = scale.DatabaseBytes, walBytes = scale.WalBytes },
    metrics = new
    {
        recallAt5 = Math.Round(recall, 4),
        groundedTop1Accuracy = Math.Round(top1Accuracy, 4),
        meanReciprocalRank = Math.Round(meanReciprocalRank, 4),
        topicalPrecisionAt5 = Math.Round(topicalPrecision, 4),
        topicDriftRate = Math.Round(1 - topicalPrecision, 4),
        latencyP50Ms = Math.Round(Percentile(latencies, 0.50), 2),
        latencyP95Ms = Math.Round(Percentile(latencies, 0.95), 2),
        graphExpansionRecoveredCorrection = results.Single(result => result.Name == "graph_correction").GraphEvidence,
        oldFullTextRecallOutsideSemanticWindow = results.Single(result => result.Name == "old_fts_outside_semantic_window").Recalled,
        integrityValid = integrity.IsValid,
        supersededLeakRate = results.Single(result => result.Name == "current_decision").ReturnedVersionIds.Contains("region-old") ? 1 : 0,
    },
    thresholds = new { minimumRecallAt5 = 1.0, minimumGroundedTop1Accuracy = 1.0, minimumTopicalPrecisionAt5 = 0.80, maximumSupersededLeakRate = 0, integrityRequired = true },
    passed = recall >= 1.0 && top1Accuracy >= 1.0 && topicalPrecision >= 0.80 && integrity.IsValid &&
        !results.Single(result => result.Name == "current_decision").ReturnedVersionIds.Contains("region-old"),
    scenarios = results,
};

var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
var outputIndex = Array.IndexOf(args, "--output");
if (outputIndex >= 0 && outputIndex + 1 < args.Length)
{
    var outputPath = Path.GetFullPath(args[outputIndex + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(outputPath, json);
}
Environment.ExitCode = report.passed ? 0 : 1;

async Task Append(string id, string content, string project, EmbeddingVector vector,
    Dictionary<string, string>? metadata = null, string? claimKey = null, string? supersedes = null)
{
    await store.AppendAsync(new MemoryWriteRequest(content, EventId: id, Project: project, Source: "evaluation",
        Metadata: metadata, SupersedesVersionId: supersedes, ClaimKey: claimKey), vector);
}

static Dictionary<string, string> FileMetadata(char hashCharacter) => new()
{
    ["kind"] = "conversation-turn",
    ["workspace"] = "D:/evaluation/game",
    ["artifacts.verifiedFiles"] = JsonSerializer.Serialize(new[]
    {
        new { path = "src/engine.js", sha256 = new string(hashCharacter, 64), size = 100, tool = "write_file", verification = "workspace-file-hash" }
    })
};

static EmbeddingVector Vector(int index)
{
    var values = new float[8];
    values[index] = 1;
    return new EmbeddingVector(values, "evaluation", "orthogonal-v1");
}

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0) return 0;
    var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

internal sealed record Scenario(string Name, string Query, string Project, EmbeddingVector Vector,
    string ExpectedVersion, string AllowedPrefix, bool IncludeSuperseded = true);

internal sealed record ScenarioResult(string Name, bool Recalled, bool Top1Correct, double ReciprocalRank,
    double TopicalPrecision, double LatencyMs,
    IReadOnlyList<string> ReturnedVersionIds, IReadOnlyList<HitDiagnostic> Hits, bool GraphEvidence);

internal sealed record HitDiagnostic(string VersionId, double Score, double TextScore, double SemanticScore,
    double KnowledgeScore);
