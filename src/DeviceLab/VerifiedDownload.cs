using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DeviceLab;

internal static class VerifiedDownload
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };

    public static async Task DownloadAsync(Uri uri, string checksum, string destination, string message,
        IProgress<SetupProgress> progress, CancellationToken cancellationToken, long? expectedLength = null)
    {
        progress.Report(new(message + "…", 0));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var hash = IncrementalHash.CreateHash(checksum.Length == 64 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        var advertisedLength = response.Content.Headers.ContentLength;
        if (expectedLength is not null && advertisedLength is not null && advertisedLength != expectedLength)
            throw new InvalidOperationException("The download size changed. Try setup again after updating Device Lab.");

        var length = expectedLength ?? advertisedLength;
        var buffer = new byte[65536];
        long received = 0;
        var lastPercent = 0;
        while ((await content.ReadAsync(buffer, deadline.Token)) is var read && read > 0)
        {
            received += read;
            if (expectedLength is { } limit && received > limit)
                throw new InvalidOperationException("The download was larger than expected.");
            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            hash.AppendData(buffer, 0, read);
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
}
