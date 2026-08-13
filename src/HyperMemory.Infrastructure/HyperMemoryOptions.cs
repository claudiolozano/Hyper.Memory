namespace HyperMemory.Infrastructure;

public sealed class HyperMemoryOptions
{
    public const string SectionName = "HyperMemory";
    public string StorageBasePath { get; set; } = string.Empty;
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string? OllamaModel { get; set; }
    public bool PreferOllamaEmbeddings { get; set; }
    public bool AllowDeterministicEmbeddingFallback { get; set; } = true;
    public bool EnableBackgroundSummaries { get; set; }
    public int BackgroundSummaryThresholdCharacters { get; set; } = 12_000;
    public int RecentSemanticCandidateLimit { get; set; } = 2_000;
    public bool EnableKnowledgeProjection { get; set; } = true;
    public int KnowledgeProjectionBatchSize { get; set; } = 100;
    public int ScaleMaintenanceIntervalMinutes { get; set; } = 360;
    public int ExternalGraphImportMaxNodes { get; set; } = 100_000;
    public int ExternalGraphImportMaxEdges { get; set; } = 250_000;
    public OperationalMemoryFeatureOptions Operational { get; set; } = new();
}

public sealed class OperationalMemoryFeatureOptions
{
    public bool EnableEventJournal { get; set; }
    public bool EnableProjectState { get; set; }
    public bool EnableValidationMemory { get; set; }
    public bool EnableErrorMemory { get; set; }
    public bool EnableDecisionMemory { get; set; }
    public bool EnableTaskGraph { get; set; }
    public bool EnableContracts { get; set; }
    public bool EnableCheckpoints { get; set; }
    public bool EnableSelectiveMemoryRouter { get; set; }
    public bool EnableCapabilityRouting { get; set; }
    public bool EnableToolEventCapture { get; set; }
    public bool EnableWorkingMemory { get; set; }
    public int MaxRepairAttempts { get; set; } = 3;
    public int WorkingMemoryDefaultTtlMinutes { get; set; } = 1_440;
    public int WorkingMemoryMaxItems { get; set; } = 200;

    public bool AnyEnabled => EnableEventJournal || EnableProjectState || EnableValidationMemory ||
        EnableErrorMemory || EnableDecisionMemory || EnableTaskGraph || EnableContracts ||
        EnableCheckpoints || EnableSelectiveMemoryRouter || EnableCapabilityRouting ||
        EnableToolEventCapture || EnableWorkingMemory;
}

public sealed record StorageLayout(string Root, string DatabasePath)
{
    public static StorageLayout Create(string configuredBasePath)
    {
        if (string.IsNullOrWhiteSpace(configuredBasePath))
            throw new InvalidOperationException(
                "Storage is not configured. Set HyperMemory:StorageBasePath, HYPERMEMORY_STORAGE, or --storage-root <path>.");

        var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredBasePath.Trim()));
        var root = string.Equals(Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar)), "Hyper_Memory", StringComparison.OrdinalIgnoreCase)
            ? basePath.TrimEnd(Path.DirectorySeparatorChar)
            : Path.Combine(basePath, "Hyper_Memory");
        root = Path.GetFullPath(root);

        if (!string.Equals(Path.GetFileName(root), "Hyper_Memory", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The resolved storage folder must be named Hyper_Memory.");

        if (File.Exists(root))
            throw new InvalidOperationException($"The storage target is a file, not a directory: {root}");

        Directory.CreateDirectory(root);
        var info = new DirectoryInfo(root);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Hyper_Memory cannot be a symbolic link or junction.");

        return new StorageLayout(root, Path.Combine(root, "hypermemory.sqlite3"));
    }
}
