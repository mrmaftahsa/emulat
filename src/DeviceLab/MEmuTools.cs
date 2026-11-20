using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeviceLab;

internal sealed partial class MEmuTools(string executable)
{
    private const string ToolName = "memuc.exe";
    public string Executable { get; } = executable;
    public bool IsInstalled => IsCommandTool(Executable);

    public static bool IsCommandTool(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && Path.GetFileName(path).Equals(ToolName, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static string? FindExecutable(string? savedPath)
    {
        if (IsCommandTool(savedPath))
            return savedPath;

        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var folder = Environment.GetFolderPath(root, Environment.SpecialFolderOption.DoNotVerify);
            var path = FindInFolder(Path.Combine(folder, "Microvirt")) ?? FindInFolder(Path.Combine(folder, "MEmu"));
            if (path is not null)
                return path;
        }

        if (OperatingSystem.IsWindows() && FindInRegistry() is { } registered)
            return registered;

        // A running copy also reveals installations on a custom drive.
        foreach (var name in new[] { "MEmu", "MEmuConsole", "MEmuHeadless", "MEmuSVC" })
        {
            string? runningPath = null;
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (runningPath is not null)
                        continue;
                    try
                    {
                        var folder = Path.GetDirectoryName(process.MainModule?.FileName);
                        runningPath = FindInFolder(folder) ?? FindInFolder(Path.GetDirectoryName(folder));
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Some installed processes run at a higher privilege level.
                    }
                }
            }
            if (runningPath is not null)
                return runningPath;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            var path = FindInFolder(Path.Combine(drive.Name, "Microvirt"))
                ?? FindInFolder(Path.Combine(drive.Name, "Program Files", "Microvirt"))
                ?? FindInFolder(Path.Combine(drive.Name, "Program Files (x86)", "Microvirt"))
                ?? FindInFolder(Path.Combine(drive.Name, "MEmu"));
            if (path is not null)
                return path;
        }
        return null;
    }

    public static string? FindInFolder(string? folder)
    {
        folder = folder?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            return null;

        return new[] { Path.Combine(folder, ToolName), Path.Combine(folder, "MEmu", ToolName),
            Path.Combine(folder, "Microvirt", "MEmu", ToolName) }.FirstOrDefault(IsCommandTool);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindInRegistry()
    {
        const string windows = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                foreach (var key in new[] { @"SOFTWARE\Microvirt", @"SOFTWARE\Microvirt\MEmu" })
                {
                    using var vendor = root.OpenSubKey(key);
                    var path = FindInFolder(vendor?.GetValue("InstallPath") as string)
                        ?? FindInFolder(vendor?.GetValue("InstallDir") as string)
                        ?? FindInFolder(vendor?.GetValue("InstallLocation") as string)
                        ?? FindInFolder(vendor?.GetValue("Path") as string);
                    if (path is not null)
                        return path;
                }

                foreach (var file in new[] { "MEmu.exe", "MEmuConsole.exe", ToolName })
                {
                    using var appPaths = root.OpenSubKey($@"{windows}\App Paths\{file}");
                    var path = FindFromRegisteredCommand(appPaths?.GetValue(null) as string);
                    if (path is not null)
                        return path;
                }

                using var uninstall = root.OpenSubKey($@"{windows}\Uninstall");
                foreach (var key in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(key);
                    var name = entry?.GetValue("DisplayName") as string;
                    if (name is null || !name.StartsWith("MEmu", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var path = FindInFolder(entry?.GetValue("InstallLocation") as string)
                        ?? FindFromRegisteredCommand(entry?.GetValue("UninstallString") as string)
                        ?? FindFromRegisteredCommand(entry?.GetValue("DisplayIcon") as string);
                    if (path is not null)
                        return path;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                // Detected through another registry view or selected manually.
            }
        }
        return null;
    }

    internal static string? FindFromRegisteredCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var end = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        end = expanded[0] == '"' ? expanded.IndexOf('"', 1) : end < 0 ? -1 : end + 4;
        if (end <= 0)
            return null;

        var path = expanded[..end].TrimStart('"');
        return Path.IsPathFullyQualified(path) ? FindInFolder(Path.GetDirectoryName(path)) : null;
    }

    public async Task<IReadOnlyList<AndroidDevice>> ListDevicesAsync()
    {
        var installed = await ReadInventoryAsync(runningOnly: false);
        if (installed.Count == 0)
            return [];
        var running = await ReadInventoryAsync(runningOnly: true);
        return installed.Select(entry => new AndroidDevice(entry.Value, null,
            running.TryGetValue(entry.Key, out var name) && name == entry.Value ? "Running" : "Stopped",
            MEmuIndex: entry.Key)).ToArray();
    }

    public async Task<AndroidDevice> CreateDeviceAsync()
    {
        var before = await ReadInventoryAsync(runningOnly: false);
        await RunAsync("Creating a MEmu device", ["create"], TimeSpan.FromMinutes(8));
        var after = await ReadInventoryAsync(runningOnly: false);
        var added = after.Where(entry => !before.ContainsKey(entry.Key)).ToArray();
        if (added.Length != 1)
            throw new InvalidOperationException("Refresh the MEmu device list to see the result of creation.");

        var device = added[0];
        return new(device.Value, null, "Stopped", MEmuIndex: device.Key);
    }

    public async Task StartDeviceAsync(AndroidDevice device)
    {
        var index = await VerifyTargetAsync(device, runningOnly: false);
        await RunAsync($"Starting {device.Name}", ["start", "-i", index], TimeSpan.FromMinutes(2));
    }

    public async Task StopDeviceAsync(AndroidDevice device)
    {
        var index = await VerifyTargetAsync(device, runningOnly: true);
        await RunAsync($"Stopping {device.Name}", ["stop", "-i", index], TimeSpan.FromMinutes(2));
    }

    public async Task InstallAsync(AndroidDevice device, string apk)
    {
        if (!Path.IsPathFullyQualified(apk) || !File.Exists(apk)
            || !Path.GetExtension(apk).Equals(".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing .apk file.");

        var index = await VerifyTargetAsync(device, runningOnly: true);
        await RunAsync("Installing the APK in MEmu", ["installapp", "-i", index, apk], TimeSpan.FromMinutes(3));
    }

    public async Task<string> ReadLogsAsync(AndroidDevice device)
    {
        var index = await VerifyTargetAsync(device, runningOnly: true);
        return await RunAsync("Reading MEmu logs", ["-i", index, "adb", "logcat -d -t 250 -v threadtime"]);
    }

    private async Task<string> VerifyTargetAsync(AndroidDevice device, bool runningOnly)
    {
        var inventory = await ReadInventoryAsync(runningOnly);
        if (device.MEmuIndex is not { } index || !inventory.TryGetValue(index, out var name) || name != device.Name)
            throw new InvalidOperationException("The selected MEmu device changed. Refresh the list and try again.");

        return index.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<int, string>> ReadInventoryAsync(bool runningOnly)
    {
        var text = await RunAsync("Reading MEmu devices", runningOnly ? ["listvms", "--running"] : ["listvms"]);
        var inventory = new Dictionary<int, string>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields.Length < 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                continue;

            // The title follows the index. The list ends with numeric window, status, and process fields.
            var end = fields.Length;
            while (end > 2 && long.TryParse(fields[end - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                end--;
            var name = string.Join(',', fields[1..end]).Trim().Trim('"');
            if (name.Length > 0)
                inventory.TryAdd(index, name);
        }
        return inventory;
    }

    private async Task<string> RunAsync(string operation, string[] args, TimeSpan? timeout = null)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Open Setup to install or reconnect MEmu.");

        var start = new ProcessStartInfo(Executable) { WorkingDirectory = Path.GetDirectoryName(Executable)! };
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        var result = await ProcessRunner.RunAsync(start, timeout ?? TimeSpan.FromSeconds(12));
        result.EnsureSuccess(operation);
        if (CommandFailure().IsMatch(result.Output))
            throw new InvalidOperationException($"{operation} failed. {result.ShortDetail}".Trim());

        return result.Output;
    }

    [GeneratedRegex(@"^(?:error|failed|failure)(?:\b|:)|^.*\b(?:operation|install(?:app|ation)?|start\s+(?:vm|emulator)|stop\s+(?:vm|emulator)|create\s+(?:vm|emulator))\s+failed\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandFailure();
}
