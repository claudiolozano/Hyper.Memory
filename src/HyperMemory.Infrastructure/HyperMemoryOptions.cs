namespace HyperMemory.Infrastructure;

public sealed class HyperMemoryOptions
{
    public const string SectionName = "HyperMemory";
    public string StorageBasePath { get; set; } = string.Empty;
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string? OllamaModel { get; set; }
    public bool AllowDeterministicEmbeddingFallback { get; set; } = true;
    public int BackgroundSummaryThresholdCharacters { get; set; } = 12_000;
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
