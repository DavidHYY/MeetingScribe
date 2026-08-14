using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MeetingScribe.App.Services;

/// <summary>
/// Shared process-spawning core for the CLI-backed minutes providers (claude, codex): starts the
/// process with all-UTF-8 redirected streams, writes <c>stdinText</c> to stdin before closing it
/// (reading stdout/stderr concurrently first - if the child produces enough output to fill the OS
/// pipe buffer before this finishes writing and closing stdin, a synchronous write-then-read order
/// can deadlock; this order cannot), reports elapsed wall-clock time via
/// <see cref="ElapsedProgressReporter"/> so a slow backend does not look hung, and always kills
/// the child process on any cancellation (timeout or caller-requested) rather than leaking an
/// orphaned process.
/// </summary>
internal static class CliProcessRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        string stdinText,
        TimeSpan timeout,
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken,
        string notFoundHint)
    {
        var startInfo = new ProcessStartInfo
        {
            // Resolved to a full path (with extension) ourselves - see CliExecutableResolver's
            // doc comment for why: CreateProcess (what UseShellExecute=false uses) does not do
            // the PATHEXT search a shell does, which fails a bare "codex" on Windows (npm-shimmed
            // as codex.cmd) even though "codex" on PATH works from an interactive shell.
            FileName = CliExecutableResolver.Resolve(fileName),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Explicit UTF-8 on every redirected stream - never rely on the process's default
            // console codepage (cp950 on this machine), which is exactly the corruption mode
            // this project's UTF-8-everywhere rule exists to prevent.
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new MinutesGenerationException($"'{fileName}' was not found on PATH. {notFoundHint}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.StandardInput.WriteAsync(stdinText.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var progressReporter = ElapsedProgressReporter.Start(progress, ProgressInterval);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ProcessKillHelper.TryKill(process);
            throw new MinutesGenerationException($"'{fileName}' timed out after {timeout.TotalSeconds:F0}s generating minutes.");
        }
        catch (OperationCanceledException)
        {
            // Caller-requested cancellation (not a timeout): the child process must not be left
            // running detached just because we stopped waiting for it.
            ProcessKillHelper.TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new Result(process.ExitCode, stdout, stderr);
    }
}
