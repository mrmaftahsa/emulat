using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DeviceLab;

internal sealed record AndroidDevice(string Name, string? Serial, string Status)
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
    private readonly Dictionary<string, DateTimeOffset> startingDevices = new(StringComparer.Ordinal);

    public string SdkDirectory { get; }
    public bool HasEmulator => File.Exists(Emulator);
    public bool HasAdb => File.Exists(Adb);
    private string Emulator => Path.Combine(SdkDirectory, "emulator", "emulator.exe");
    private string Adb => Path.Combine(SdkDirectory, "platform-tools", "adb.exe");

    public AndroidTools(string sdkDirectory)
    {
        SdkDirectory = Path.GetFullPath(sdkDirectory);
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

        foreach (var (name, expiry) in startingDevices.ToArray())
        {
            if (expiry <= DateTimeOffset.UtcNow || (running.TryGetValue(name, out var device) && device.IsOnline))
                startingDevices.Remove(name);
        }
        return names.Select(name => running.TryGetValue(name, out var device) ? device
            : new AndroidDevice(name, null, startingDevices.ContainsKey(name) ? Busy : Offline)).ToArray();
    }

    public IReadOnlyList<SystemImage> ListSystemImages()
    {
        var directory = Path.Combine(SdkDirectory, "system-images");
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "source.properties", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SdkDirectory, Path.GetDirectoryName(path)!))
            .Select(relative => relative.Split(Path.DirectorySeparatorChar))
            .Where(parts => parts.Length == 4)
            .Select(parts => new SystemImage(string.Join(';', parts),
                $"{parts[1]}  /  {parts[2]}  /  {parts[3]}"))
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

        var manager = FindAvdManager()
            ?? throw new InvalidOperationException("Install Android SDK Command-line Tools in SDK Manager.");
        var start = CreateAvdManagerStartInfo(manager, name, image.Package);
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

        var start = StartInfo(Emulator, ["-avd", device.Name]);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not launch {device.Name}.");

        await Task.Delay(700);
        if (process.HasExited && process.ExitCode != 0)
            throw new InvalidOperationException(
                $"The emulator exited with code {process.ExitCode}. Run diagnostics and check the device in Android Studio.");

        // A successful launcher can exit after handing off to the emulator UI.
        startingDevices[device.Name] = DateTimeOffset.UtcNow.AddSeconds(90);
    }

    public async Task StopDeviceAsync(AndroidDevice device)
    {
        var serial = await VerifyTargetAsync(device);
        var result = await RunToolAsync(Adb, ["-s", serial, "emu", "kill"]);
        EnsureConsoleSuccess(result, $"Stopping {device.Name}", "OK: killing emulator, bye bye");
        startingDevices.Remove(device.Name);
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
        var manager = FindAvdManager();
        var images = ListSystemImages();
        var java = FindJava();
        var results = new List<Diagnostic>
        {
            Check("SDK folder", Directory.Exists(SdkDirectory), SdkDirectory,
                "Choose the Android SDK folder or install the SDK with Android Studio."),
            Check("Android Emulator", File.Exists(Emulator), Emulator, "Install Android Emulator in SDK Manager."),
            Check("Platform Tools / ADB", File.Exists(Adb), Adb, "Install Android SDK Platform-Tools."),
            Check("Command-line Tools", manager is not null, manager ?? "", "Install Android SDK Command-line Tools."),
            Check("Java", java is not null, java ?? "", "Set JAVA_HOME to a JDK or install Android Studio."),
            Check("System images", images.Count > 0, $"{images.Count} image(s) installed.",
                "Install an Android system image in SDK Manager.")
        };

        var root = Path.GetPathRoot(SdkDirectory);
        if (root is not null && Path.IsPathFullyQualified(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
                results.Add(new("SDK drive space", "Info", $"{drive.AvailableFreeSpace / (1024d * 1024 * 1024):0.0} GB free on {drive.Name}"));
        }

        if (File.Exists(Emulator))
        {
            try
            {
                var acceleration = await RunToolAsync(Emulator, ["-accel-check"]);
                var lines = Lines(acceleration.Output);
                var checkHeader = Array.FindIndex(lines, line => line == "accel:");
                var code = checkHeader >= 0 && checkHeader + 1 < lines.Length ? lines[checkHeader + 1] : null;
                var ready = acceleration.Succeeded && (code is null || code == "0");
                var description = string.Join(" ", lines.Where(line => line != "accel:" &&
                    line != code && line != "accel").Concat(Lines(acceleration.Error)));
                results.Add(Check("Virtualization", ready,
                    description.Length > 0 ? description : "The emulator supports hardware acceleration.",
                    description.Length > 0 ? description : "Check Windows Hypervisor Platform and firmware virtualization."));
            }
            catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception)
            {
                results.Add(new("Virtualization", "Check", ex.Message));
            }
        }

        return results;
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

    private string? FindAvdManager()
    {
        var root = Path.Combine(SdkDirectory, "cmdline-tools");
        if (!Directory.Exists(root))
            return null;

        var latest = Path.Combine(root, "latest", "bin", "avdmanager.bat");
        return File.Exists(latest) ? latest : Directory.EnumerateDirectories(root)
            .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version())
            .Select(path => Path.Combine(path, "bin", "avdmanager.bat"))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindJava()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            return ExistingJava(Path.Combine(javaHome, "bin"));

        var studio = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Android", "Android Studio", "jbr", "bin");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return ExistingJava(studio) ?? path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => ExistingJava(directory.Trim('"')))
            .FirstOrDefault(java => java is not null);
    }

    private static string? ExistingJava(string directory)
    {
        var java = Path.Combine(directory, "java.exe");
        return Path.IsPathFullyQualified(java) && File.Exists(java) ? java : null;
    }

    private ProcessStartInfo CreateAvdManagerStartInfo(string manager, string name, string package)
    {
        // The .bat launcher requires cmd.exe. Both command operands have been allow-listed.
        // Give the launcher path to cmd as an environment value inside double quotes.
        var start = StartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), []);
        start.Environment["DEVICELAB_AVDMANAGER"] = manager;
        var java = FindJava();
        if (java is not null)
            start.Environment["JAVA_HOME"] = Path.GetDirectoryName(Path.GetDirectoryName(java)!)!;
        start.Arguments = $"/d /v:off /s /c \"\"%DEVICELAB_AVDMANAGER%\" create avd --name {name} --package \"{package}\"\"";
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
