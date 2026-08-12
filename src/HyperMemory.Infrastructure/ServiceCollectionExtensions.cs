using HyperMemory.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyperMemory.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHyperMemory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HyperMemoryOptions>().Bind(configuration.GetSection(HyperMemoryOptions.SectionName));
        services.AddSingleton(sp => StorageLayout.Create(sp.GetRequiredService<IOptions<HyperMemoryOptions>>().Value.StorageBasePath));
        services.AddSingleton<SqliteMemoryStore>();
        services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        services.AddHttpClient("ollama", (sp, client) =>
        {
            var endpoint = sp.GetRequiredService<IOptions<HyperMemoryOptions>>().Value.OllamaEndpoint;
            client.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton(sp => new OllamaModelResolver(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), sp.GetRequiredService<IOptions<HyperMemoryOptions>>()));
        services.AddSingleton<IEmbeddingGenerator>(sp => new AdaptiveEmbeddingGenerator(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"),
            sp.GetRequiredService<OllamaModelResolver>(), sp.GetRequiredService<IOptions<HyperMemoryOptions>>()));
        services.AddSingleton<ITextSummarizer>(sp => new AdaptiveTextSummarizer(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama"), sp.GetRequiredService<OllamaModelResolver>()));
        services.AddSingleton<IMemoryService, MemoryService>();
        return services;
    }
}
