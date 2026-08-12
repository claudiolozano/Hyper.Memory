using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperMemory.Core;
using Microsoft.Extensions.Options;

namespace HyperMemory.Infrastructure;

public sealed class OllamaModelResolver(HttpClient client, IOptions<HyperMemoryOptions> options)
{
    public async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.OllamaModel)) return options.Value.OllamaModel;
        try
        {
            using var response = await client.GetAsync("api/ps", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return json.RootElement.GetProperty("models").EnumerateArray()
                .Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }
        catch (HttpRequestException) { return null; }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}

public sealed class AdaptiveEmbeddingGenerator(
    HttpClient client,
    OllamaModelResolver models,
    IOptions<HyperMemoryOptions> options) : IEmbeddingGenerator
{
    public async Task<EmbeddingVector> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        var model = options.Value.PreferOllamaEmbeddings
            ? await models.ResolveAsync(cancellationToken)
            : null;
        if (model is not null)
        {
            try
            {
                using var response = await client.PostAsJsonAsync("api/embed", new { model, input = text }, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                var values = json.RootElement.GetProperty("embeddings")[0].EnumerateArray()
                    .Select(x => x.GetSingle()).ToArray();
                if (values.Length > 0) return new EmbeddingVector(Normalize(values), "ollama", model);
            }
            catch (HttpRequestException) when (options.Value.AllowDeterministicEmbeddingFallback) { }
            catch (Exception error) when (options.Value.AllowDeterministicEmbeddingFallback &&
                error is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { }
        }

        if (!options.Value.AllowDeterministicEmbeddingFallback)
            throw new InvalidOperationException("The active Ollama model could not produce an embedding.");
        return new EmbeddingVector(HashEmbedding(text), "local", "sha256-token-v1");
    }

    private static float[] HashEmbedding(string text)
    {
        const int dimensions = 384;
        var vector = new float[dimensions];
        var tokens = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            for (var i = 0; i < 8; i++)
            {
                var index = BitConverter.ToUInt16(bytes, i * 2) % dimensions;
                vector[index] += (bytes[16 + i] & 1) == 0 ? 1f : -1f;
            }
        }
        return Normalize(vector);
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        if (norm <= 0) return vector;
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }
}

public sealed class AdaptiveTextSummarizer(HttpClient client, OllamaModelResolver models) : ITextSummarizer
{
    public async Task<(string Text, string Model)> SummarizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var model = await models.ResolveAsync(cancellationToken);
        if (model is not null)
        {
            try
            {
                var prompt = "Summarize the following durable project context. Preserve decisions, constraints, file names, errors and unresolved tasks. Do not invent facts.\n\n" + text;
                using var response = await client.PostAsJsonAsync("api/generate", new { model, prompt, stream = false }, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                var summary = json.RootElement.GetProperty("response").GetString();
                if (!string.IsNullOrWhiteSpace(summary)) return (summary.Trim(), model);
            }
            catch (HttpRequestException) { }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }

        var sentences = text.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fallback = string.Join(". ", sentences.Take(12));
        return (fallback.Length == 0 ? text : fallback + ".", "local-extractive-v1");
    }
}
