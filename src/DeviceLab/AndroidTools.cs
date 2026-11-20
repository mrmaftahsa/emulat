using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DeviceLab;

internal sealed record AndroidDevice(string Name, string? Serial, string Status, string? LaunchError = null, int? MEmuIndex = null)
{
    public bool IsOnline => Status == "Running";
}

internal sealed record SystemImage(string Package, string DisplayName);
internal sealed record Diagnostic(string Check, string Status, string Detail);

internal sealed partial class AndroidTools
{
    private static readonly TimeSpan StandardTimeout = TimeSpan.FromSeconds(12);
    private const string Offline = "Stopped";
    private const string Busy = "Starting";
    private readonly Dictionary<string, Launch> launches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> launchFailures = new(StringComparer.Ordinal);

    private sealed record Launch(Task<CommandResult> Completion, DateTimeOffset Deadline, bool Connected);

    public string SdkDirectory { get; }
    public string RuntimeDirectory { get; }
    public string ManagedJavaHome => Path.Combine(RuntimeDirectory, "Java", "21.0.13.1+2");
    public bool HasEmulator => File.Exists(Emulator);
    public bool HasAdb => File.Exists(Adb);
    private string Emulator => Path.Combine(SdkDirectory, "emulator", "emulator.exe");
    private string Adb => Path.Combine(SdkDirectory, "platform-tools", "adb.exe");

    public AndroidTools(string sdkDirectory, string? runtimeDirectory = null)
    {
        SdkDirectory = Path.GetFullPath(sdkDirectory);
        RuntimeDirectory = runtimeDirectory ?? Preferences.Folder;
    }

    public static string FindSdk()
    {
        var localSdk = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        var candidates = new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"), localSdk };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)
            && Directory.Exists(path)) ?? localSdk;
    }

    public async Task<IReadOnlyList<AndroidDevice>> ListDevicesAsync()
    {
        var avds = await RunToolAsync(Emulator, ["-list-avds"]);
        avds.EnsureSuccess("Listing virtual devices");
        var names = Lines(avds.Output).Distinct(StringComparer.Ordinal).ToArray();
        if (!File.Exists(Adb))
            return names.Select(name => new AndroidDevice(name, null, "Unknown")).ToArray();

        var adbDevices = await RunToolAsync(Adb, ["devices"]);
        adbDevices.EnsureSuccess("Reading device status");
        var emulators = Lines(adbDevices.Output)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(entry => entry.Length >= 2 && EmulatorSerial().IsMatch(entry[0]))
            .ToArray();

        var running = new Dictionary<string, AndroidDevice>(StringComparer.Ordinal);
        foreach (var entry in emulators)
        {
            try
            {
                var name = await GetAvdNameAsync(entry[0]);
                if (name is not null)
                    running.TryAdd(name, new(name, entry[0], entry[1] == "device" ? "Running" : Busy));
            }
            catch (TimeoutException)
            {
                // An emulator console can be temporarily unavailable while it starts.
            }
        }

        foreach (var (name, launch) in launches.ToArray())
        {
            if (running.TryGetValue(name, out var device) && device.IsOnline)
            {
                launches[name] = launch with { Connected = true };
                launchFailures.Remove(name);
            }
            else if (launch.Completion.IsCompleted)
            {
                try
                {
                    var result = await launch.Completion;
                    if (!result.Succeeded && !launch.Connected)
                        launchFailures[name] = DescribeLaunchFailure(result);
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
                {
                    if (!launch.Connected)
                        launchFailures[name] = ex.Message;
                }
                launches.Remove(name);
            }
            else if (DateTimeOffset.UtcNow > launch.Deadline && !launch.Connected)
            {
                launchFailures[name] = "Android is taking longer than expected. Check the emulator window.";
            }
        }
        return names.Select(name => running.TryGetValue(name, out var device) ? device
            : new AndroidDevice(name, null,
                launches.TryGetValue(name, out var launch) && DateTimeOffset.UtcNow <= launch.Deadline ? Busy : Offline,
                launchFailures.GetValueOrDefault(name))).ToArray();
    }

    public IReadOnlyList<SystemImage> ListSystemImages()
    {
        var directory = Path.Combine(SdkDirectory, "system-images");
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "source.properties", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SdkDirectory, Path.GetDirectoryName(path)!))
            .Select(relative => relative.Split(Path.DirectorySeparatorChar))
            .Where(parts => parts.Length == 4 && File.Exists(Path.Combine(SdkDirectory,
                Path.Combine(parts), "system.img")))
            .Select(parts => new SystemImage(string.Join(';', parts),
                ImageDisplayName(parts[1], parts[2], parts[3])))
            .Where(image => ImagePackage().IsMatch(image.Package))
            .OrderByDescending(image => ImageApiLevel(image.Package))
            .ThenBy(image => image.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task CreateDeviceAsync(string name, SystemImage image)
    {
        if (!DeviceName().IsMatch(name) || name.EndsWith('.') || ReservedName().IsMatch(name))
            throw new InvalidOperationException(
                "Use 1–64 letters, numbers, hyphens, periods or underscores. Start with a letter or number and choose a valid Windows filename.");

        if (!ImagePackage().IsMatch(image.Package)
            || !ListSystemImages().Any(installed => installed.Package == image.Package))
            throw new InvalidOperationException("Select an installed Android system image.");

        var existing = await RunToolAsync(Emulator, ["-list-avds"]);
        existing.EnsureSuccess("Checking existing virtual devices");
        if (Lines(existing.Output).Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A virtual device named {name} already exists.");

        var folder = FindCommandLineTools()
            ?? throw new InvalidOperationException("Open Setup to finish installing the Android tools.");
        var java = await FindJavaHomeAsync()
            ?? throw new InvalidOperationException("Open Setup and select Set up Android to install Java automatically.");
        var manager = Path.Combine(folder, "bin", "avdmanager.bat");
        var start = CreateBatchStartInfo(manager, java, $"create avd --name {name} --package \"{image.Package}\"");
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMinutes(2), "no");
        result.EnsureSuccess($"Creating {name}");
    }

    public async Task StartDeviceAsync(AndroidDevice device)
    {
        EnsureFile(Emulator, "Android Emulator");
        if (device.Status != Offline && device.Status != "Unknown")
            throw new InvalidOperationException("This device is already starting or running.");
        if (device.Name.Length == 0 || device.Name.Contains('"') || device.Name.Any(char.IsControl))
            throw new InvalidOperationException("The virtual device name is invalid.");

        if (launches.TryGetValue(device.Name, out var previous) && !previous.Completion.IsCompleted)
            throw new InvalidOperationException("This emulator is already open. Close its window before trying again.");

        var acceleration = await CheckAccelerationAsync();
        if (acceleration.Status != "Ready")
            throw new InvalidOperationException("Windows virtualization is needed to start this device. Open Setup to check it.");

        launchFailures.Remove(device.Name);
        var start = StartInfo(Emulator, ["-avd", device.Name]);
        var completion = ProcessRunner.RunAsync(start, Timeout.InfiniteTimeSpan);
        launches[device.Name] = new(completion, DateTimeOffset.UtcNow.AddMinutes(3), false);
        if (await Task.WhenAny(completion, Task.Delay(750)) == completion)
        {
            var result = await completion;
            if (!result.Succeeded)
            {
                launches.Remove(device.Name);
                launchFailures[device.Name] = DescribeLaunchFailure(result);
                throw new InvalidOperationException(launchFailures[device.Name]);
            }
        }
    }

    private static string DescribeLaunchFailure(CommandResult result)
    {
        var lines = Lines(result.Error).Concat(Lines(result.Output)).ToArray();
        var detail = lines.LastOrDefault(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault();
        return string.IsNullOrWhiteSpace(detail)
            ? "The emulator closed before Android started. Open Setup to check this PC."
            : $"Android couldn’t start. {detail}";
    }

    public async Task StopDeviceAsync(AndroidDevice device)
    {
        var serial = await VerifyTargetAsync(device);
        var result = await RunToolAsync(Adb, ["-s", serial, "emu", "kill"]);
        EnsureConsoleSuccess(result, $"Stopping {device.Name}", "OK: killing emulator, bye bye");
        launchFailures.Remove(device.Name);
    }

    public async Task InstallAsync(AndroidDevice device, string apk)
    {
        if (!Path.IsPathFullyQualified(apk) || !File.Exists(apk)
            || !Path.GetExtension(apk).Equals(".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing .apk file.");

        var serial = await VerifyTargetAsync(device);
        var result = await RunToolAsync(Adb, ["-s", serial, "install", "-r", apk],
            TimeSpan.FromMinutes(3));
        result.EnsureSuccess("Installing the APK");
        if (!Lines(result.Output).Contains("Success", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("ADB did not confirm that the APK was installed.");
    }

    public async Task<string> ReadLogsAsync(AndroidDevice device)
    {
        var serial = await VerifyTargetAsync(device);
        var result = await RunToolAsync(Adb, ["-s", serial, "logcat", "-d", "-t", "250", "-v", "threadtime"]);
        result.EnsureSuccess("Reading device logs");
        return result.Output;
    }

    public async Task ApplyLocationAsync(AndroidDevice device, LocationProfile location)
    {
        if (!double.IsFinite(location.Latitude) || Math.Abs(location.Latitude) > 90
            || !double.IsFinite(location.Longitude) || Math.Abs(location.Longitude) > 180)
            throw new InvalidOperationException("Enter valid latitude and longitude values.");

        var serial = await VerifyTargetAsync(device);
        var result = await RunToolAsync(Adb, ["-s", serial, "emu", "geo", "fix",
            location.Longitude.ToString("G17", CultureInfo.InvariantCulture),
            location.Latitude.ToString("G17", CultureInfo.InvariantCulture)]);
        EnsureConsoleSuccess(result, "Setting the test location");
    }

    public async Task<IReadOnlyList<Diagnostic>> DiagnoseAsync()
    {
        var manager = FindCommandLineTools();
        var images = ListSystemImages();
        var java = await FindJavaHomeAsync();
        var results = new List<Diagnostic>
        {
            Check("SDK folder", Directory.Exists(SdkDirectory), SdkDirectory,
                "Use Set up Android to install the required files."),
            Check("Android Emulator", File.Exists(Emulator), Emulator, "Use Set up Android to install the emulator."),
            Check("Platform Tools / ADB", File.Exists(Adb), Adb, "Use Set up Android to install the connection tools."),
            Check("Command-line Tools", manager is not null, manager ?? "", "Use Set up Android to install the Android tools."),
            Check("Java", java is not null, java ?? "", "Use Set up Android to install Java automatically."),
            Check("System images", images.Count > 0, $"{images.Count} image(s) installed.",
                "Use Set up Android to download an Android system.")
        };

        var root = Path.GetPathRoot(SdkDirectory);
        if (root is not null && Path.IsPathFullyQualified(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
                results.Add(new("SDK drive space", "Info", $"{drive.AvailableFreeSpace / (1024d * 1024 * 1024):0.0} GB free on {drive.Name}"));
        }

        if (HasEmulator)
            results.Add(await CheckAccelerationAsync());

        return results;
    }

    public async Task<Diagnostic> CheckAccelerationAsync()
    {
        if (!HasEmulator)
            return new("Virtualization", "Check", "Finish Android setup first.");
        try
        {
            var acceleration = await RunToolAsync(Emulator, ["-accel-check"]);
            var lines = Lines(acceleration.Output);
            var checkHeader = Array.FindIndex(lines, line => line == "accel:");
            var code = checkHeader >= 0 && checkHeader + 1 < lines.Length ? lines[checkHeader + 1] : null;
            var ready = acceleration.Succeeded && (code is null || code == "0");
            var description = string.Join(" ", lines.Where(line => line != "accel:" &&
                line != code && line != "accel").Concat(Lines(acceleration.Error)));
            return Check("Virtualization", ready,
                description.Length > 0 ? description : "Hardware acceleration is ready.",
                description.Length > 0 ? description : "Windows could not start hardware acceleration.");
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception)
        {
            return new("Virtualization", "Check", ex.Message);
        }
    }

    private async Task<string> VerifyTargetAsync(AndroidDevice device)
    {
        if (device.Serial is null || !EmulatorSerial().IsMatch(device.Serial)
            || await GetAvdNameAsync(device.Serial) != device.Name)
            throw new InvalidOperationException("This emulator is no longer connected. Refresh the device list.");

        return device.Serial;
    }

    private async Task<string?> GetAvdNameAsync(string serial)
    {
        var result = await RunToolAsync(Adb, ["-s", serial, "emu", "avd", "name"], TimeSpan.FromSeconds(3));
        var lines = Lines(result.Output);
        return result.Succeeded && lines.Length == 2 && lines[1] == "OK" ? lines[0] : null;
    }

    public string? FindCommandLineTools()
    {
        var root = Path.Combine(SdkDirectory, "cmdline-tools");
        if (!Directory.Exists(root))
            return null;

        var managed = Path.Combine(root, AndroidSetup.CommandLineToolsVersion);
        var latest = Path.Combine(root, "latest");
        return IsCommandLineFolder(managed) ? managed : IsCommandLineFolder(latest) ? latest :
            Directory.EnumerateDirectories(root)
                .Where(path => Version.TryParse(Path.GetFileName(path), out _))
                .OrderByDescending(path => Version.Parse(Path.GetFileName(path)))
                .FirstOrDefault(IsCommandLineFolder);
    }

    internal static bool IsCommandLineFolder(string path) =>
        File.Exists(Path.Combine(path, "bin", "avdmanager.bat"))
        && File.Exists(Path.Combine(path, "bin", "sdkmanager.bat"));

    public async Task<string?> FindJavaHomeAsync(CancellationToken cancellationToken = default)
    {
        var studio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Android", "Android Studio", "jbr");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var homes = new[] { ManagedJavaHome, Environment.GetEnvironmentVariable("JAVA_HOME"), studio }
            .Concat(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(bin => Path.GetDirectoryName(bin.Trim('"').TrimEnd(Path.DirectorySeparatorChar))))
            .Where(home => !string.IsNullOrWhiteSpace(home) && Path.IsPathFullyQualified(home))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var home in homes)
        {
            if (!AndroidSetup.IsJavaFolder(home!))
                continue;
            try
            {
                var start = new ProcessStartInfo(Path.Combine(home!, "bin", "java.exe"));
                start.ArgumentList.Add("-version");
                var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(5),
                    cancellationToken: cancellationToken);
                var match = JavaVersion().Match(result.Detail);
                if (result.Succeeded && match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= 21)
                    return home;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or TimeoutException)
            {
                // A missing or incompatible install can be repaired with Device Lab’s Java runtime.
            }
        }
        return null;
    }

    internal ProcessStartInfo CreateBatchStartInfo(string tool, string javaHome, string arguments)
    {
        // Only fixed arguments and allow-listed AVD names/package IDs are passed by callers.
        // Supply paths through quoted environment expansions for cmd.exe's batch-file launcher.
        var start = StartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), []);
        start.Environment["DEVICELAB_ANDROID_TOOL"] = tool;
        start.Environment["JAVA_HOME"] = javaHome;
        start.Arguments = $"/d /v:off /s /c \"\"%DEVICELAB_ANDROID_TOOL%\" {arguments}\"";
        return start;
    }

    private Task<CommandResult> RunToolAsync(string tool, string[] args, TimeSpan? timeout = null)
    {
        EnsureFile(tool, Path.GetFileName(tool));
        return ProcessRunner.RunAsync(StartInfo(tool, args), timeout ?? StandardTimeout);
    }

    private ProcessStartInfo StartInfo(string tool, string[] args)
    {
        var start = new ProcessStartInfo(tool);
        start.Environment["ANDROID_HOME"] = SdkDirectory;
        start.Environment["ANDROID_SDK_ROOT"] = SdkDirectory;
        foreach (var argument in args)
            start.ArgumentList.Add(argument);

        return start;
    }

    private static void EnsureFile(string path, string tool)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{tool} was not found. Check the Android SDK setup.");
    }

    private static void EnsureConsoleSuccess(CommandResult result, string operation, string expected = "OK")
    {
        result.EnsureSuccess(operation);
        if (!Lines(result.Output).Contains(expected, StringComparer.Ordinal))
            throw new InvalidOperationException($"{operation} could not be confirmed. {result.Detail}".Trim());
    }

    private static Diagnostic Check(string name, bool ready, string success, string failure) =>
        new(name, ready ? "Ready" : "Check", ready ? success : failure);

    private static string[] Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ImageDisplayName(string api, string tag, string architecture) =>
        (api, tag, architecture) == ("android-35", "google_apis", "x86_64")
            ? "Android 15 (recommended)"
            : $"{api}  /  {tag}  /  {architecture}";

    [GeneratedRegex("""\b(?:openjdk|java) version "(\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex JavaVersion();

    private static int ImageApiLevel(string package)
    {
        var androidVersion = package.Split(';')[1];
        return int.TryParse(androidVersion.Split('-').Last(), out var api) ? api : 0;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();

    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    [GeneratedRegex(@"\Asystem-images;android-[a-zA-Z0-9_.-]+;[a-zA-Z0-9_.-]+;[a-zA-Z0-9_.-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePackage();

    [GeneratedRegex(@"\Aemulator-[0-9]{1,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmulatorSerial();
}
