using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace DeviceLab;

internal static class MEmuInstaller
{
    // The offline installer published on MEmu's official download page.
    private const long DownloadBytes = 770455296;
    private static readonly Uri Package = new(
        "https://dl.memuplay.net/download/MEmu-Setup-9.5.9.0-ha35dc62d0.exe");
    private const string PackageSha256 = "3071ede03b7914e7ae31044d9bc12fef2d8d4f59a9cbe95692f342435e2a6e4f";

    public static Task DownloadAsync(string destination, IProgress<SetupProgress> progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetPathRoot(destination);
        if (root is not null && !root.StartsWith(@"\\", StringComparison.Ordinal)
            && new DriveInfo(root) is { IsReady: true } drive && drive.AvailableFreeSpace < DownloadBytes)
            throw new IOException("Free at least 770 MB on the Windows drive, then try again.");

        return VerifiedDownload.DownloadAsync(Package, PackageSha256, destination, "Downloading MEmu",
            progress, cancellationToken, DownloadBytes);
    }

    public static async Task<int> RunAsync(string path)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows couldn’t open MEmu Setup.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The Windows approval was canceled. Click Install MEmu to try again.", ex);
        }
    }
}
