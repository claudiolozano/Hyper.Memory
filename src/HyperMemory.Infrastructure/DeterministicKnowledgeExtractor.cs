using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperMemory.Core;

namespace HyperMemory.Infrastructure;

internal sealed record KnowledgeProjectionInput(
    string VersionId,
    string LogicalId,
    string Content,
    string? Project,
    string? Source,
    string MetadataJson,
    string? SourceUri,
    string? Author,
    string? ClaimKey,
    string? SupersedesVersionId,
    DateTimeOffset OccurredAt);

internal sealed record KnowledgeMention(
    string MentionId,
    string VersionId,
    string EntityId,
    string Role,
    string EvidenceClass,
    double Confidence,
    int? StartOffset = null,
    int? EndOffset = null);

internal sealed record ExtractedKnowledge(
    IReadOnlyList<KnowledgeEntity> Entities,
    IReadOnlyList<KnowledgeRelation> Relations,
    IReadOnlyList<KnowledgeMention> Mentions);

internal static partial class DeterministicKnowledgeExtractor
{
    public const string Version = "1.2.0";
    private const string TurnSeparator = "\n\nHermes response:\n";

    public static ExtractedKnowledge Extract(KnowledgeProjectionInput input)
    {
        var entities = new Dictionary<string, KnowledgeEntity>(StringComparer.Ordinal);
        var relations = new Dictionary<string, KnowledgeRelation>(StringComparer.Ordinal);
        var mentions = new Dictionary<string, KnowledgeMention>(StringComparer.Ordinal);
        var metadata = ParseMetadata(input.MetadataJson);
        var (request, response, responseOffset) = SplitTurn(input.Content);

        var requestId = AddEntity("request", input.VersionId, Summarize(request), entities);
        AddMention(requestId, "request", "EXTRACTED", 1, 0, request.Length, input, mentions);

        var versionEntity = AddEntity("memory_version", input.VersionId, input.VersionId, entities);
        AddMention(versionEntity, "immutable_memory_version", "VERIFIED", 1, null, null, input, mentions);
        AddRelation(requestId, versionEntity, "STORED_AS_VERSION", "VERIFIED", 1, input.VersionId, relations);

        var occurredDate = input.OccurredAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var occurredDateEntity = AddEntity("date", occurredDate, occurredDate, entities);
        AddMention(occurredDateEntity, "occurred_date", "VERIFIED", 1, null, null, input, mentions);
        AddRelation(versionEntity, occurredDateEntity, "OCCURRED_ON", "VERIFIED", 1, input.VersionId, relations);

        ProjectStructuredTextEntities(request, requestId, 0, input, entities, relations, mentions);

        string? responseId = null;
        if (!string.IsNullOrWhiteSpace(response))
        {
            responseId = AddEntity("response", input.VersionId, Summarize(response), entities);
            AddMention(responseId, "response", "EXTRACTED", 1, responseOffset, responseOffset + response.Length, input, mentions);
            AddRelation(requestId, responseId, "HAS_RESPONSE", "EXTRACTED", 1, input.VersionId, relations);
            AddRelation(responseId, versionEntity, "STORED_AS_VERSION", "VERIFIED", 1, input.VersionId, relations);
            ProjectStructuredTextEntities(response, responseId, responseOffset, input, entities, relations, mentions);
        }

        if (!string.IsNullOrWhiteSpace(input.SupersedesVersionId))
        {
            var previousVersion = AddEntity("memory_version", input.SupersedesVersionId, input.SupersedesVersionId, entities);
            AddMention(previousVersion, "superseded_memory_version", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(versionEntity, previousVersion, "SUPERSEDES", "EXTRACTED", 1, input.VersionId, relations);
            AddRelation(responseId ?? requestId, previousVersion, "CORRECTS_VERSION", "EXTRACTED", 1,
                input.VersionId, relations);
        }

        if (!string.IsNullOrWhiteSpace(input.Project))
        {
            var projectId = AddEntity("project", input.Project, input.Project, entities);
            AddMention(projectId, "project", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(requestId, projectId, "PART_OF_PROJECT", "EXTRACTED", 1, input.VersionId, relations);
            if (responseId is not null)
                AddRelation(responseId, projectId, "PART_OF_PROJECT", "EXTRACTED", 1, input.VersionId, relations);
        }

        if (metadata.TryGetValue("sessionId", out var sessionId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            var sessionEntity = AddEntity("session", sessionId, sessionId, entities);
            AddMention(sessionEntity, "session", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(requestId, sessionEntity, "OCCURRED_IN_SESSION", "EXTRACTED", 1, input.VersionId, relations);
            if (responseId is not null)
                AddRelation(responseId, sessionEntity, "OCCURRED_IN_SESSION", "EXTRACTED", 1, input.VersionId, relations);
        }

        if (!string.IsNullOrWhiteSpace(input.SourceUri))
        {
            var sourceType = Uri.TryCreate(input.SourceUri, UriKind.Absolute, out var uri) && uri.IsFile ? "file" : "source";
            var sourceEntity = AddEntity(sourceType, input.SourceUri, input.SourceUri, entities);
            AddMention(sourceEntity, "source", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(requestId, sourceEntity, "SOURCED_FROM", "EXTRACTED", 1, input.VersionId, relations);
            if (responseId is not null)
                AddRelation(responseId, sourceEntity, "SOURCED_FROM", "EXTRACTED", 1, input.VersionId, relations);
        }

        if (responseId is not null && !string.IsNullOrWhiteSpace(input.Author))
        {
            var authorId = AddEntity("person_or_agent", input.Author, input.Author, entities);
            AddMention(authorId, "author", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(responseId, authorId, "AUTHORED_BY", "EXTRACTED", 1, input.VersionId, relations);
        }

        var kind = metadata.GetValueOrDefault("kind") ?? metadata.GetValueOrDefault("memory.kind") ?? string.Empty;
        if (kind.Equals("decision", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(input.ClaimKey))
        {
            var decisionKey = input.ClaimKey ?? input.LogicalId;
            var decisionId = AddEntity("decision", decisionKey, decisionKey, entities);
            AddMention(decisionId, "decision", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(responseId ?? requestId, decisionId, "ASSERTS_DECISION", "EXTRACTED", 0.95, input.VersionId, relations);
        }

        if (responseId is not null && IsArtifactRequest(request))
        {
            foreach (var title in ExtractArtifactTitles(response))
            {
                var artifactId = AddEntity("artifact", title, title, entities);
                AddMention(artifactId, "artifact_title", "EXTRACTED", 0.95, null, null, input, mentions);
                AddRelation(requestId, artifactId, "REQUESTED_ARTIFACT", "INFERRED", 0.75, input.VersionId, relations);
                AddRelation(responseId, artifactId, "PRODUCED_ARTIFACT", "INFERRED", 0.70, input.VersionId, relations);
            }
        }

        if (responseId is not null && metadata.TryGetValue("artifacts.verifiedFiles", out var verifiedFiles))
        {
            foreach (var file in ParseVerifiedFiles(verifiedFiles))
            {
                var workspace = metadata.GetValueOrDefault("workspace") ?? string.Empty;
                var fileId = AddEntity("file", workspace + "\0" + file.Path, file.Path, entities);
                var hashId = AddEntity("content_hash", file.Sha256, "SHA256:" + file.Sha256, entities);
                AddMention(fileId, "verified_file", "VERIFIED", 1, null, null, input, mentions);
                AddMention(hashId, "verified_hash", "VERIFIED", 1, null, null, input, mentions);
                AddRelation(requestId, fileId, "REQUESTED_FILE", "INFERRED", 0.65, input.VersionId, relations);
                AddRelation(responseId, fileId, "PRODUCED_FILE", "VERIFIED", 1, input.VersionId, relations);
                AddRelation(fileId, hashId, "HAS_CONTENT_HASH", "VERIFIED", 1, input.VersionId, relations);
            }
        }

        if (responseId is not null && metadata.TryGetValue("execution.events", out var executionEvents))
        {
            foreach (var execution in ParseExecutionEvents(executionEvents))
            {
                var executionKey = input.VersionId + "\0" + execution.Command + "\0" + execution.OutputSha256;
                var executionLabel = $"{execution.Status}: {Summarize(execution.Command)}";
                var executionId = AddEntity("execution", executionKey, executionLabel, entities);
                var commandId = AddEntity("command", execution.Command, Summarize(execution.Command), entities);
                AddMention(executionId, "execution_result", "VERIFIED", 1, null, null, input, mentions);
                AddMention(commandId, "executed_command", "EXTRACTED", 1, null, null, input, mentions);
                AddRelation(responseId, executionId,
                    execution.Status == "succeeded" ? "EXECUTION_SUCCEEDED" : "EXECUTION_FAILED",
                    "VERIFIED", 1, input.VersionId, relations);
                AddRelation(executionId, commandId, "RAN_COMMAND", "EXTRACTED", 1, input.VersionId, relations);

                if (execution.Verification is not null)
                {
                    var verification = execution.Verification;
                    var verificationKey = executionKey + "\0" + verification.Kind + "\0" + verification.Scope;
                    var verificationLabel = $"{verification.Status} {verification.Scope} {verification.Kind}: {verification.CanonicalCommand}";
                    var verificationId = AddEntity("verification", verificationKey, verificationLabel, entities);
                    AddMention(verificationId, "verification_result", "VERIFIED", 1, null, null, input, mentions);
                    AddRelation(responseId, verificationId,
                        verification.Status == "passed" ? "PASSED_CHECK" : "FAILED_CHECK",
                        "VERIFIED", 1, input.VersionId, relations);
                    AddRelation(verificationId, commandId, "VERIFIED_BY_COMMAND", "EXTRACTED", 1,
                        input.VersionId, relations);
                }
            }
        }

        if (kind.Equals("external-graph-node", StringComparison.OrdinalIgnoreCase))
            ProjectExternalGraphNode(input, metadata, entities, relations, mentions);
        else if (kind.Equals("external-graph-edge", StringComparison.OrdinalIgnoreCase))
            ProjectExternalGraphEdge(input, metadata, entities, relations, mentions);

        return new ExtractedKnowledge(entities.Values.ToArray(), relations.Values.ToArray(), mentions.Values.ToArray());
    }

    private static void ProjectExternalGraphNode(KnowledgeProjectionInput input, IReadOnlyDictionary<string, string> metadata,
        IDictionary<string, KnowledgeEntity> entities, IDictionary<string, KnowledgeRelation> relations,
        IDictionary<string, KnowledgeMention> mentions)
    {
        if (!ExternalGraphField(metadata, "external.graph.namespace", out var graphNamespace) ||
            !ExternalGraphField(metadata, "external.graph.nodeId", out var nodeId) ||
            !ExternalGraphField(metadata, "external.graph.label", out var label)) return;

        var graphNode = AddEntity("graph_node", graphNamespace + "\0" + nodeId, label, entities);
        AddMention(graphNode, "external_graph_node", "EXTRACTED", 1, null, null, input, mentions);
        if (ExternalGraphField(metadata, "external.graph.sourceFile", out var sourceFile))
        {
            var file = AddEntity("file", graphNamespace + "\0" + sourceFile, sourceFile, entities);
            AddMention(file, "external_graph_source_file", "EXTRACTED", 1, null, null, input, mentions);
            AddRelation(graphNode, file, "DEFINED_IN_FILE", "EXTRACTED", 1, input.VersionId, relations);
        }
        if (!string.IsNullOrWhiteSpace(input.SourceUri))
        {
            var source = AddEntity("source", input.SourceUri, input.SourceUri, entities);
            AddRelation(graphNode, source, "IMPORTED_FROM", "EXTRACTED", 1, input.VersionId, relations);
        }
    }

    private static void ProjectExternalGraphEdge(KnowledgeProjectionInput input, IReadOnlyDictionary<string, string> metadata,
        IDictionary<string, KnowledgeEntity> entities, IDictionary<string, KnowledgeRelation> relations,
        IDictionary<string, KnowledgeMention> mentions)
    {
        if (!ExternalGraphField(metadata, "external.graph.namespace", out var graphNamespace) ||
            !ExternalGraphField(metadata, "external.graph.fromId", out var fromId) ||
            !ExternalGraphField(metadata, "external.graph.fromLabel", out var fromLabel) ||
            !ExternalGraphField(metadata, "external.graph.toId", out var toId) ||
            !ExternalGraphField(metadata, "external.graph.toLabel", out var toLabel) ||
            !ExternalGraphField(metadata, "external.graph.relation", out var relation)) return;

        var evidence = metadata.GetValueOrDefault("external.graph.evidenceClass")?.ToUpperInvariant();
        if (evidence is not ("EXTRACTED" or "INFERRED" or "AMBIGUOUS")) return;
        var confidence = double.TryParse(metadata.GetValueOrDefault("external.graph.confidence"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 1 ? parsed : 0;
        var from = AddEntity("graph_node", graphNamespace + "\0" + fromId, fromLabel, entities);
        var to = AddEntity("graph_node", graphNamespace + "\0" + toId, toLabel, entities);
        AddMention(from, "external_graph_edge_source", evidence, confidence, null, null, input, mentions);
        AddMention(to, "external_graph_edge_target", evidence, confidence, null, null, input, mentions);
        AddRelation(from, to, NormalizeRelation(relation), evidence, confidence, input.VersionId, relations);
    }

    private static bool ExternalGraphField(IReadOnlyDictionary<string, string> metadata, string key, out string value)
    {
        if (metadata.TryGetValue(key, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
        {
            value = candidate;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static string NormalizeRelation(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"[^\p{L}\p{N}]+", "_").Trim('_').ToUpperInvariant();
        return normalized.Length == 0 ? "RELATED_TO" : normalized;
    }

    private static Dictionary<string, string> ParseMetadata(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IEnumerable<VerifiedFile> ParseVerifiedFiles(string json)
    {
        try
        {
            var files = JsonSerializer.Deserialize<List<VerifiedFile>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
            return files.Where(file => !string.IsNullOrWhiteSpace(file.Path) &&
                Regex.IsMatch(file.Sha256 ?? string.Empty, "^[A-Fa-f0-9]{64}$"));
        }
        catch (JsonException) { return []; }
    }

    private static IEnumerable<ExecutionEvent> ParseExecutionEvents(string json)
    {
        try
        {
            var events = JsonSerializer.Deserialize<List<ExecutionEvent>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
            return events.Where(item => !string.IsNullOrWhiteSpace(item.Command) &&
                item.Status is "succeeded" or "failed" &&
                Regex.IsMatch(item.OutputSha256 ?? string.Empty, "^[A-Fa-f0-9]{64}$"));
        }
        catch (JsonException) { return []; }
    }

    private static (string Request, string Response, int ResponseOffset) SplitTurn(string content)
    {
        var separator = content.IndexOf(TurnSeparator, StringComparison.Ordinal);
        if (separator < 0) return (content.Trim(), string.Empty, content.Length);
        var requestStart = content.StartsWith("User request:\n", StringComparison.Ordinal) ? "User request:\n".Length : 0;
        var request = content[requestStart..separator].Trim();
        var responseOffset = separator + TurnSeparator.Length;
        return (request, content[responseOffset..].Trim(), responseOffset);
    }

    private static string Summarize(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? value.Trim();
        return line.Length <= 180 ? line : line[..177] + "...";
    }

    private static bool IsArtifactRequest(string request) => ArtifactRequestRegex().IsMatch(request);

    private static IEnumerable<string> ExtractArtifactTitles(string response)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ArtifactTitleRegex().Matches(response))
        {
            var title = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim().Trim('"', '“', '”').TrimEnd('.', ':');
            if (title.Length is < 3 or > 120 || title.Contains('?')) continue;
            var wordCount = WordRegex().Matches(title).Count;
            if (wordCount is < 2 or > 14) continue;
            if (seen.Add(title)) yield return title;
        }
    }

    private static void ProjectStructuredTextEntities(string text, string ownerEntity, int baseOffset,
        KnowledgeProjectionInput input, IDictionary<string, KnowledgeEntity> entities,
        IDictionary<string, KnowledgeRelation> relations, IDictionary<string, KnowledgeMention> mentions)
    {
        foreach (Match match in LabelledPersonRegex().Matches(text))
        {
            var name = match.Groups["name"].Value.Trim();
            if (name.Length is < 2 or > 80) continue;
            var person = AddEntity("person", name, name, entities);
            AddMention(person, "labelled_person", "EXTRACTED", 1,
                baseOffset + match.Groups["name"].Index,
                baseOffset + match.Groups["name"].Index + match.Groups["name"].Length, input, mentions);
            AddRelation(ownerEntity, person, "MENTIONS_PERSON", "EXTRACTED", 1, input.VersionId, relations);
        }

        foreach (Match match in DateRegex().Matches(text))
        {
            if (!TryNormalizeDate(match.Value, out var canonical)) continue;
            var date = AddEntity("date", canonical, canonical, entities);
            AddMention(date, "explicit_date", "EXTRACTED", 1, baseOffset + match.Index,
                baseOffset + match.Index + match.Length, input, mentions);
            AddRelation(ownerEntity, date, "MENTIONS_DATE", "EXTRACTED", 1, input.VersionId, relations);
        }
    }

    private static bool TryNormalizeDate(string value, out string canonical)
    {
        var formats = new[] { "yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy" };
        if (DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed))
        {
            canonical = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        canonical = string.Empty;
        return false;
    }

    private static string AddEntity(string type, string stableKey, string label, IDictionary<string, KnowledgeEntity> entities)
    {
        var id = $"{type}:{Hash(type + "\0" + Normalize(stableKey))}";
        entities.TryAdd(id, new KnowledgeEntity(id, type, label.Trim()));
        return id;
    }

    private static void AddMention(string entityId, string role, string evidenceClass, double confidence, int? start, int? end,
        KnowledgeProjectionInput input, IDictionary<string, KnowledgeMention> mentions)
    {
        var id = "mention:" + Hash(input.VersionId + "\0" + entityId + "\0" + role);
        mentions.TryAdd(id, new KnowledgeMention(id, input.VersionId, entityId, role, evidenceClass, confidence, start, end));
    }

    private static void AddRelation(string from, string to, string type, string evidenceClass, double confidence, string versionId,
        IDictionary<string, KnowledgeRelation> relations)
    {
        var id = "relation:" + Hash(versionId + "\0" + from + "\0" + to + "\0" + type);
        relations.TryAdd(id, new KnowledgeRelation(id, from, to, type, evidenceClass, confidence, versionId));
    }

    private static string Normalize(string value) => string.Join(' ', WordRegex().Matches(value.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        .Select(match => match.Value));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];

    [GeneratedRegex(@"\b(cuento|relato|historia|juego|codigo|código|documento|informe|plan|programa|aplicacion|aplicación)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactRequestRegex();

    [GeneratedRegex(@"(?m)^\s*(?:#{1,6}\s+([^#*\r\n]{3,120})|\*\*([^*\r\n]{3,120})\*\*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactTitleRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?m)\b(?i:persona|person|autor(?:a)?|author|responsable|owner)\s*:\s*(?<name>[\p{Lu}][\p{L}\p{M}'’-]+(?:\s+[\p{Lu}][\p{L}\p{M}'’-]+){0,3})", RegexOptions.CultureInvariant)]
    private static partial Regex LabelledPersonRegex();

    [GeneratedRegex(@"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}[/-]\d{1,2}[/-]\d{4})\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    private sealed record VerifiedFile(string Path, string Sha256, long Size, string Tool, string Verification);
    private sealed record VerificationEvent(string Status, string Kind, string Scope, string CanonicalCommand);
    private sealed record ExecutionEvent(string Command, int ExitCode, string Status, string Workdir,
        string OutputSha256, int PrivacyRedactions, VerificationEvent? Verification);
}
