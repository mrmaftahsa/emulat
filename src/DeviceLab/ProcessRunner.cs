using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeviceLab;

internal sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Detail => string.Join(Environment.NewLine,
        new[] { Output.Trim(), Error.Trim() }.Where(line => line.Length > 0));

    public void EnsureSuccess(string operation)
    {
        if (!Succeeded)
            throw new InvalidOperationException($"{operation} failed (exit code {ExitCode}). {ShortDetail}".Trim());
    }

    public string ShortDetail
    {
        get
        {
            var detail = Detail;
            return detail.Length > 800 ? "…" + detail[^800..] : detail;
        }
    }
}

internal static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        ProcessStartInfo start, TimeSpan timeout, string? input = null,
        CancellationToken cancellationToken = default, Action<string>? onOutput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = input is not null;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(start.FileName)}.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stdout = ReadOutputAsync(process.StandardOutput, onOutput, deadline.Token);
        var stderr = ReadOutputAsync(process.StandardError, null, deadline.Token);

        try
        {
            if (input is not null)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync(input.AsMemory(), deadline.Token)
                        .ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // An early exit can close the pipe before reading input. Preserve its output.
                }
            }

            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr)
                .ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process may exit just before Kill is called.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Preserve the cancellation when the process can no longer be accessed.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} did not respond in time.");
        }
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        const int maxCharacters = 256 * 1024;
        var output = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            onOutput?.Invoke(line);
            output.AppendLine(line);
            if (output.Length > maxCharacters)
                output.Remove(0, output.Length - maxCharacters);
        }
        return output.ToString();
    }
}
