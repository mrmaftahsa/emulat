using System.Diagnostics;
using System.IO;

namespace DeviceLab;

internal sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    public string Detail => string.Join(Environment.NewLine,
        new[] { Output.Trim(), Error.Trim() }.Where(line => line.Length > 0));

    public void EnsureSuccess(string operation)
    {
        if (Succeeded)
            return;

        var detail = Detail;
        if (detail.Length > 750)
            detail = detail[..750] + "…";

        throw new InvalidOperationException($"{operation} failed (exit code {ExitCode}). {detail}".Trim());
    }
}

internal static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        ProcessStartInfo start, TimeSpan timeout, string? input = null)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = input is not null;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(start.FileName)}.");
        using var deadline = new CancellationTokenSource(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteLineAsync(input.AsMemory(), deadline.Token)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
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
            }
            catch (InvalidOperationException)
            {
                // The process may exit just before Kill is called.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Preserve the timeout when the process can no longer be accessed.
            }

            throw new TimeoutException($"{Path.GetFileName(start.FileName)} did not respond in time.");
        }
    }
}
