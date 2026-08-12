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

    private static WebApplicationFactory<Program> CreateFactory(string storage, int summaryThreshold) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyperMemory:StorageBasePath"] = storage,
                ["HyperMemory:OllamaEndpoint"] = "http://127.0.0.1:1",
                ["HyperMemory:BackgroundSummaryThresholdCharacters"] = summaryThreshold.ToString()
            })));
}
