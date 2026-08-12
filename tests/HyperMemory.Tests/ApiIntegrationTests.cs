using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using HyperMemory.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace HyperMemory.Tests;

public sealed class ApiIntegrationTests
{
    [Fact]
    public async Task Legacy_json_contract_remains_accepted()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryContractTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000);
        using var client = factory.CreateClient();
        const string legacyJson = """{"content":"Old client payload","logicalId":"legacy","eventId":"legacy-event","project":"compat","source":"hermes","metadata":{"kind":"decision"},"occurredAt":"2025-01-02T03:04:05Z"}""";
        using var response = await client.PostAsync("/memory/upsert", new StringContent(legacyJson, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("legacy-event", document.RootElement.GetProperty("versionId").GetString());
        Assert.True(document.RootElement.GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task Http_contract_appends_queries_and_reports_integrity()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryApiTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 20);
        using var client = factory.CreateClient();

        using var writeResponse = await client.PostAsJsonAsync("/memory/upsert", new MemoryWriteRequest(
            "Hermes selected append-only SQLite memory with FTS5.", "architecture", "api-event-1", "e2e", "test"));
        writeResponse.EnsureSuccessStatusCode();
        var write = await writeResponse.Content.ReadFromJsonAsync<MemoryWriteResult>();
        Assert.NotNull(write);
        Assert.True(write.Created);

        using var queryResponse = await client.PostAsJsonAsync("/memory/query", new MemoryQuery("SQLite FTS5 architecture", 5, "e2e"));
        queryResponse.EnsureSuccessStatusCode();
        var hits = await queryResponse.Content.ReadFromJsonAsync<MemoryHit[]>();
        Assert.NotNull(hits);
        Assert.Contains(hits, hit => hit.Atom.VersionId == "api-event-1");

        var scale = await client.GetFromJsonAsync<MemoryScaleStatus>("/memory/scale");
        Assert.NotNull(scale);
        Assert.True(scale.FullTextCoversAllHistory);
        Assert.True(scale.DatabaseBytes > 0);

        var diagnostics = await client.GetFromJsonAsync<OperationalDiagnostics>("/memory/diagnostics");
        Assert.NotNull(diagnostics);
        Assert.True(diagnostics.AtomCount >= 1);
        Assert.Equal(diagnostics.AtomCount, diagnostics.VectorCount);
        Assert.Empty(diagnostics.Problems);

        IntegrityReport? integrity = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            integrity = await client.GetFromJsonAsync<IntegrityReport>("/memory/integrity");
            if (integrity?.AtomCount >= 2) break;
            await Task.Delay(50);
        }
        Assert.NotNull(integrity);
        Assert.True(integrity.IsValid);
        Assert.Equal(2, integrity.AtomCount);
    }

    [Fact]
    public async Task Local_auth_protects_memory_routes_but_keeps_liveness_available()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryAuthTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000, "local-secret-token");
        using var client = factory.CreateClient();

        Assert.True((await client.GetAsync("/live")).IsSuccessStatusCode);
        using var rejected = await client.PostAsJsonAsync("/memory/query", new MemoryQuery("private memory"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);

        client.DefaultRequestHeaders.Add("X-HyperMemory-Token", "local-secret-token");
        using var accepted = await client.PostAsJsonAsync("/memory/upsert", new MemoryWriteRequest("Protected memory"));
        accepted.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Knowledge_projection_is_built_in_background_and_exposed_as_evidence()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryKnowledgeApiTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000);
        using var client = factory.CreateClient();
        using var written = await client.PostAsJsonAsync("/memory/upsert", new MemoryWriteRequest(
            "User request:\nredacta un informe\n\nHermes response:\n# Informe de continuidad\n\nContenido del informe.",
            EventId: "knowledge-api-turn", Project: "hermes",
            Metadata: new Dictionary<string, string> { ["kind"] = "conversation-turn", ["sessionId"] = "api-session" }));
        written.EnsureSuccessStatusCode();

        KnowledgeProjectionSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var response = await client.GetAsync("/memory/knowledge/knowledge-api-turn");
            if (response.IsSuccessStatusCode)
            {
                snapshot = await response.Content.ReadFromJsonAsync<KnowledgeProjectionSnapshot>();
                break;
            }
            await Task.Delay(50);
        }
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.Entities, entity => entity.EntityType == "artifact" && entity.Label == "Informe de continuidad");
        Assert.Contains(snapshot.Relations, relation => relation.RelationType == "HAS_RESPONSE" && relation.EvidenceClass == "EXTRACTED");
    }

    [Fact]
    public async Task Core_memory_remains_healthy_when_knowledge_projection_is_disabled()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryNoKnowledgeTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000, enableKnowledgeProjection: false);
        using var client = factory.CreateClient();
        using var written = await client.PostAsJsonAsync("/memory/upsert", new MemoryWriteRequest(
            "User request:\nrecuerda la clave modo-sin-grafo\n\nHermes response:\nQueda registrada.",
            EventId: "no-knowledge-turn", Project: "hermes", Source: "hermes-auto"));
        written.EnsureSuccessStatusCode();

        await Task.Delay(1_200);
        using var queried = await client.PostAsJsonAsync("/memory/query", new MemoryQuery("modo-sin-grafo", 5, "hermes"));
        queried.EnsureSuccessStatusCode();
        var hits = await queried.Content.ReadFromJsonAsync<MemoryHit[]>();
        Assert.Contains(hits!, hit => hit.Atom.VersionId == "no-knowledge-turn");
        Assert.True((await client.GetFromJsonAsync<IntegrityReport>("/memory/integrity"))!.IsValid);
        Assert.Equal(1, (await client.GetFromJsonAsync<MemoryScaleStatus>("/memory/scale"))!.KnowledgePendingCount);
    }

    [Fact]
    public async Task External_graph_import_is_previewed_validated_idempotent_and_projected()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryGraphImportApiTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000);
        using var client = factory.CreateClient();
        var graph = JsonSerializer.Deserialize<JsonElement>("""
            {
              "directed": true,
              "multigraph": false,
              "graph": {},
              "nodes": [
                { "id": "service", "label": "AlphaService", "file_type": "code", "source_file": "src/alpha.cs", "source_location": "L10" },
                { "id": "repository", "label": "BetaRepository", "file_type": "code", "source_file": "src/beta.cs", "source_location": "L20" }
              ],
              "links": [
                { "source": "service", "target": "repository", "relation": "calls", "confidence": "EXTRACTED", "source_file": "src/alpha.cs" }
              ]
            }
            """);
        var previewRequest = new ExternalGraphImportRequest(graph, "Graphify fixture",
            "file:///workspace/graphify-out/graph.json", "import-test");
        using var previewResponse = await client.PostAsJsonAsync("/memory/import/graph", previewRequest);
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<ExternalGraphImportReport>();
        Assert.NotNull(preview);
        Assert.True(preview.Valid);
        Assert.False(preview.Committed);
        Assert.Equal(2, preview.NodeCount);
        Assert.Equal(1, preview.EdgeCount);
        Assert.Matches("^[a-f0-9]{64}$", preview.SourceSha256);
        Assert.Equal(0, (await client.GetFromJsonAsync<OperationalDiagnostics>("/memory/diagnostics"))!.AtomCount);

        var commitRequest = previewRequest with { Commit = true, ExpectedSha256 = preview.SourceSha256 };
        using var commitResponse = await client.PostAsJsonAsync("/memory/import/graph", commitRequest);
        commitResponse.EnsureSuccessStatusCode();
        var committed = await commitResponse.Content.ReadFromJsonAsync<ExternalGraphImportReport>();
        Assert.NotNull(committed);
        Assert.Equal(3, committed.CreatedCount);
        Assert.Equal(0, committed.ExistingCount);

        using var repeatedResponse = await client.PostAsJsonAsync("/memory/import/graph", commitRequest);
        repeatedResponse.EnsureSuccessStatusCode();
        var repeated = await repeatedResponse.Content.ReadFromJsonAsync<ExternalGraphImportReport>();
        Assert.NotNull(repeated);
        Assert.Equal(0, repeated.CreatedCount);
        Assert.Equal(3, repeated.ExistingCount);

        MemoryHit[]? hits = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var query = await client.PostAsJsonAsync("/memory/query", new MemoryQuery("AlphaService calls BetaRepository", 5, "import-test"));
            query.EnsureSuccessStatusCode();
            hits = await query.Content.ReadFromJsonAsync<MemoryHit[]>();
            if (hits?.Any(hit => hit.Knowledge?.Reasons.Any(reason => reason.Contains("AlphaService", StringComparison.Ordinal)) == true) == true) break;
            await Task.Delay(50);
        }
        Assert.NotNull(hits);
        Assert.Contains(hits, hit => hit.Atom.Content.Contains("AlphaService", StringComparison.Ordinal));
        Assert.True((await client.GetFromJsonAsync<IntegrityReport>("/memory/integrity"))!.IsValid);
    }

    [Fact]
    public async Task External_graph_import_rejects_dangling_edges_and_absolute_source_paths()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryInvalidGraphImportApiTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000);
        using var client = factory.CreateClient();
        var graph = JsonSerializer.Deserialize<JsonElement>("""
            {
              "nodes": [{ "id": "one", "label": "One", "source_file": "C:/private/one.cs" }],
              "links": [{ "source": "one", "target": "missing", "relation": "calls", "confidence": "INFERRED" }]
            }
            """);
        using var response = await client.PostAsJsonAsync("/memory/import/graph", new ExternalGraphImportRequest(
            graph, "Invalid fixture", "file:///workspace/graph.json", Commit: true));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<ExternalGraphImportReport>();
        Assert.NotNull(report);
        Assert.False(report.Valid);
        Assert.Contains(report.Problems, problem => problem.Contains("relative", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Problems, problem => problem.Contains("missing node", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, (await client.GetFromJsonAsync<OperationalDiagnostics>("/memory/diagnostics"))!.AtomCount);
    }

    [Fact]
    public async Task Direct_api_writes_are_centrally_redacted_before_immutable_storage()
    {
        var storage = Path.Combine(Path.GetTempPath(), "HyperMemoryPrivacyApiTests", Guid.NewGuid().ToString("N"));
        await using var factory = CreateFactory(storage, 12000);
        using var client = factory.CreateClient();
        using var write = await client.PostAsJsonAsync("/memory/upsert", new MemoryWriteRequest(
            "Authorization: Bearer direct-secret-value and card 4111 1111 1111 1111",
            EventId: "privacy-direct", Project: "privacy",
            Metadata: new Dictionary<string, string> { ["note"] = "api_key=metadata-secret" }));
        write.EnsureSuccessStatusCode();

        using var query = await client.PostAsJsonAsync("/memory/query", new MemoryQuery("Authorization card", 5, "privacy"));
        query.EnsureSuccessStatusCode();
        var hit = Assert.Single((await query.Content.ReadFromJsonAsync<MemoryHit[]>())!, item => item.Atom.VersionId == "privacy-direct");
        Assert.DoesNotContain("direct-secret-value", hit.Atom.Content);
        Assert.DoesNotContain("4111 1111", hit.Atom.Content);
        Assert.DoesNotContain("metadata-secret", hit.Atom.Metadata["note"]);
        Assert.Equal("restricted-redacted", hit.Atom.Metadata["privacy.classification"]);
        Assert.Equal("api-central-redaction-v1", hit.Atom.Metadata["privacy.enforcement"]);
        Assert.Equal("3", hit.Atom.Metadata["privacy.redactions"]);
        Assert.True((await client.GetFromJsonAsync<IntegrityReport>("/memory/integrity"))!.IsValid);
    }

    private static WebApplicationFactory<Program> CreateFactory(string storage, int summaryThreshold, string? authToken = null,
        bool enableKnowledgeProjection = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperMemory:StorageBasePath"] = storage,
                ["HyperMemory:OllamaEndpoint"] = "http://127.0.0.1:1",
                ["HyperMemory:EnableBackgroundSummaries"] = "true",
                ["HyperMemory:BackgroundSummaryThresholdCharacters"] = summaryThreshold.ToString(),
                ["HyperMemory:AuthToken"] = authToken,
                ["HyperMemory:EnableKnowledgeProjection"] = enableKnowledgeProjection.ToString()
            })));
}
