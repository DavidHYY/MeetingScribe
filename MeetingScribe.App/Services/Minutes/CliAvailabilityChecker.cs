using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MeetingScribe.App.Services;

/// <summary>Runs "<c>{exe} {versionArguments}</c>" with a short timeout to check a CLI is on PATH and actually runs.</summary>
internal static class CliAvailabilityChecker
{
    public static async Task<MinutesProviderAvailability> CheckAsync(
        string fileName,
        IReadOnlyList<string> versionArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            // See CliExecutableResolver's doc comment: CreateProcess does not do the PATHEXT
            // search a shell does, so a bare "codex" fails here even though it is genuinely on
            // PATH (npm-shimmed as codex.cmd on Windows) - resolve the real path ourselves first.
            FileName = CliExecutableResolver.Resolve(fileName),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in versionArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new MinutesProviderAvailability(false, $"'{fileName}' was not found on PATH.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ProcessKillHelper.TryKill(process);
            return new MinutesProviderAvailability(
                false, $"'{fileName}' did not respond to '{string.Join(' ', versionArguments)}' within {timeout.TotalSeconds:F0}s.");
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr : (stdout.Length > 0 ? stdout : "(no output)");
            return new MinutesProviderAvailability(false, $"'{fileName}' is on PATH but exited {process.ExitCode}: {detail}");
        }

        var version = stdout.Length > 0 ? stdout : stderr;
        return new MinutesProviderAvailability(true, version.Length > 0 ? $"Found: {version}" : "Found on PATH.");
    }
}
