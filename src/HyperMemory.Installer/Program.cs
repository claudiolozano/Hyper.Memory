using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace HyperMemory.Installer;

internal static class Program
{
    private const string ProductVersion = "2.0.0";
    private const string MemoryProviderName = "hypermemory";
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
            if (options.Supervise) return SuperviseAsync(Required(options.Manifest, "--manifest")).GetAwaiter().GetResult();
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
        var configDirectory = Path.Combine(root, "app", "config");
        var authTokenPath = Path.Combine(configDirectory, "auth-token.txt");
        var runtimeDirectory = Path.Combine(root, "app", "runtime");
        var supervisorPidPath = Path.Combine(runtimeDirectory, "supervisor.json");
        var activeInstallationPath = Path.Combine(root, "app", "active-installation.json");
        var hermesBase = ResolveHermesRoot(hermesRoot);
        var skillPath = Path.GetFullPath(Path.Combine(hermesBase, "skills", "hyper-memory"));
        var pluginPath = Path.GetFullPath(Path.Combine(hermesBase, "plugins", MemoryProviderName));
        var upgradeManifestPath = FindOwnedUpgradeManifest(activeInstallationPath, root, hermesBase);
        MigrationBackup? upgradeBackup = null;
        var upgradeDetached = false;
        string? newApiExecutable = null;
        var previousMemoryProvider = GetHermesConfigValue(hermesBase, "memory.provider");
        if (!string.IsNullOrWhiteSpace(previousMemoryProvider) &&
            !string.Equals(previousMemoryProvider, MemoryProviderName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Hermes ya utiliza el proveedor de memoria '{previousMemoryProvider}'. HyperMemory no lo reemplazará automáticamente.");
        if (string.Equals(previousMemoryProvider, MemoryProviderName, StringComparison.OrdinalIgnoreCase))
            previousMemoryProvider = null;

        var configurationChanged = false;
        try
        {
            if (upgradeManifestPath is null)
            {
                ValidateNewSkillTarget(skillPath);
                ValidateNewPluginTarget(pluginPath);
            }
            Directory.CreateDirectory(release);
            ExtractPayload(release);
            var apiExe = RequiredFile(Path.Combine(release, "api", "HyperMemory.Api.exe"));
            newApiExecutable = apiExe;
            var bridgeDirectory = Path.Combine(release, "bridge");
            RequiredFile(Path.Combine(bridgeDirectory, "HyperMemory.Bridge.exe"));
            var skillSource = RequiredFile(Path.Combine(release, "skill", "SKILL.md"));
            var pluginSource = Path.Combine(release, "plugin");
            RequiredFile(Path.Combine(pluginSource, "__init__.py"));
            RequiredFile(Path.Combine(pluginSource, "plugin.yaml"));

            if (upgradeManifestPath is not null)
            {
                var previousManifest = ReadManifest(upgradeManifestPath);
                upgradeBackup = CreateMigrationBackup(previousManifest, upgradeManifestPath, installId);
                var result = Uninstall(new Options(true, false, false, true, null, null, upgradeManifestPath));
                if (result != 0) throw new InvalidOperationException("No se pudo retirar de forma segura la instalación anterior.");
                upgradeDetached = true;
                upgradeBackup = CompleteMigrationBackup(upgradeBackup);
            }
            ValidateNewSkillTarget(skillPath);
            ValidateNewPluginTarget(pluginPath);

            var installedSetup = Path.Combine(release, "HyperMemorySetup.exe");
            File.Copy(Required(Environment.ProcessPath, "installer executable"), installedSetup, overwrite: false);
            Directory.CreateDirectory(configDirectory);
            var authToken = GetOrCreateAuthToken(authTokenPath);

            Directory.CreateDirectory(Path.Combine(skillPath, "bin"));
            File.Copy(skillSource, Path.Combine(skillPath, "SKILL.md"), overwrite: false);
            foreach (var file in Directory.EnumerateFiles(bridgeDirectory))
                File.Copy(file, Path.Combine(skillPath, "bin", Path.GetFileName(file)), overwrite: false);
            var markerPath = Path.Combine(skillPath, ".hypermemory-owned.json");
            WriteNewJson(markerPath, new OwnershipMarker(installId, root, ProductVersion));

            CopyNewDirectory(pluginSource, pluginPath);
            var pluginMarkerPath = Path.Combine(pluginPath, ".hypermemory-owned.json");
            WriteNewJson(pluginMarkerPath, new OwnershipMarker(installId, root, ProductVersion));
            var pluginConnectionPath = Path.Combine(pluginPath, "connection.json");
            WriteNewJson(pluginConnectionPath, new PluginConnection("http://127.0.0.1:5077", authToken, true, true, true, true));
            RestrictFileToCurrentUser(pluginConnectionPath);
            SetHermesConfigValue(hermesBase, "memory.provider", MemoryProviderName);
            configurationChanged = true;

            Directory.CreateDirectory(installationDirectory);
            var manifest = new InstallationManifest(installId, ProductVersion, root, release, apiExe,
                installedSetup, hermesBase, skillPath, markerPath, DateTimeOffset.UtcNow,
                pluginPath, pluginMarkerPath, previousMemoryProvider, authTokenPath, supervisorPidPath, activeInstallationPath);
            WriteNewJson(manifestPath, manifest);
            WriteCurrentJson(activeInstallationPath, new ActiveInstallation(installId, manifestPath, ProductVersion));

            var launchCommand = $"\"{installedSetup}\" --supervise --manifest \"{manifestPath}\"";
            using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
                runKey.SetValue(RegistryValueName, launchCommand, RegistryValueKind.String);
            RegisterUninstaller(manifest, manifestPath);
            RetireOldManifests(installationDirectory, manifestPath);

            if (startImmediately)
            {
                var launcher = Process.Start(new ProcessStartInfo(installedSetup,
                    $"--supervise --manifest \"{manifestPath}\"")
                { UseShellExecute = false, CreateNoWindow = true });
                if (launcher is null || !WaitForHealthAsync(TimeSpan.FromSeconds(20), root, ProductVersion).GetAwaiter().GetResult())
                    throw new InvalidOperationException("La instalación terminó, pero el servicio local no pudo iniciarse. Reinicia Windows para completar el arranque.");
            }

            Log($"Installed {installId} at {root}");
            return manifest;
        }
        catch
        {
            if (newApiExecutable is not null)
            {
                try { StopOwnedApi(newApiExecutable); }
                catch (Exception error) { Log($"Rollback could not stop the new API: {error}"); }
            }
            RollbackFailedInstall(installId, hermesBase, skillPath, pluginPath, previousMemoryProvider,
                configurationChanged, manifestPath, activeInstallationPath);
            if (upgradeDetached && upgradeBackup is not null)
                RestoreMigrationBackup(upgradeBackup);
            throw;
        }
    }

    private static async Task<int> LaunchAsync(string manifestPath)
    {
        var manifest = ReadManifest(manifestPath);
        if (await IsHealthyAsync(manifest.StorageRoot, manifest.Version)) return 0;
        if (await IsEndpointOccupiedAsync()) return 4;
        var authArgument = string.IsNullOrWhiteSpace(manifest.AuthTokenPath) ? "" :
            $" --auth-token-file \"{manifest.AuthTokenPath}\"";
        var process = Process.Start(new ProcessStartInfo(manifest.ApiExecutable,
            $"--storage-root \"{manifest.StorageRoot}\"{authArgument}")
        {
            WorkingDirectory = Path.GetDirectoryName(manifest.ApiExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null) return 2;
        return await WaitForHealthAsync(TimeSpan.FromSeconds(20), manifest.StorageRoot, manifest.Version, process.Id) ? 0 : 3;
    }

    private static async Task<int> SuperviseAsync(string manifestPath)
    {
        var manifest = ReadManifest(manifestPath);
        if (!IsActiveInstallation(manifest, manifestPath)) return 5;
        var mutexName = $"Local\\HyperMemorySupervisor-{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.StorageRoot)))[..24]}";
        using var mutex = new Mutex(initiallyOwned: true, mutexName, out var created);
        if (!created) return 0;
        if (!string.IsNullOrWhiteSpace(manifest.SupervisorPidPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifest.SupervisorPidPath)!);
            WriteCurrentJson(manifest.SupervisorPidPath, new SupervisorState(Environment.ProcessId, manifest.InstallId, Environment.ProcessPath ?? ""));
        }
        var failures = 0;
        while (IsActiveInstallation(manifest, manifestPath))
        {
            if (await IsHealthyAsync(manifest.StorageRoot, manifest.Version))
            {
                failures = 0;
                await Task.Delay(TimeSpan.FromSeconds(3));
                continue;
            }
            if (await IsEndpointOccupiedAsync()) return 4;
            var result = await LaunchAsync(manifestPath);
            if (result == 4) return result;
            failures = result == 0 ? 0 : failures + 1;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, failures * 2))));
        }
        return 0;
    }

    private static int Uninstall(Options options)
    {
        var manifestPath = Required(options.Manifest, "--manifest");
        var manifest = ReadManifest(manifestPath);
        if (!IsActiveInstallation(manifest, manifestPath))
            throw new InvalidOperationException("Esta instalación ya no es la instalación activa. No se modificó Hermes.");
        var eraseMemory = options.EraseMemory;
        if (options.Silent && eraseMemory &&
            !string.Equals(Path.GetFullPath(options.ConfirmStorageRoot ?? ""), Path.GetFullPath(manifest.StorageRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Para borrar memoria en modo silencioso se requiere --confirm-storage-root con la ruta exacta.");
        if (!options.Silent)
        {
            ApplicationConfiguration.Initialize();
            var answer = MessageBox.Show(
                "Se retirará HyperMemory de Hermes y del inicio automático sin afectar al agente. ¿Continuar?",
                "Desinstalar HyperMemory", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return 0;
            var dataChoice = MessageBox.Show(
                "¿Deseas CONSERVAR la memoria histórica para poder recuperarla más adelante?\n\nSí = conservar (recomendado)\nNo = borrar permanentemente\nCancelar = no desinstalar",
                "Memoria histórica", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (dataChoice == DialogResult.Cancel) return 0;
            eraseMemory = dataChoice == DialogResult.No;
            if (eraseMemory && MessageBox.Show(
                    $"La memoria de esta ubicación se borrará de forma permanente:\n\n{manifest.StorageRoot}\n\nEsta acción no se puede deshacer. ¿Confirmas el borrado?",
                    "Confirmar borrado permanente", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return 0;
        }

        StopOwnedSupervisor(manifest);
        StopOwnedApi(manifest.ApiExecutable);
        RemoveOwnedStartup(manifest, manifestPath);
        RestoreOwnedMemoryProvider(manifest);
        RemoveOwnedPlugin(manifest);
        RemoveOwnedSkill(manifest);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        if (!string.IsNullOrWhiteSpace(manifest.ActiveInstallationPath))
            WriteCurrentJson(manifest.ActiveInstallationPath,
                new ActiveInstallation(manifest.InstallId, manifestPath, manifest.Version, "uninstalled"));
        if (eraseMemory) EraseHistoricalMemory(manifest);
        var receipt = Path.Combine(manifest.StorageRoot, $"UNINSTALLED-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.txt");
        using (var writer = new StreamWriter(new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
        {
            writer.WriteLine("HyperMemory was detached from Hermes and Windows startup.");
            writer.WriteLine(eraseMemory
                ? "Historical memory was permanently erased after explicit confirmation. Installed binaries were preserved."
                : "Historical memory and installed binaries were preserved for recovery.");
            writer.WriteLine($"Installation: {manifest.InstallId}");
        }
        Log($"Uninstalled integration {manifest.InstallId}; historical memory {(eraseMemory ? "erased" : "preserved")} at {manifest.StorageRoot}");
        if (!options.Silent)
            MessageBox.Show(eraseMemory
                    ? "HyperMemory se retiró correctamente de Hermes y la memoria histórica fue borrada."
                    : "HyperMemory se retiró correctamente de Hermes. La memoria histórica quedó conservada como respaldo.",
                "HyperMemory", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static void EraseHistoricalMemory(InstallationManifest manifest)
    {
        var root = Path.GetFullPath(manifest.StorageRoot);
        if (!string.Equals(Path.GetFileName(root), "Hyper_Memory", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(root) || (new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La raíz de memoria no supera las comprobaciones de seguridad; no se borró ningún recuerdo.");

        var eventsPath = Path.Combine(root, "events");
        DeleteExactDataDirectory(eventsPath, Path.Combine(root, "events"));
        foreach (var file in new[] { "hypermemory.sqlite3", "hypermemory.sqlite3-wal", "hypermemory.sqlite3-shm" })
        {
            var path = Path.GetFullPath(Path.Combine(root, file));
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La ruta de datos no es segura.");
            if (File.Exists(path)) File.Delete(path);
        }

        var migrationRoot = Path.Combine(root, "app", "migration-backups");
        if (Directory.Exists(migrationRoot))
        {
            if ((new DirectoryInfo(migrationRoot).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("La carpeta de respaldos es un enlace; no se borraron los respaldos.");
            foreach (var backup in Directory.EnumerateDirectories(migrationRoot, "*", SearchOption.TopDirectoryOnly))
                DeleteExactDataDirectory(Path.Combine(backup, "database"), Path.Combine(backup, "database"));
        }
    }

    private static void DeleteExactDataDirectory(string actual, string expected)
    {
        actual = Path.GetFullPath(actual);
        expected = Path.GetFullPath(expected);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(actual)) return;
        var root = new DirectoryInfo(actual);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La carpeta de datos es un enlace; no se borró.");
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Los datos contienen un enlace; no se borraron.");
        Directory.Delete(actual, recursive: true);
    }

    private static void RemoveOwnedStartup(InstallationManifest manifest, string manifestPath)
    {
        var expected = $"\"{manifest.InstallerExecutable}\" --supervise --manifest \"{manifestPath}\"";
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        var actual = runKey?.GetValue(RegistryValueName) as string;
        if (string.Equals(actual, expected, StringComparison.Ordinal)) runKey?.DeleteValue(RegistryValueName, false);
    }

    private static void StopOwnedSupervisor(InstallationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.SupervisorPidPath) || !File.Exists(manifest.SupervisorPidPath)) return;
        var expectedPidPath = Path.GetFullPath(Path.Combine(manifest.StorageRoot, "app", "runtime", "supervisor.json"));
        if (!string.Equals(Path.GetFullPath(manifest.SupervisorPidPath), expectedPidPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La ruta de control del supervisor no es segura.");
        var state = JsonSerializer.Deserialize<SupervisorState>(File.ReadAllText(manifest.SupervisorPidPath));
        if (state is null || state.InstallId != manifest.InstallId || state.ProcessId == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            var actual = process.MainModule?.FileName;
            if (!string.Equals(actual is null ? null : Path.GetFullPath(actual), Path.GetFullPath(manifest.InstallerExecutable), StringComparison.OrdinalIgnoreCase))
                return;
            process.Kill(entireProcessTree: false);
            process.WaitForExit(10_000);
        }
        catch (ArgumentException) { }
    }

    private static void RollbackFailedInstall(string installId, string hermesRoot, string skillPath, string pluginPath,
        string? previousMemoryProvider, bool configurationChanged, string manifestPath, string activeInstallationPath)
    {
        try
        {
            if (configurationChanged && string.Equals(GetHermesConfigValue(hermesRoot, "memory.provider"), MemoryProviderName, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(previousMemoryProvider)) UnsetHermesConfigValue(hermesRoot, "memory.provider");
                else SetHermesConfigValue(hermesRoot, "memory.provider", previousMemoryProvider);
            }
        }
        catch (Exception error) { Log($"Rollback could not restore Hermes configuration: {error}"); }

        TryRemoveCreatedDirectory(pluginPath, installId);
        TryRemoveCreatedDirectory(skillPath, installId);
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey?.GetValue(RegistryValueName) is string value && value.Contains(installId, StringComparison.Ordinal))
                runKey.DeleteValue(RegistryValueName, false);
            string? uninstallCommand;
            using (var uninstall = Registry.CurrentUser.OpenSubKey(UninstallKeyPath))
                uninstallCommand = uninstall?.GetValue("UninstallString") as string;
            if (uninstallCommand is not null && uninstallCommand.Contains(installId, StringComparison.Ordinal))
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
        }
        catch (Exception error) { Log($"Rollback could not clean registry: {error}"); }
        try
        {
            if (File.Exists(activeInstallationPath))
            {
                var active = JsonSerializer.Deserialize<ActiveInstallation>(File.ReadAllText(activeInstallationPath));
                if (active?.InstallId == installId)
                    WriteCurrentJson(activeInstallationPath, active with { Status = "failed" });
            }
            if (File.Exists(manifestPath)) File.Move(manifestPath, manifestPath + ".failed", overwrite: false);
        }
        catch (Exception error) { Log($"Rollback could not retire failed manifest: {error}"); }
    }

    private static MigrationBackup CreateMigrationBackup(InstallationManifest previous, string previousManifestPath,
        string incomingInstallId)
    {
        var backupDirectory = Path.GetFullPath(Path.Combine(previous.StorageRoot, "app", "migration-backups", incomingInstallId));
        var expectedParent = Path.GetFullPath(Path.Combine(previous.StorageRoot, "app", "migration-backups"));
        if (!string.Equals(Path.GetDirectoryName(backupDirectory), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La ruta del respaldo de actualización no es segura.");
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
            throw new IOException("Ya existe un respaldo con el identificador de esta actualización.");

        Directory.CreateDirectory(backupDirectory);
        var integrationDirectory = Path.Combine(backupDirectory, "integration");
        Directory.CreateDirectory(integrationDirectory);
        SafeCopyDirectory(previous.SkillPath, Path.Combine(integrationDirectory, "skill"));
        if (!string.IsNullOrWhiteSpace(previous.PluginPath) && Directory.Exists(previous.PluginPath))
            SafeCopyDirectory(previous.PluginPath, Path.Combine(integrationDirectory, "plugin"));
        File.Copy(previousManifestPath, Path.Combine(backupDirectory, "previous-installation.json"), overwrite: false);

        var backup = BuildMigrationBackup(previous, previousManifestPath, backupDirectory, databaseSnapshotComplete: false);
        WriteNewJson(Path.Combine(backupDirectory, "backup-manifest.json"), backup);
        ValidateMigrationBackup(backup);
        return backup;
    }

    private static MigrationBackup CompleteMigrationBackup(MigrationBackup backup)
    {
        var dataDirectory = Path.Combine(backup.BackupDirectory, "database");
        Directory.CreateDirectory(dataDirectory);
        var database = Path.Combine(backup.PreviousInstallation.StorageRoot, "hypermemory.sqlite3");
        foreach (var source in new[] { database, database + "-wal", database + "-shm" })
            if (File.Exists(source))
                File.Copy(source, Path.Combine(dataDirectory, Path.GetFileName(source)), overwrite: false);

        var completed = BuildMigrationBackup(backup.PreviousInstallation, backup.PreviousManifestPath,
            backup.BackupDirectory, databaseSnapshotComplete: true);
        WriteCurrentJson(Path.Combine(backup.BackupDirectory, "backup-manifest.json"), completed);
        ValidateMigrationBackup(completed);
        return completed;
    }

    private static MigrationBackup BuildMigrationBackup(InstallationManifest previous, string previousManifestPath,
        string backupDirectory, bool databaseSnapshotComplete)
    {
        var files = Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), "backup-manifest.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new BackupFile(Path.GetRelativePath(backupDirectory, path), new FileInfo(path).Length,
                HashFile(path)))
            .ToArray();
        return new MigrationBackup(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, backupDirectory,
            previousManifestPath, previous, databaseSnapshotComplete, files);
    }

    private static void ValidateMigrationBackup(MigrationBackup backup)
    {
        var boundary = Path.GetFullPath(backup.BackupDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (backup.Files.Count == 0) throw new InvalidOperationException("El respaldo de actualización está vacío.");
        foreach (var item in backup.Files)
        {
            var path = Path.GetFullPath(Path.Combine(backup.BackupDirectory, item.RelativePath));
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("El respaldo de actualización contiene una ruta no válida.");
            var info = new FileInfo(path);
            var hash = HashFile(path);
            if (info.Length != item.Length || !string.Equals(hash, item.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"Falló la integridad del respaldo: {item.RelativePath}");
        }
    }

    private static void RestoreMigrationBackup(MigrationBackup backup)
    {
        ValidateMigrationBackup(backup);
        var previous = backup.PreviousInstallation;
        if (Directory.Exists(previous.SkillPath) || File.Exists(previous.SkillPath) ||
            (!string.IsNullOrWhiteSpace(previous.PluginPath) && (Directory.Exists(previous.PluginPath) || File.Exists(previous.PluginPath))))
            throw new InvalidOperationException("No se puede restaurar la versión anterior porque uno de sus destinos está ocupado.");

        SafeCopyDirectory(Path.Combine(backup.BackupDirectory, "integration", "skill"), previous.SkillPath);
        var pluginBackup = Path.Combine(backup.BackupDirectory, "integration", "plugin");
        if (!string.IsNullOrWhiteSpace(previous.PluginPath) && Directory.Exists(pluginBackup))
            SafeCopyDirectory(pluginBackup, previous.PluginPath);

        if (backup.DatabaseSnapshotComplete)
            RestoreDatabaseSnapshot(backup);

        SetHermesConfigValue(previous.HermesRoot, "memory.provider", MemoryProviderName);
        WriteCurrentJson(previous.ActiveInstallationPath!,
            new ActiveInstallation(previous.InstallId, backup.PreviousManifestPath, previous.Version));
        var launchCommand = $"\"{previous.InstallerExecutable}\" --supervise --manifest \"{backup.PreviousManifestPath}\"";
        using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
            runKey.SetValue(RegistryValueName, launchCommand, RegistryValueKind.String);
        RegisterUninstaller(previous, backup.PreviousManifestPath);
        Process.Start(new ProcessStartInfo(previous.InstallerExecutable,
            $"--supervise --manifest \"{backup.PreviousManifestPath}\"")
        { UseShellExecute = false, CreateNoWindow = true });
        Log($"Restored previous installation {previous.InstallId} from verified backup {backup.BackupId}");
    }

    private static void RestoreDatabaseSnapshot(MigrationBackup backup)
    {
        var database = Path.GetFullPath(Path.Combine(backup.PreviousInstallation.StorageRoot, "hypermemory.sqlite3"));
        var storageBoundary = Path.GetFullPath(backup.PreviousInstallation.StorageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!database.StartsWith(storageBoundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La ruta de la base de datos no es segura.");
        var snapshotDirectory = Path.Combine(backup.BackupDirectory, "database");
        foreach (var target in new[] { database, database + "-wal", database + "-shm" })
        {
            var source = Path.Combine(snapshotDirectory, Path.GetFileName(target));
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(source)) File.Copy(source, target, overwrite: false);
        }
    }

    private static void SafeCopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        var root = new DirectoryInfo(source);
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("No se respaldará una carpeta que sea enlace o unión.");
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("El respaldo contiene un enlace o unión y fue rechazado.");
        CopyNewDirectory(source, destination);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryRemoveCreatedDirectory(string path, string installId)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var markerPath = Path.Combine(path, ".hypermemory-owned.json");
            if (File.Exists(markerPath))
            {
                var marker = JsonSerializer.Deserialize<OwnershipMarker>(File.ReadAllText(markerPath));
                if (marker?.InstallId != installId) return;
            }
            foreach (var entry in info.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return;
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) { Log($"Rollback could not remove {path}: {error}"); }
    }

    private static void RemoveOwnedSkill(InstallationManifest manifest)
    {
        var expected = Path.GetFullPath(Path.Combine(manifest.HermesRoot, "skills", "hyper-memory"));
        var actual = Path.GetFullPath(manifest.SkillPath);
        RemoveOwnedDirectory(actual, expected, manifest.MarkerPath, manifest.InstallId, "Skill");
    }

    private static void RemoveOwnedPlugin(InstallationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.PluginPath) || string.IsNullOrWhiteSpace(manifest.PluginMarkerPath)) return;
        var expected = Path.GetFullPath(Path.Combine(manifest.HermesRoot, "plugins", MemoryProviderName));
        var actual = Path.GetFullPath(manifest.PluginPath);
        RemoveOwnedDirectory(actual, expected, manifest.PluginMarkerPath, manifest.InstallId, "plugin");
    }

    private static void RemoveOwnedDirectory(string actual, string expected, string markerPath, string installId, string label)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"La ruta del {label} no coincide con la ubicación segura esperada. No se eliminó nada de Hermes.");
        if (!Directory.Exists(actual)) return;
        var rootInfo = new DirectoryInfo(actual);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"El {label} es un enlace o unión. No se eliminó nada.");
        var marker = JsonSerializer.Deserialize<OwnershipMarker>(File.ReadAllText(markerPath));
        if (marker?.InstallId != installId)
            throw new InvalidOperationException($"No se pudo verificar que el {label} pertenezca a esta instalación.");
        foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Se detectó un enlace dentro del {label}: {entry.FullName}. No se eliminó nada.");
        Directory.Delete(actual, recursive: true);
    }

    private static void RestoreOwnedMemoryProvider(InstallationManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.PluginPath)) return;
        var current = GetHermesConfigValue(manifest.HermesRoot, "memory.provider");
        if (!string.Equals(current, MemoryProviderName, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(manifest.PreviousMemoryProvider))
            UnsetHermesConfigValue(manifest.HermesRoot, "memory.provider");
        else
            SetHermesConfigValue(manifest.HermesRoot, "memory.provider", manifest.PreviousMemoryProvider);
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
        key.SetValue("DisplayVersion", manifest.Version, RegistryValueKind.String);
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

    private static void ValidateNewPluginTarget(string pluginPath)
    {
        if (Directory.Exists(pluginPath) || File.Exists(pluginPath))
            throw new InvalidOperationException("Ya existe un plugin llamado hypermemory. Para proteger Hermes no será sobrescrito; desinstala primero la instalación anterior.");
        var pluginsRoot = Path.GetDirectoryName(pluginPath)!;
        Directory.CreateDirectory(pluginsRoot);
        if ((new DirectoryInfo(pluginsRoot).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("La carpeta de plugins de Hermes es un enlace o unión y no puede modificarse de forma segura.");
    }

    private static void CopyNewDirectory(string source, string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"El destino ya existe: {destination}");
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
    }

    private static string? GetHermesConfigValue(string hermesRoot, string key) =>
        RunHermesConfig(hermesRoot, "get", key, null).Trim() is { Length: > 0 } value ? value : null;

    private static void SetHermesConfigValue(string hermesRoot, string key, string value) =>
        RunHermesConfig(hermesRoot, "set", key, value);

    private static void UnsetHermesConfigValue(string hermesRoot, string key) =>
        RunHermesConfig(hermesRoot, "unset", key, null);

    private static string RunHermesConfig(string hermesRoot, string action, string key, string? value)
    {
        var agentRoot = Path.Combine(hermesRoot, "hermes-agent");
        var python = RequiredFile(Path.Combine(agentRoot, "venv", "Scripts", "python.exe"));
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = agentRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["HERMES_HOME"] = hermesRoot;
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("hermes_cli.main");
        info.ArgumentList.Add("config");
        info.ArgumentList.Add(action);
        info.ArgumentList.Add(key);
        if (value is not null) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo ejecutar la configuración oficial de Hermes.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Hermes tardó demasiado en actualizar su configuración.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Hermes no pudo actualizar su configuración: {error.Trim()}");
        return output;
    }

    private static string ResolveHermesRoot(string? requestedRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestedRoot))
            return Path.GetFullPath(requestedRoot);

        var environmentRoot = Environment.GetEnvironmentVariable("HERMES_HOME");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
            return Path.GetFullPath(environmentRoot);

        if (OperatingSystem.IsWindows())
        {
            var desktopRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes");
            if (Directory.Exists(desktopRoot) &&
                (File.Exists(Path.Combine(desktopRoot, "config.yaml")) ||
                 Directory.Exists(Path.Combine(desktopRoot, "hermes-agent")) ||
                 Directory.Exists(Path.Combine(desktopRoot, "skills"))))
                return Path.GetFullPath(desktopRoot);
        }

        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hermes"));
    }

    private static string? FindOwnedUpgradeManifest(string activeInstallationPath, string storageRoot, string hermesRoot)
    {
        if (!File.Exists(activeInstallationPath)) return null;
        var active = JsonSerializer.Deserialize<ActiveInstallation>(File.ReadAllText(activeInstallationPath))
            ?? throw new InvalidOperationException("El registro de la instalación activa no es válido.");
        if (!string.Equals(active.Status, "active", StringComparison.OrdinalIgnoreCase)) return null;

        var manifestPath = Path.GetFullPath(active.ManifestPath);
        var expectedDirectory = Path.GetFullPath(Path.Combine(storageRoot, "app", "installations"));
        if (!string.Equals(Path.GetDirectoryName(manifestPath), expectedDirectory, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(manifestPath))
            throw new InvalidOperationException("No se pudo verificar de forma segura la instalación anterior.");

        var manifest = ReadManifest(manifestPath);
        if (manifest.InstallId != active.InstallId ||
            !string.Equals(Path.GetFullPath(manifest.StorageRoot), Path.GetFullPath(storageRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(manifest.HermesRoot), Path.GetFullPath(hermesRoot), StringComparison.OrdinalIgnoreCase) ||
            !IsActiveInstallation(manifest, manifestPath))
            throw new InvalidOperationException("La instalación anterior no coincide con Hermes o con la memoria seleccionada.");

        if (!Version.TryParse(manifest.Version, out var installedVersion) || !Version.TryParse(ProductVersion, out var incomingVersion))
            throw new InvalidOperationException("No se pudo comparar la versión instalada.");
        if (installedVersion >= incomingVersion)
            throw new InvalidOperationException($"HyperMemory {manifest.Version} ya está instalado. No se realizó ningún cambio.");
        return manifestPath;
    }

    private static InstallationManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(Path.GetFullPath(path)))
        ?? throw new InvalidOperationException("El manifiesto de instalación no es válido.");

    private static string GetOrCreateAuthToken(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length < 32) throw new InvalidOperationException("El token local de HyperMemory no es válido.");
            RestrictFileToCurrentUser(path);
            return existing;
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
        writer.Write(token);
        writer.Flush();
        stream.Flush(true);
        RestrictFileToCurrentUser(path);
        return token;
    }

    private static void RestrictFileToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No se pudo identificar al usuario actual.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void WriteCurrentJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsActiveInstallation(InstallationManifest manifest, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.ActiveInstallationPath)) return true;
        try
        {
            var active = JsonSerializer.Deserialize<ActiveInstallation>(File.ReadAllText(manifest.ActiveInstallationPath));
            return active?.Status == "active" && active.InstallId == manifest.InstallId &&
                string.Equals(Path.GetFullPath(active.ManifestPath), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private static void RetireOldManifests(string installationDirectory, string currentManifest)
    {
        foreach (var path in Directory.EnumerateFiles(installationDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentManifest), StringComparison.OrdinalIgnoreCase)) continue;
            var retired = path + $".retired-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}";
            File.Move(path, retired, overwrite: false);
        }
    }

    private static async Task<bool> WaitForHealthAsync(TimeSpan timeout, string expectedStorageRoot, string expectedVersion, int? expectedProcessId = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (await IsHealthyAsync(expectedStorageRoot, expectedVersion, expectedProcessId)) return true;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static async Task<bool> IsHealthyAsync(string expectedStorageRoot, string expectedVersion, int? expectedProcessId = null)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync("http://127.0.0.1:5077/health");
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var root = json.RootElement;
            return root.TryGetProperty("product", out var product) && product.GetString() == "HyperMemory" &&
                   root.TryGetProperty("status", out var status) && status.GetString() == "healthy" &&
                   root.TryGetProperty("apiVersion", out var version) && version.GetString() == expectedVersion &&
                   root.TryGetProperty("storageRoot", out var storage) &&
                   string.Equals(Path.GetFullPath(storage.GetString() ?? ""), Path.GetFullPath(expectedStorageRoot), StringComparison.OrdinalIgnoreCase) &&
                   (!expectedProcessId.HasValue || root.TryGetProperty("processId", out var pid) && pid.GetInt32() == expectedProcessId.Value);
        }
        catch { return false; }
    }

    private static async Task<bool> IsEndpointOccupiedAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(750) };
            using var response = await client.GetAsync("http://127.0.0.1:5077/live");
            return true;
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

    private sealed record Options(bool Silent, bool Launch, bool Supervise, bool Uninstall, string? StorageRoot,
        string? HermesRoot, string? Manifest, bool EraseMemory = false, string? ConfirmStorageRoot = null)
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
                args.Any(x => x.Equals("--supervise", StringComparison.OrdinalIgnoreCase)),
                args.Any(x => x.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)),
                Value("--storage-root"), Value("--hermes-root"), Value("--manifest"),
                args.Any(x => x.Equals("--erase-memory", StringComparison.OrdinalIgnoreCase)),
                Value("--confirm-storage-root"));
        }
    }

    internal sealed record InstallationManifest(string InstallId, string Version, string StorageRoot, string ReleaseDirectory,
        string ApiExecutable, string InstallerExecutable, string HermesRoot, string SkillPath, string MarkerPath, DateTimeOffset InstalledAt,
        string? PluginPath = null, string? PluginMarkerPath = null, string? PreviousMemoryProvider = null,
        string? AuthTokenPath = null, string? SupervisorPidPath = null, string? ActiveInstallationPath = null);
    internal sealed record OwnershipMarker(string InstallId, string StorageRoot, string Version);
    internal sealed record PluginConnection(string Endpoint, string Token, bool RedactSecrets,
        bool CaptureEnabled, bool UserOptOutEnabled, bool OperationalEnabled);
    internal sealed record ActiveInstallation(string InstallId, string ManifestPath, string Version, string Status = "active");
    internal sealed record SupervisorState(int ProcessId, string InstallId, string Executable);
    internal sealed record BackupFile(string RelativePath, long Length, string Sha256);
    internal sealed record MigrationBackup(string BackupId, DateTimeOffset CreatedAt, string BackupDirectory,
        string PreviousManifestPath, InstallationManifest PreviousInstallation, bool DatabaseSnapshotComplete,
        IReadOnlyList<BackupFile> Files);
}
