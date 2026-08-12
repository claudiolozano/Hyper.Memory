using System.Diagnostics;
using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

internal static class ScaleProfileRunner
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        var count = IntArgument(args, "--synthetic-count");
        if (count is null) return null;
        if (count is < 1_000 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(args), "--synthetic-count must be between 1,000 and 1,000,000.");
        var targetCount = count.Value;
        var output = StringArgument(args, "--output");
        var root = Path.Combine(Path.GetTempPath(), "HyperMemoryScale", Guid.NewGuid().ToString("N"));
        var layout = StorageLayout.Create(root);
        var options = Options.Create(new HyperMemoryOptions { RecentSemanticCandidateLimit = 5_000 });
        await using var store = new SqliteMemoryStore(layout, options);
        await store.InitializeAsync();
        var vector = new EmbeddingVector([1, 0, 0, 0], "scale", "fixed-v1");

        var ingest = Stopwatch.StartNew();
        for (var index = 0; index < targetCount; index++)
        {
            var anchor = $"anchor-{index:D8}";
            await store.AppendAsync(new MemoryWriteRequest(
                $"User request:\nrecord synthetic history {anchor}\n\nHermes response:\n" +
                $"Stored scale topic {index % 100:D3} sequence {index}.",
                EventId: $"scale-{index:D8}", Project: "synthetic-scale", Source: "scale-profile",
                OccurredAt: DateTimeOffset.UnixEpoch.AddMinutes(index)), vector);
            if ((index + 1) % 10_000 == 0)
                Console.Error.WriteLine($"ingested {index + 1}/{targetCount}");
        }
        ingest.Stop();

        var projection = Stopwatch.StartNew();
        long projected = 0;
        int batch;
        do
        {
            batch = await store.ProjectPendingKnowledgeAsync(5_000);
            projected += batch;
            if (batch > 0 && projected % 10_000 == 0)
                Console.Error.WriteLine($"projected {projected}/{targetCount}");
        } while (batch > 0);
        projection.Stop();

        var queryIndexes = new[] { 0, targetCount / 4, targetCount / 2, targetCount * 3 / 4, targetCount - 1 };
        var queryResults = new List<object>();
        var latencies = new List<double>();
        var recalled = 0;
        foreach (var index in queryIndexes.Distinct())
        {
            var timer = Stopwatch.StartNew();
            var hits = await store.QueryAsync(new MemoryQuery($"anchor-{index:D8}", 5, "synthetic-scale"), vector);
            timer.Stop();
            latencies.Add(timer.Elapsed.TotalMilliseconds);
            var expected = $"scale-{index:D8}";
            var rank = hits.Select(hit => hit.Atom.VersionId).ToList().IndexOf(expected);
            if (rank >= 0) recalled++;
            queryResults.Add(new { index, expected, rank = rank < 0 ? (int?)null : rank + 1, latencyMs = Math.Round(timer.Elapsed.TotalMilliseconds, 2) });
        }

        var maintenance = Stopwatch.StartNew();
        await store.RunScaleMaintenanceAsync();
        maintenance.Stop();
        var scale = await store.GetScaleStatusAsync();
        var diagnostics = await store.GetOperationalDiagnosticsAsync();
        var integrityTimer = Stopwatch.StartNew();
        var integrity = await store.VerifyIntegrityAsync();
        integrityTimer.Stop();
        latencies.Sort();

        var report = new
        {
            schemaVersion = 1,
            profile = "synthetic-multi-year",
            generatedAt = DateTimeOffset.UtcNow,
            corpus = new
            {
                requested = targetCount,
                atoms = scale.AtomCount,
                projected,
                databaseBytes = scale.DatabaseBytes,
                walBytes = scale.WalBytes,
                immutableEventFiles = Directory.EnumerateFiles(Path.Combine(layout.Root, "events"), "*.json", SearchOption.AllDirectories).LongCount()
            },
            throughput = new
            {
                ingestSeconds = Math.Round(ingest.Elapsed.TotalSeconds, 2),
                memoriesPerSecond = Math.Round(targetCount / Math.Max(ingest.Elapsed.TotalSeconds, 0.001), 2),
                projectionSeconds = Math.Round(projection.Elapsed.TotalSeconds, 2),
                projectionsPerSecond = Math.Round(targetCount / Math.Max(projection.Elapsed.TotalSeconds, 0.001), 2),
                maintenanceSeconds = Math.Round(maintenance.Elapsed.TotalSeconds, 2),
                integritySeconds = Math.Round(integrityTimer.Elapsed.TotalSeconds, 2)
            },
            retrieval = new
            {
                recallAt5 = Math.Round(recalled / (double)queryIndexes.Distinct().Count(), 4),
                latencyP50Ms = Math.Round(Percentile(latencies, .50), 2),
                latencyP95Ms = Math.Round(Percentile(latencies, .95), 2),
                queries = queryResults
            },
            scale = new
            {
                scale.FullTextCoversAllHistory,
                scale.EstimatedSemanticCoverage,
                scale.AnnEvaluationRecommended,
                knowledgePending = scale.KnowledgePendingCount,
                diagnostics.Status,
                diagnostics.Problems
            },
            integrity = new { integrity.IsValid, integrity.Problems },
            passed = scale.AtomCount == targetCount && projected == targetCount && recalled == queryIndexes.Distinct().Count() &&
                     scale.FullTextCoversAllHistory && scale.KnowledgePendingCount == 0 && integrity.IsValid
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(output))
        {
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json);
        }
        return report.passed ? 0 : 1;
    }

    private static int? IntArgument(string[] args, string name)
    {
        var value = StringArgument(args, name);
        return value is null ? null : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? StringArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
