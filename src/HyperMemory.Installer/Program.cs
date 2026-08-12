using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace HyperMemory.Installer;

internal static class Program
{
    private const string ProductVersion = "1.1.0";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HyperMemory";
    private const string RegistryValueName = "HyperMemory";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Launch) return LaunchAsync(Required(options.Manifest, "--manifest")).GetAwaiter().GetResult();
            if (options.Uninstall) return Uninstall(options);
            if (options.Silent)
            {
                Install(Required(options.StorageRoot, "--storage-root"), options.HermesRoot, startImmediately: false);
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new InstallerForm());
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            Log(error.ToString());
            if (!args.Any(x => x.Equals("--silent", StringComparison.OrdinalIgnoreCase) || x.Equals("/S", StringComparison.OrdinalIgnoreCase)))
                MessageBox.Show(error.Message, "HyperMemory", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static InstallationManifest Install(string selectedPath, string? hermesRoot = null, bool startImmediately = true)
    {
        var root = ResolveStorageRoot(selectedPath);
        var installId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var release = Path.Combine(root, "app", "releases", $"{ProductVersion}-{installId}");
        var installationDirectory = Path.Combine(root, "app", "installations");
        var manifestPath = Path.Combine(installationDirectory, installId + ".json");
        var hermesBase = Path.GetFullPath(hermesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes"));
        var skillPath = Path.GetFullPath(Path.Combine(hermesBase, "skills", "hyper-memory"));

        ValidateNewSkillTarget(skillPath);
        Directory.CreateDirectory(release);
        ExtractPayload(release);
        var apiExe = RequiredFile(Path.Combine(release, "api", "HyperMemory.Api.exe"));
        var bridgeDirectory = Path.Combine(release, "bridge");
        RequiredFile(Path.Combine(bridgeDirectory, "HyperMemory.Bridge.exe"));
        var skillSource = RequiredFile(Path.Combine(release, "skill", "SKILL.md"));

        var installedSetup = Path.Combine(release, "HyperMemorySetup.exe");
        File.Copy(Required(Environment.ProcessPath, "installer executable"), installedSetup, overwrite: false);

        Directory.CreateDirectory(Path.Combine(skillPath, "bin"));
        File.Copy(skillSource, Path.Combine(skillPath, "SKILL.md"), overwrite: false);
        foreach (var file in Directory.EnumerateFiles(bridgeDirectory))
            File.Copy(file, Path.Combine(skillPath, "bin", Path.GetFileName(file)), overwrite: false);
        var markerPath = Path.Combine(skillPath, ".hypermemory-owned.json");
        WriteNewJson(markerPath, new OwnershipMarker(installId, root, ProductVersion));

        Directory.CreateDirectory(installationDirectory);
        var manifest = new InstallationManifest(installId, ProductVersion, root, release, apiExe,
            installedSetup, hermesBase, skillPath, markerPath, DateTimeOffset.UtcNow);
        WriteNewJson(manifestPath, manifest);

        var launchCommand = $"\"{installedSetup}\" --launch --manifest \"{manifestPath}\"";
        using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
            runKey.SetValue(RegistryValueName, launchCommand, RegistryValueKind.String);
        RegisterUninstaller(manifest, manifestPath);

        if (startImmediately)
        {
            var launcher = Process.Start(new ProcessStartInfo(installedSetup,
                $"--launch --manifest \"{manifestPath}\"") { UseShellExecute = false, CreateNoWindow = true });
            launcher?.WaitForExit(20_000);
            if (!WaitForHealthAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult())
                throw new InvalidOperationException("La instalación terminó, pero el servicio local no pudo iniciarse. Reinicia Windows para completar el arranque.");
        }

        Log($"Installed {installId} at {root}");
        return manifest;
    }

    private static async Task<int> LaunchAsync(string manifestPath)
    {
        var manifest = ReadManifest(manifestPath);
        if (await IsHealthyAsync()) return 0;
        var process = Process.Start(new ProcessStartInfo(manifest.ApiExecutable,
            $"--storage-root \"{manifest.StorageRoot}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(manifest.ApiExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null) return 2;
        return await WaitForHealthAsync(TimeSpan.FromSeconds(20)) ? 0 : 3;
    }

    private static int Uninstall(Options options)
    {
        var manifestPath = Required(options.Manifest, "--manifest");
        var manifest = ReadManifest(manifestPath);
        if (!options.Silent)
        {
            ApplicationConfiguration.Initialize();
            var answer = MessageBox.Show(
                "Se retirará HyperMemory de Hermes y del inicio automático. La memoria histórica se conservará como respaldo y no afectará al agente. ¿Continuar?",
                "Desinstalar HyperMemory", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return 0;
        }

        StopOwnedApi(manifest.ApiExecutable);
        RemoveOwnedStartup(manifest, manifestPath);
        RemoveOwnedSkill(manifest);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        var receipt = Path.Combine(manifest.StorageRoot, $"UNINSTALLED-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.txt");
        using (var writer = new StreamWriter(new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
        {
            writer.WriteLine("HyperMemory was detached from Hermes and Windows startup.");
            writer.WriteLine("Historical memory and installed binaries were preserved by the zero-deletion policy.");
            writer.WriteLine($"Installation: {manifest.InstallId}");
        }
        Log($"Uninstalled integration {manifest.InstallId}; data preserved at {manifest.StorageRoot}");
        if (!options.Silent)
            MessageBox.Show("HyperMemory se retiró correctamente de Hermes. La memoria histórica quedó conservada como respaldo.",
                "HyperMemory", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static void RemoveOwnedStartup(InstallationManifest manifest, string manifestPath)
    {
        var expected = $"\"{manifest.InstallerExecutable}\" --launch --manifest \"{manifestPath}\"";
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        var actual = runKey?.GetValue(RegistryValueName) as string;
        if (string.Equals(actual, expected, StringComparison.Ordinal)) runKey?.DeleteValue(RegistryValueName, false);
    }

    private static void RemoveOwnedSkill(InstallationManifest manifest)
    {
        var expected = Path.GetFullPath(Path.Combine(manifest.HermesRoot, "skills", "hyper-memory"));
        var actual = Path.GetFullPath(manifest.SkillPath);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La ruta del Skill no coincide con la ubicación segura esperada. No se eliminó nada de Hermes.");
        if (!Directory.Exists(actual)) return;
        var rootInfo = new DirectoryInfo(actual);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("El Skill es un enlace o unión. No se eliminó nada.");
        var marker = JsonSerializer.Deserialize<OwnershipMarker>(File.ReadAllText(manifest.MarkerPath));
        if (marker?.InstallId != manifest.InstallId)
            throw new InvalidOperationException("No se pudo verificar que el Skill pertenezca a esta instalación.");
        foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Se detectó un enlace dentro del Skill: {entry.FullName}. No se eliminó nada.");
        Directory.Delete(actual, recursive: true);
    }

    private static void StopOwnedApi(string expectedExecutable)
    {
        var expected = Path.GetFullPath(expectedExecutable);
        foreach (var process in Process.GetProcessesByName("HyperMemory.Api"))
        {
            using (process)
            {
                string? actual;
                try { actual = process.MainModule?.FileName; }
                catch { continue; }
                if (!string.Equals(actual is null ? null : Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(10_000))
                    throw new InvalidOperationException("No se pudo detener el proceso de HyperMemory. Cierra la sesión de Windows e inténtalo nuevamente.");
            }
        }
    }

    private static void RegisterUninstaller(InstallationManifest manifest, string manifestPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true);
        key.SetValue("DisplayName", "HyperMemory para Hermes", RegistryValueKind.String);
        key.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
        key.SetValue("Publisher", "HyperMemory", RegistryValueKind.String);
        key.SetValue("InstallLocation", manifest.StorageRoot, RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{manifest.InstallerExecutable}\" --uninstall --manifest \"{manifestPath}\"", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{manifest.InstallerExecutable}\" --uninstall --silent --manifest \"{manifestPath}\"", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void ExtractPayload(string release)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HyperMemory.Payload.zip")
            ?? throw new InvalidOperationException("El instalador no contiene su carga útil.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var boundary = Path.GetFullPath(release).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(release, entry.FullName));
            if (!destination.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La carga del instalador contiene una ruta no segura.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(true);
        }
    }

    private static string ResolveStorageRoot(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) throw new ArgumentException("Selecciona una ubicación de almacenamiento.");
        var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(selectedPath.Trim()));
        var root = string.Equals(Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar)), "Hyper_Memory", StringComparison.OrdinalIgnoreCase)
            ? basePath.TrimEnd(Path.DirectorySeparatorChar) : Path.Combine(basePath, "Hyper_Memory");
        root = Path.GetFullPath(root);
        if (!string.Equals(Path.GetFileName(root), "Hyper_Memory", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La carpeta final debe llamarse Hyper_Memory.");
        if (File.Exists(root)) throw new InvalidOperationException("La ubicación elegida es un archivo.");
        Directory.CreateDirectory(root);
        if ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Hyper_Memory no puede ser un enlace o unión.");
        return root;
    }

    private static void ValidateNewSkillTarget(string skillPath)
    {
        if (Directory.Exists(skillPath) || File.Exists(skillPath))
            throw new InvalidOperationException("Ya existe un Skill llamado hyper-memory. Para proteger Hermes no será sobrescrito; desinstala primero la instalación anterior.");
        var skillsRoot = Path.GetDirectoryName(skillPath)!;
        Directory.CreateDirectory(skillsRoot);
        if ((new DirectoryInfo(skillsRoot).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La carpeta de Skills de Hermes es un enlace o unión y no puede modificarse de forma segura.");
    }

    private static InstallationManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(Path.GetFullPath(path)))
        ?? throw new InvalidOperationException("El manifiesto de instalación no es válido.");

    private static async Task<bool> WaitForHealthAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (await IsHealthyAsync()) return true;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync("http://127.0.0.1:5077/health");
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            return json.RootElement.TryGetProperty("product", out var product) && product.GetString() == "HyperMemory" &&
                   json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "healthy";
        }
        catch { return false; }
    }

    private static void WriteNewJson<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(true);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Falta {name}.") : value;
    private static string RequiredFile(string path) => File.Exists(path) ? path : throw new FileNotFoundException("Falta un archivo del instalador.", path);
    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "HyperMemorySetup.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }

    private sealed record Options(bool Silent, bool Launch, bool Uninstall, string? StorageRoot, string? HermesRoot, string? Manifest)
    {
        public static Options Parse(string[] args)
        {
            string? Value(string name)
            {
                for (var i = 0; i < args.Length - 1; i++) if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            return new Options(args.Any(x => x.Equals("--silent", StringComparison.OrdinalIgnoreCase) || x.Equals("/S", StringComparison.OrdinalIgnoreCase)),
                args.Any(x => x.Equals("--launch", StringComparison.OrdinalIgnoreCase)),
                args.Any(x => x.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)),
                Value("--storage-root"), Value("--hermes-root"), Value("--manifest"));
        }
    }

    internal sealed record InstallationManifest(string InstallId, string Version, string StorageRoot, string ReleaseDirectory,
        string ApiExecutable, string InstallerExecutable, string HermesRoot, string SkillPath, string MarkerPath, DateTimeOffset InstalledAt);
    internal sealed record OwnershipMarker(string InstallId, string StorageRoot, string Version);
}
