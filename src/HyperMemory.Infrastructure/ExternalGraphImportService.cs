using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperMemory.Core;
using Microsoft.Extensions.Options;

namespace HyperMemory.Infrastructure;

public sealed partial class ExternalGraphImportService(
    IMemoryService memory,
    IOptions<HyperMemoryOptions> options) : IExternalGraphImportService
{
    private const string SupportedFormat = "graphify-networkx-v1";

    public async Task<ExternalGraphImportReport> ImportAsync(ExternalGraphImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var problems = new List<string>();
        var nodes = new List<ImportNode>();
        var edges = new List<ImportEdge>();

        ValidateEnvelope(request, problems);
        if (problems.Count == 0)
            ParseGraph(request.Graph, nodes, edges, warnings, problems);

        var sourceHash = problems.Count == 0 ? CanonicalHash(nodes, edges) : string.Empty;
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256) &&
            !string.Equals(request.ExpectedSha256.Trim(), sourceHash, StringComparison.OrdinalIgnoreCase))
            problems.Add("ExpectedSha256 does not match the validated canonical graph content.");

        if (problems.Count > 0)
            return Report(false, false, sourceHash, nodes.Count, edges.Count, 0, 0, warnings, problems);
        if (!request.Commit)
            return Report(true, false, sourceHash, nodes.Count, edges.Count, 0, 0, warnings, problems);

        var created = 0;
        var existing = 0;
        var importNamespace = $"{request.SourceUri.Trim()}\0{sourceHash}";
        foreach (var node in nodes)
        {
            var metadata = CommonMetadata(request, sourceHash);
            metadata["kind"] = "external-graph-node";
            metadata["external.graph.namespace"] = importNamespace;
            metadata["external.graph.nodeId"] = node.Id;
            metadata["external.graph.label"] = node.Label;
            metadata["external.graph.nodeType"] = node.Type;
            if (node.SourceFile is not null) metadata["external.graph.sourceFile"] = node.SourceFile;
            if (node.SourceLocation is not null) metadata["external.graph.sourceLocation"] = node.SourceLocation;

            var content = $"External graph node: {node.Label}\nType: {node.Type}" +
                (node.SourceFile is null ? string.Empty : $"\nSource file: {node.SourceFile}") +
                (node.SourceLocation is null ? string.Empty : $"\nSource location: {node.SourceLocation}");
            var result = await memory.UpsertAsync(new MemoryWriteRequest(content,
                LogicalId: $"external-node:{sourceHash}:{node.Id}",
                EventId: EventId("node", sourceHash, node.Id), Project: request.Project,
                Source: "external-graph-import", Metadata: metadata, SourceUri: request.SourceUri,
                SourceTitle: request.SourceName, Author: request.Format, StatedConfidence: 1), cancellationToken);
            if (result.Created) created++; else existing++;
        }

        foreach (var edge in edges)
        {
            var metadata = CommonMetadata(request, sourceHash);
            metadata["kind"] = "external-graph-edge";
            metadata["external.graph.namespace"] = importNamespace;
            metadata["external.graph.fromId"] = edge.Source.Id;
            metadata["external.graph.fromLabel"] = edge.Source.Label;
            metadata["external.graph.toId"] = edge.Target.Id;
            metadata["external.graph.toLabel"] = edge.Target.Label;
            metadata["external.graph.relation"] = edge.Relation;
            metadata["external.graph.evidenceClass"] = edge.EvidenceClass;
            metadata["external.graph.confidence"] = edge.Confidence.ToString("R", CultureInfo.InvariantCulture);
            if (edge.SourceFile is not null) metadata["external.graph.sourceFile"] = edge.SourceFile;

            var key = $"{edge.Source.Id}\0{edge.Relation}\0{edge.Target.Id}";
            var content = $"External graph relation: {edge.Source.Label} --{edge.Relation}--> {edge.Target.Label}\n" +
                $"Evidence: {edge.EvidenceClass}; confidence: {edge.Confidence.ToString("0.###", CultureInfo.InvariantCulture)}";
            var result = await memory.UpsertAsync(new MemoryWriteRequest(content,
                LogicalId: $"external-edge:{sourceHash}:{StableHash(key)}",
                EventId: EventId("edge", sourceHash, key), Project: request.Project,
                Source: "external-graph-import", Metadata: metadata, SourceUri: request.SourceUri,
                SourceTitle: request.SourceName, Author: request.Format,
                StatedConfidence: edge.Confidence), cancellationToken);
            if (result.Created) created++; else existing++;
        }

        return Report(true, true, sourceHash, nodes.Count, edges.Count, created, existing, warnings, problems);
    }

    private void ParseGraph(JsonElement graph, List<ImportNode> nodes, List<ImportEdge> edges,
        List<string> warnings, List<string> problems)
    {
        if (graph.ValueKind != JsonValueKind.Object)
        {
            problems.Add("Graph must be a JSON object in NetworkX node-link format.");
            return;
        }
        if (!graph.TryGetProperty("nodes", out var nodeValues) || nodeValues.ValueKind != JsonValueKind.Array)
        {
            problems.Add("Graph must contain a nodes array.");
            return;
        }
        var linkName = graph.TryGetProperty("links", out var linkValues) ? "links" :
            graph.TryGetProperty("edges", out linkValues) ? "edges" : string.Empty;
        if (linkName.Length == 0 || linkValues.ValueKind != JsonValueKind.Array)
        {
            problems.Add("Graph must contain a links array (or edges for newer NetworkX exports).");
            return;
        }
        if (nodeValues.GetArrayLength() > options.Value.ExternalGraphImportMaxNodes)
            problems.Add($"Graph exceeds the configured node limit ({options.Value.ExternalGraphImportMaxNodes}).");
        if (linkValues.GetArrayLength() > options.Value.ExternalGraphImportMaxEdges)
            problems.Add($"Graph exceeds the configured edge limit ({options.Value.ExternalGraphImportMaxEdges}).");
        if (problems.Count > 0) return;

        var byId = new Dictionary<string, ImportNode>(StringComparer.Ordinal);
        foreach (var value in nodeValues.EnumerateArray())
        {
            var id = StringProperty(value, "id", 500, problems);
            var label = StringProperty(value, "label", 1_000, problems);
            if (id is null || label is null) continue;
            if (!byId.TryAdd(id, new ImportNode(id, label,
                    OptionalString(value, "file_type", 100, problems) ?? "concept",
                    SafeSourceFile(value, problems), OptionalString(value, "source_location", 100, problems))))
                problems.Add($"Duplicate graph node id '{id}'.");
        }
        nodes.AddRange(byId.Values.OrderBy(node => node.Id, StringComparer.Ordinal));

        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in linkValues.EnumerateArray())
        {
            var sourceId = StringProperty(value, "source", 500, problems);
            var targetId = StringProperty(value, "target", 500, problems);
            var relation = StringProperty(value, "relation", 100, problems);
            var evidence = StringProperty(value, "confidence", 20, problems)?.ToUpperInvariant();
            if (sourceId is null || targetId is null || relation is null || evidence is null) continue;
            if (!byId.TryGetValue(sourceId, out var source) || !byId.TryGetValue(targetId, out var target))
            {
                problems.Add($"Graph edge '{sourceId}' -> '{targetId}' references a missing node.");
                continue;
            }
            if (!RelationRegex().IsMatch(relation))
            {
                problems.Add($"Graph relation '{relation}' contains unsupported characters.");
                continue;
            }
            if (evidence is not ("EXTRACTED" or "INFERRED" or "AMBIGUOUS"))
            {
                problems.Add($"Graph edge '{sourceId}' -> '{targetId}' has invalid confidence class '{evidence}'.");
                continue;
            }
            var confidence = evidence == "EXTRACTED" ? 1d : evidence == "AMBIGUOUS" ? .35d : .75d;
            if (value.TryGetProperty("confidence_score", out var score) && score.ValueKind == JsonValueKind.Number)
            {
                if (!score.TryGetDouble(out confidence) || confidence is < 0 or > 1)
                {
                    problems.Add($"Graph edge '{sourceId}' -> '{targetId}' has confidence_score outside 0..1.");
                    continue;
                }
            }
            var edgeKey = $"{sourceId}\0{relation}\0{targetId}";
            if (!seenEdges.Add(edgeKey))
            {
                warnings.Add($"Duplicate edge ignored: {sourceId} --{relation}--> {targetId}.");
                continue;
            }
            edges.Add(new ImportEdge(source, target, relation, evidence, confidence, SafeSourceFile(value, problems)));
        }

        if (graph.TryGetProperty("graph", out var graphAttributes) && graphAttributes.ValueKind == JsonValueKind.Object &&
            graphAttributes.TryGetProperty("hyperedges", out var hyperedges) && hyperedges.ValueKind == JsonValueKind.Array &&
            hyperedges.GetArrayLength() > 0)
            problems.Add($"Graph contains {hyperedges.GetArrayLength()} hyperedges. Import was refused because silently dropping multi-node evidence is unsafe.");
    }

    private static void ValidateEnvelope(ExternalGraphImportRequest request, List<string> problems)
    {
        if (!string.Equals(request.Format, SupportedFormat, StringComparison.OrdinalIgnoreCase))
            problems.Add($"Unsupported format '{request.Format}'. Supported format: {SupportedFormat}.");
        if (string.IsNullOrWhiteSpace(request.SourceName) || request.SourceName.Trim().Length > 200 ||
            request.SourceName.Any(char.IsControl))
            problems.Add("SourceName is required and must not exceed 200 characters.");
        if (string.IsNullOrWhiteSpace(request.SourceUri) || request.SourceUri.Trim().Length > 2_048 ||
            !Uri.TryCreate(request.SourceUri.Trim(), UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme is not ("file" or "http" or "https") || !string.IsNullOrEmpty(sourceUri.UserInfo))
            problems.Add("SourceUri must be an absolute file, http, or https URI without embedded credentials.");
        if (request.Project?.Length > 200 || request.Project?.Any(char.IsControl) == true)
            problems.Add("Project must not exceed 200 characters or contain control characters.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256) && !Sha256Regex().IsMatch(request.ExpectedSha256.Trim()))
            problems.Add("ExpectedSha256 must contain exactly 64 hexadecimal characters.");
    }

    private static string? StringProperty(JsonElement value, string name, int maxLength, List<string> problems)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            problems.Add($"Every graph item must contain a non-empty string '{name}'.");
            return null;
        }
        var result = property.GetString()!.Trim();
        if (result.Length > maxLength || result.Any(char.IsControl))
        {
            problems.Add($"Graph field '{name}' exceeds {maxLength} characters or contains control characters.");
            return null;
        }
        return result;
    }

    private static string? OptionalString(JsonElement value, string name, int maxLength, List<string> problems)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (property.ValueKind != JsonValueKind.String)
        {
            problems.Add($"Optional graph field '{name}' must be a string.");
            return null;
        }
        var result = property.GetString()?.Trim();
        if (string.IsNullOrEmpty(result)) return null;
        if (result.Length > maxLength || result.Any(char.IsControl))
        {
            problems.Add($"Graph field '{name}' exceeds {maxLength} characters or contains control characters.");
            return null;
        }
        return result;
    }

    private static string? SafeSourceFile(JsonElement value, List<string> problems)
    {
        var sourceFile = OptionalString(value, "source_file", 1_000, problems);
        if (sourceFile is null) return null;
        if (Path.IsPathRooted(sourceFile) || sourceFile.Split('/', '\\').Any(part => part == ".."))
        {
            problems.Add($"source_file must be a relative, non-traversing path: '{sourceFile}'.");
            return null;
        }
        return sourceFile.Replace('\\', '/');
    }

    private static Dictionary<string, string> CommonMetadata(ExternalGraphImportRequest request, string sourceHash) => new()
    {
        ["external.graph.format"] = SupportedFormat,
        ["external.graph.sourceName"] = request.SourceName.Trim(),
        ["external.graph.sourceUri"] = request.SourceUri.Trim(),
        ["external.graph.sourceSha256"] = sourceHash,
        ["external.graph.importPolicy"] = "validated-idempotent-v1",
    };

    private static string CanonicalHash(IEnumerable<ImportNode> nodes, IEnumerable<ImportEdge> edges)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var node in nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
            Append(hash, $"N\0{node.Id}\0{node.Label}\0{node.Type}\0{node.SourceFile}\0{node.SourceLocation}\n");
        foreach (var edge in edges.OrderBy(edge => edge.Source.Id, StringComparer.Ordinal)
                     .ThenBy(edge => edge.Relation, StringComparer.Ordinal).ThenBy(edge => edge.Target.Id, StringComparer.Ordinal))
            Append(hash, $"E\0{edge.Source.Id}\0{edge.Relation}\0{edge.Target.Id}\0{edge.EvidenceClass}\0{edge.Confidence.ToString("R", CultureInfo.InvariantCulture)}\0{edge.SourceFile}\n");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
    private static string EventId(string kind, string sourceHash, string key) => $"external-{kind}-{StableHash(sourceHash + "\0" + key)}";
    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
    private static ExternalGraphImportReport Report(bool valid, bool committed, string hash, int nodes, int edges,
        int created, int existing, IReadOnlyList<string> warnings, IReadOnlyList<string> problems) =>
        new(valid, committed, SupportedFormat, hash, nodes, edges, created, existing, warnings, problems);

    private sealed record ImportNode(string Id, string Label, string Type, string? SourceFile, string? SourceLocation);
    private sealed record ImportEdge(ImportNode Source, ImportNode Target, string Relation, string EvidenceClass,
        double Confidence, string? SourceFile);

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
    [GeneratedRegex("^[\\p{L}\\p{N}_.:/ -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RelationRegex();
}
