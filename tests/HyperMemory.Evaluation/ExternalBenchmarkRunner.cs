using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperMemory.Core;
using HyperMemory.Infrastructure;
using Microsoft.Extensions.Options;

internal static class ExternalBenchmarkRunner
{
    public static async Task<int?> TryRunAsync(string[] args)
    {
        var dataset = Argument(args, "--dataset");
        if (dataset is null) return null;

        var format = (Argument(args, "--format") ?? InferFormat(dataset)).ToLowerInvariant();
        var limit = int.TryParse(Argument(args, "--limit"), out var parsed) ? Math.Max(1, parsed) : int.MaxValue;
        var topK = int.TryParse(Argument(args, "--top-k"), out parsed) ? Math.Clamp(parsed, 1, 100) : 5;
        var output = Argument(args, "--output");
        var fullPath = Path.GetFullPath(dataset);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Benchmark dataset not found.", fullPath);

        await using var datasetStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(datasetStream);
        var timer = Stopwatch.StartNew();
        var results = format switch
        {
            "locomo" => await RunLocomoAsync(document.RootElement, limit, topK),
            "longmemeval" => await RunLongMemEvalAsync(document.RootElement, limit, topK),
            _ => throw new ArgumentException("--format must be 'locomo' or 'longmemeval'."),
        };
        timer.Stop();

        var scored = results.Where(item => !item.Abstention && item.ExpectedIds.Count > 0).ToArray();
        var report = new
        {
            schemaVersion = 1,
            benchmark = format,
            source = new
            {
                file = Path.GetFileName(fullPath),
                sha256 = await FileSha256Async(fullPath),
            },
            configuration = new { topK, limit = limit == int.MaxValue ? (int?)null : limit },
            metrics = new
            {
                evaluatedQuestions = results.Count,
                scoredRetrievalQuestions = scored.Length,
                abstentionQuestions = results.Count(item => item.Abstention),
                evidenceRecallAtK = scored.Length == 0 ? 0 : Math.Round(scored.Average(item => item.Recall), 4),
                evidenceHitRateAtK = scored.Length == 0 ? 0 : Math.Round(scored.Count(item => item.Recall > 0) / (double)scored.Length, 4),
                meanReciprocalRank = scored.Length == 0 ? 0 : Math.Round(scored.Average(item => item.ReciprocalRank), 4),
                latencyP50Ms = Math.Round(Percentile(results.Select(item => item.LatencyMs).Order().ToArray(), .50), 2),
                latencyP95Ms = Math.Round(Percentile(results.Select(item => item.LatencyMs).Order().ToArray(), .95), 2),
                elapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 2),
            },
            byCategory = scored.GroupBy(item => item.Category).OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => new
                {
                    questions = group.Count(),
                    evidenceRecallAtK = Math.Round(group.Average(item => item.Recall), 4),
                    meanReciprocalRank = Math.Round(group.Average(item => item.ReciprocalRank), 4),
                }),
            notes = new[]
            {
                "This adapter measures retrieval against official evidence identifiers; it does not claim answer-generation correctness.",
                "Abstention items without evidence are reported separately and are not included in retrieval recall.",
                "The source SHA-256 makes runs reproducible and detects silent dataset changes.",
            },
            results,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (output is not null)
        {
            var outputPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, json);
        }
        return 0;
    }

    private static async Task<List<BenchmarkResult>> RunLongMemEvalAsync(JsonElement root, int limit, int topK)
    {
        RequireArray(root, "LongMemEval root");
        var results = new List<BenchmarkResult>();
        foreach (var item in root.EnumerateArray().Take(limit))
        {
            var questionId = RequiredString(item, "question_id");
            var question = RequiredString(item, "question");
            var category = RequiredString(item, "question_type");
            var sessionIds = RequiredArray(item, "haystack_session_ids").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var dates = RequiredArray(item, "haystack_dates").EnumerateArray().Select(value => value.GetString()).ToArray();
            var sessions = RequiredArray(item, "haystack_sessions").EnumerateArray().ToArray();
            if (sessionIds.Length != sessions.Length || dates.Length != sessions.Length)
                throw new InvalidDataException($"LongMemEval item '{questionId}' has misaligned session arrays.");

            var expected = RequiredArray(item, "answer_session_ids").EnumerateArray()
                .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
            var records = new List<BenchmarkMemory>(sessions.Length);
            for (var index = 0; index < sessions.Length; index++)
            {
                RequireArray(sessions[index], $"LongMemEval session {sessionIds[index]}");
                var content = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(dates[index])) content.AppendLine($"Session date: {dates[index]}");
                foreach (var turn in sessions[index].EnumerateArray())
                    content.AppendLine($"{RequiredString(turn, "role")}: {RequiredString(turn, "content")}");
                records.Add(new BenchmarkMemory(sessionIds[index], content.ToString(), dates[index]));
            }
            results.Add(await EvaluateIsolatedAsync(questionId, category, question, records, expected,
                questionId.EndsWith("_abs", StringComparison.OrdinalIgnoreCase), topK));
        }
        return results;
    }

    private static async Task<List<BenchmarkResult>> RunLocomoAsync(JsonElement root, int limit, int topK)
    {
        RequireArray(root, "LoCoMo root");
        var results = new List<BenchmarkResult>();
        var remaining = limit;
        foreach (var sample in root.EnumerateArray())
        {
            if (remaining <= 0) break;
            var sampleId = RequiredString(sample, "sample_id");
            var conversation = RequiredObject(sample, "conversation");
            var memories = new List<BenchmarkMemory>();
            foreach (var property in conversation.EnumerateObject()
                         .Where(property => property.Name.StartsWith("session_", StringComparison.Ordinal) &&
                             !property.Name.EndsWith("_date_time", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.Array)
                         .OrderBy(property => SessionNumber(property.Name)))
            {
                var number = SessionNumber(property.Name);
                var dateName = $"session_{number}_date_time";
                var date = conversation.TryGetProperty(dateName, out var dateValue) ? dateValue.GetString() : null;
                var content = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(date)) content.AppendLine($"Session date: {date}");
                foreach (var turn in property.Value.EnumerateArray())
                    content.AppendLine($"[{RequiredString(turn, "dia_id")}] {RequiredString(turn, "speaker")}: {RequiredString(turn, "text")}");
                memories.Add(new BenchmarkMemory($"D{number}", content.ToString(), date));
            }

            foreach (var qa in RequiredArray(sample, "qa").EnumerateArray().Take(remaining))
            {
                var question = RequiredString(qa, "question");
                var evidence = RequiredArray(qa, "evidence").EnumerateArray()
                    .SelectMany(value => (value.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(DialogSession).Where(value => value is not null).Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);
                var category = qa.GetProperty("category").ToString();
                results.Add(await EvaluateIsolatedAsync($"{sampleId}:{results.Count}", category, question, memories,
                    evidence, evidence.Count == 0, topK));
                remaining--;
                if (remaining <= 0) break;
            }
        }
        return results;
    }

    private static async Task<BenchmarkResult> EvaluateIsolatedAsync(string id, string category, string question,
        IReadOnlyList<BenchmarkMemory> memories, IReadOnlySet<string> expected, bool abstention, int topK)
    {
        var root = Path.Combine(Path.GetTempPath(), "HyperMemoryBenchmarks", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = StorageLayout.Create(root);
            var options = Options.Create(new HyperMemoryOptions { RecentSemanticCandidateLimit = Math.Max(100, memories.Count) });
            await using var store = new SqliteMemoryStore(layout, options);
            await store.InitializeAsync();
            foreach (var memory in memories)
            {
                DateTimeOffset? occurred = DateTimeOffset.TryParse(memory.Date, out var parsed) ? parsed : null;
                await store.AppendAsync(new MemoryWriteRequest(memory.Content, EventId: memory.Id, Project: id,
                    Source: "benchmark", OccurredAt: occurred), Embed(memory.Content));
            }

            var timer = Stopwatch.StartNew();
            var hits = await store.QueryAsync(new MemoryQuery(question, topK, id), Embed(question));
            timer.Stop();
            var returned = hits.Select(hit => hit.Atom.VersionId).ToArray();
            var matched = returned.Where(expected.Contains).Distinct(StringComparer.Ordinal).ToArray();
            var first = Array.FindIndex(returned, expected.Contains);
            return new BenchmarkResult(id, category, abstention, expected.Order().ToArray(), returned,
                expected.Count == 0 ? 0 : matched.Length / (double)expected.Count,
                first < 0 ? 0 : 1d / (first + 1), timer.Elapsed.TotalMilliseconds);
        }
        finally
        {
            TryDeleteTemporaryStore(root);
        }
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream)).ToLowerInvariant();
    }

    private static void TryDeleteTemporaryStore(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static EmbeddingVector Embed(string text)
    {
        const int dimensions = 384;
        var vector = new float[dimensions];
        foreach (var token in text.ToLowerInvariant().Split((char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            for (var index = 0; index < 8; index++)
                vector[BitConverter.ToUInt16(bytes, index * 2) % dimensions] += (bytes[16 + index] & 1) == 0 ? 1f : -1f;
        }
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
            for (var index = 0; index < vector.Length; index++) vector[index] = (float)(vector[index] / norm);
        return new EmbeddingVector(vector, "benchmark", "sha256-token-v1");
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string InferFormat(string path) => Path.GetFileName(path).Contains("locomo", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileName(path).Contains("longmemeval", StringComparison.OrdinalIgnoreCase) ? "locomo" : "longmemeval";

    private static JsonElement RequiredArray(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Array);
    private static JsonElement RequiredObject(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Object);
    private static string RequiredString(JsonElement parent, string name)
    {
        var value = Required(parent, name, JsonValueKind.String).GetString();
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"Required field '{name}' is empty.") : value;
    }
    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidDataException($"Required field '{name}' must be {kind}.");
        return value;
    }
    private static void RequireArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{name} must be an array.");
    }
    private static int SessionNumber(string name) => int.TryParse(name.AsSpan("session_".Length), out var number) ? number : int.MaxValue;
    private static string? DialogSession(string evidence)
    {
        if (evidence.Length < 2 || char.ToUpperInvariant(evidence[0]) != 'D') return null;
        var colon = evidence.IndexOf(':');
        return colon > 1 && int.TryParse(evidence.AsSpan(1, colon - 1), out var number) ? $"D{number}" : null;
    }
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        return sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private sealed record BenchmarkMemory(string Id, string Content, string? Date);
    private sealed record BenchmarkResult(string Id, string Category, bool Abstention,
        IReadOnlyList<string> ExpectedIds, IReadOnlyList<string> ReturnedIds, double Recall,
        double ReciprocalRank, double LatencyMs);
}
