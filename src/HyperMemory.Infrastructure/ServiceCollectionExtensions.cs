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
        services.AddSingleton<IKnowledgeProjectionStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        services.AddSingleton<IScaleMaintenanceStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        services.AddSingleton<IOperationalDiagnosticsStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        // These registrations are inert until a feature-gated endpoint resolves them. SQLite
        // migrations and all operational behavior remain controlled by HyperMemoryOptions.
        services.AddSingleton<IOperationalEventStore>(sp => sp.GetRequiredService<SqliteMemoryStore>());
        services.AddSingleton<IProjectStateProjectionStore, SqliteProjectStateProjectionStore>();
        services.AddSingleton<IValidationMemoryService, ValidationMemoryService>();
        services.AddSingleton<IErrorDecisionMemoryService>(sp => new ErrorDecisionMemoryService(
            sp.GetRequiredService<IOperationalEventStore>(),
            sp.GetRequiredService<IProjectStateProjectionStore>(),
            sp.GetRequiredService<IOptions<HyperMemoryOptions>>().Value.Operational.MaxRepairAttempts));
        services.AddSingleton<IContractInvalidationService, ContractInvalidationService>();
        services.AddSingleton<ICheckpointService, CheckpointService>();
        services.AddSingleton<ICompletionEvaluator, CompletionEvaluator>();
        services.AddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        services.AddSingleton<ICapabilityRouter, CapabilityRouter>();
        services.AddSingleton<IOperationalMemoryRouter, OperationalMemoryRouter>();
        services.AddSingleton<IWorkingProjectMemoryService>(sp => new WorkingProjectMemoryService(
            sp.GetRequiredService<IOperationalEventStore>(),
            sp.GetRequiredService<IProjectStateProjectionStore>(),
            sp.GetServices<IValidationMemoryService>(),
            sp.GetRequiredService<IOptions<HyperMemoryOptions>>().Value.Operational.WorkingMemoryDefaultTtlMinutes,
            sp.GetRequiredService<IOptions<HyperMemoryOptions>>().Value.Operational.WorkingMemoryMaxItems));
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
        services.AddSingleton<IExternalGraphImportService, ExternalGraphImportService>();
        return services;
    }
}
