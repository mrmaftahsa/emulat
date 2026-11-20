using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DeviceLab;

internal sealed record SetupPlan(string? JavaHome, bool NeedsCommandLineTools, string[] Packages)
{
    public bool NeedsAndroid => NeedsCommandLineTools || Packages.Length > 0;
    public bool IsReady => JavaHome is not null && !NeedsAndroid;
}

internal sealed record SetupProgress(string Message, int? Percent = null);

internal sealed partial class AndroidSetup(AndroidTools tools)
{
    private const string GoogleRepository = "https://dl.google.com/android/repository/";
    private const string LicenseId = "android-sdk-license";
    internal const string CommandLineToolsVersion = "23.0";
    internal const string RecommendedSystem = "system-images;android-35;google_apis;x86_64";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };

    private static readonly Uri JavaArchive = new(
        "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.13.1%2B2/OpenJDK21U-jre_x64_windows_hotspot_21.0.13.1_2.zip");
    private const string JavaArchiveSha256 = "e15ee1e54a5a3b38027a09cedfa87bc3348d7dd3ce50866dd3d9671d7fffc6c1";
    private static readonly Uri CommandLineZip = new(GoogleRepository + "commandlinetools-win-16111833_latest.zip");
    private const string CommandLineZipSha1 = "57d04f2d75eb8e8fffc5000a987e5de4b5a63e9d";

    public async Task<SetupPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        var packages = new List<string>();
        if (!tools.HasAdb)
            packages.Add("platform-tools");
        if (!tools.HasEmulator)
            packages.Add("emulator");
        if (!tools.ListSystemImages().Any(image => image.Package.EndsWith(";x86_64", StringComparison.Ordinal)))
            packages.Add(RecommendedSystem);

        var java = await tools.FindJavaHomeAsync(cancellationToken);
        return new(java, tools.FindCommandLineTools() is null, packages.ToArray());
    }

    public async Task<string> ReadLicenseAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(GoogleRepository + "repository2-3.xml",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = XmlReader.Create(content, new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 12_000_000
        });
        var repository = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
        var license = repository.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "license"
            && (string?)e.Attribute("id") == LicenseId)?.Value;
        return string.IsNullOrWhiteSpace(license)
            ? throw new InvalidOperationException("Google’s Android SDK terms could not be loaded. Please try again.")
            : license;
    }

    public async Task InstallAsync(SetupPlan plan, string? acceptedLicense, IProgress<SetupProgress> progress,
        CancellationToken cancellationToken)
    {
        if (plan.NeedsAndroid && string.IsNullOrWhiteSpace(acceptedLicense))
            throw new InvalidOperationException("Review and accept Google’s Android SDK terms to continue.");

        // Temp files and replacements share a drive with their destination for atomic moves.
        Directory.CreateDirectory(tools.RuntimeDirectory);
        using var setupLock = OpenSetupLock();
        var javaHome = plan.JavaHome;
        if (javaHome is null)
        {
            CheckDiskSpace(tools.RuntimeDirectory, 280 * 1024 * 1024L);
            await InstallArchiveAsync(JavaArchive, JavaArchiveSha256, tools.ManagedJavaHome,
                "Downloading Java", GetJavaRoot, IsJavaFolder, progress, cancellationToken);
            javaHome = await tools.FindJavaHomeAsync(cancellationToken)
                ?? throw new InvalidOperationException("The downloaded Java runtime could not start.");
        }

        if (!plan.NeedsAndroid)
            return;

        Directory.CreateDirectory(tools.SdkDirectory);
        var requiredBytes = plan.Packages.Contains(RecommendedSystem, StringComparer.Ordinal)
            ? 9L * 1024 * 1024 * 1024
            : 2L * 1024 * 1024 * 1024;
        CheckDiskSpace(tools.SdkDirectory, requiredBytes);
        await SaveAcceptedLicenseAsync(acceptedLicense!, cancellationToken);

        if (plan.NeedsCommandLineTools && tools.FindCommandLineTools() is null)
        {
            var commandLineFolder = Path.Combine(tools.SdkDirectory, "cmdline-tools", CommandLineToolsVersion);
            await InstallArchiveAsync(CommandLineZip, CommandLineZipSha1, commandLineFolder,
                "Downloading Android tools", root => Path.Combine(root, "cmdline-tools"),
                AndroidTools.IsCommandLineFolder, progress, cancellationToken);
        }

        if (plan.Packages.Length > 0)
        {
            var manager = tools.FindCommandLineTools()
                ?? throw new InvalidOperationException("The Android tools could not be installed. Try setup again.");
            var executable = Path.Combine(manager, "bin", "sdkmanager.bat");
            // The only package arguments come from PlanAsync and are fixed Android package IDs.
            var packages = string.Join(" ", plan.Packages.Select(package => $"\"{package}\""));
            var start = tools.CreateBatchStartInfo(executable, javaHome,
                $"--sdk_root=\"%ANDROID_HOME%\" --channel=0 --install {packages}");
            progress.Report(new("Downloading and installing Android. This may take several minutes."));
            int lastPercent = -1;
            var result = await ProcessRunner.RunAsync(start, TimeSpan.FromHours(2), "n", cancellationToken,
                line =>
                {
                    var match = SdkProgress().Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
                            out var percent) && percent != lastPercent && percent <= 100)
                    {
                        lastPercent = percent;
                        progress.Report(new("Downloading and installing Android…", percent));
                    }
                });

            if (result.Detail.Contains("Accept? (y/N)", StringComparison.OrdinalIgnoreCase)
                || result.Detail.Contains("license agreements not accepted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Android setup needs updated Google terms. Close this window and reopen setup to review them.");

            result.EnsureSuccess("Installing the Android components");
        }

        if (!(await PlanAsync(cancellationToken)).IsReady)
            throw new InvalidOperationException("Some Android components are still missing. Run setup again to finish downloading them.");
    }

    public static async Task EnableWindowsVirtualizationAsync()
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "dism.exe"),
            Arguments = "/Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart /Quiet",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows could not open the virtualization setup.");
            await process.WaitForExitAsync();
            if (process.ExitCode is not (0 or 3010))
                throw new InvalidOperationException($"Windows could not enable virtualization (code {process.ExitCode}).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The Windows approval was canceled. You can try again.", ex);
        }
    }

    private FileStream OpenSetupLock()
    {
        try
        {
            return new(Path.Combine(tools.RuntimeDirectory, "android-setup.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Android setup is already running. Finish it, then try again.", ex);
        }
    }

    private async Task SaveAcceptedLicenseAsync(string license, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(tools.SdkDirectory, "licenses");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, LicenseId);
        // sdkmanager stores SHA-1 hashes of the exact XML license text that the user accepted.
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(license)));
        if (!File.Exists(path) || !(await File.ReadAllLinesAsync(path, cancellationToken)).Contains(hash))
            await File.AppendAllTextAsync(path, Environment.NewLine + hash + Environment.NewLine, cancellationToken);
    }

    private static void CheckDiskSpace(string path, long minimum)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal))
            return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < minimum)
            throw new IOException($"There is too little space on {drive.Name} to finish setup. Free some disk space and try again.");
    }

    private static async Task InstallArchiveAsync(Uri uri, string checksum, string destination, string downloadMessage,
        Func<string, string> getContents, Func<string, bool> isValidFolder, IProgress<SetupProgress> progress,
        CancellationToken cancellationToken)
    {
        if (isValidFolder(destination))
            return;
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".device-lab-" + Path.GetRandomFileName());
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, "download.zip");
            await DownloadAsync(uri, checksum, archive, downloadMessage, progress, cancellationToken);
            progress.Report(new("Unpacking files…"));
            var unpacked = Path.Combine(stage, "unpacked");
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, unpacked), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var contents = getContents(unpacked);
            if (!isValidFolder(contents))
                throw new InvalidOperationException("The downloaded archive is missing required files.");

            // Preserve any incomplete existing folder so other tools and the next retry stay recoverable.
            if (Directory.Exists(destination))
                Directory.Move(destination, destination + ".backup-" + Path.GetRandomFileName());
            Directory.Move(contents, destination);
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the original installation error visible if cleanup fails.
            }
        }
    }

    private static async Task DownloadAsync(Uri uri, string checksum, string destination, string message,
        IProgress<SetupProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new(message + "…", 0));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(checksum.Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        var length = response.Content.Headers.ContentLength;
        var buffer = new byte[65536];
        long received = 0;
        var lastPercent = 0;
        while ((await content.ReadAsync(buffer, deadline.Token)) is var read && read > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            hash.AppendData(buffer, 0, read);
            received += read;
            if (length is > 0)
            {
                var percent = (int)(received * 100 / length.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(new(message + "…", percent));
                }
            }
        }

        if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(checksum, StringComparison.Ordinal))
            throw new InvalidOperationException("A download failed its integrity check. Try setup again.");
    }

    private static string GetJavaRoot(string unpacked) =>
        Directory.EnumerateDirectories(unpacked).SingleOrDefault(IsJavaFolder)
        ?? throw new InvalidOperationException("The Java archive did not contain a Windows runtime.");

    internal static bool IsJavaFolder(string javaHome) => File.Exists(Path.Combine(javaHome, "bin", "java.exe"));

    [GeneratedRegex(@"^\s*(?:\[[= >-]*\]\s*)?(\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex SdkProgress();
}
