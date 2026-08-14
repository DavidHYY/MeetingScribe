using System.Text;

namespace MeetingScribe.App.Services;

/// <summary>
/// Shells out to the Codex CLI (<c>codex exec</c>). The full prompt (rendered template +
/// transcript, wrapped in a <c>&lt;transcript&gt;</c> block) is sent entirely over stdin, with
/// <b>no</b> prompt passed as a command-line argument - this is deliberate, not the first design
/// tried. codex exec's own doc for a bare invocation confirms it: "If not provided as an argument
/// ..., instructions are read from stdin." An earlier version instead passed the rendered prompt
/// as an argv entry (mirroring <see cref="ClaudeMinutesProvider"/>) and piped only the transcript
/// - that reliably failed on Windows, reproduced directly: <c>codex</c> is npm-installed and
/// ships as a <c>codex.cmd</c> batch shim (verified: <c>where codex</c> shows both a bare,
/// extension-less POSIX shim and <c>codex.cmd</c>; no <c>.exe</c> at all), and .NET's
/// <c>Process.Start</c> transparently routes a <c>.cmd</c> <c>FileName</c> through
/// <c>cmd.exe /c</c>. cmd.exe's command-line parser is line-based and cannot carry an embedded
/// newline inside a quoted argument (confirmed: passing the real, multi-line default minutes
/// prompt as an argv entry produced <c>cmd.exe</c> error ". was unexpected at this time." and
/// exit code 255, closing codex's stdin before the transcript write finished, surfacing as
/// "The pipe has been ended" on our side) - and the shipped default prompt template
/// (<see cref="MeetingScribe.App.Models.AppSettings.DefaultMinutesPromptTemplate"/>) is
/// multi-line, so that failure was not an edge case, it was the common case. Putting everything
/// on stdin instead sidesteps command-line parsing entirely, matching
/// <see cref="OllamaMinutesProvider"/>'s approach. <see cref="ClaudeMinutesProvider"/> is
/// unaffected by this - <c>claude</c> is an actual <c>.exe</c> on this machine, so
/// <c>Process.Start</c> launches it directly with no <c>cmd.exe</c> indirection.
///
/// The clean final agent message is captured via <c>-o &lt;file&gt;</c> rather than parsed out of
/// stdout/stderr, because those streams also carry a human-readable session banner and
/// transcript (verified: banner/transcript on stderr, a bare copy of the final message on
/// stdout, real error text on stderr on failure) that would need fragile parsing otherwise.
/// <c>-s read-only</c> and <c>--skip-git-repo-check</c> keep this a pure text-generation call: no
/// shell command execution capability is needed, and the working directory does not need to be a
/// git repository.
/// </summary>
public sealed class CodexMinutesProvider : IMinutesProvider
{
    private const string Exe = "codex";

    public MinutesProviderKind Kind => MinutesProviderKind.Codex;

    public string DisplayName => "Codex (codex exec)";

    public Task<MinutesProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        CliAvailabilityChecker.CheckAsync(Exe, ["--version"], TimeSpan.FromSeconds(10), cancellationToken);

    public async Task<string> GenerateAsync(
        string transcriptText,
        string promptTemplate,
        string meetingTitle,
        string meetingDate,
        TimeSpan timeout,
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcriptText);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptTemplate);

        var instructions = MinutesTextUtils.RenderPrompt(promptTemplate, meetingTitle, meetingDate);
        var stdinPayload = $"{instructions}\n\n<transcript>\n{transcriptText}\n</transcript>\n";
        var outputFile = Path.Combine(Path.GetTempPath(), $"meetingscribe-codex-{Guid.NewGuid():N}.md");

        try
        {
            var args = new List<string>
            {
                "exec",
                "--skip-git-repo-check",
                "-s", "read-only",
                "--color", "never",
                "-o", outputFile,
                // No positional prompt argument - see class doc comment for why.
            };

            var result = await CliProcessRunner.RunAsync(
                Exe,
                args,
                workingDirectory: Path.GetTempPath(),
                stdinPayload,
                timeout,
                progress,
                cancellationToken,
                notFoundHint: "Install the Codex CLI and run 'codex login' first.").ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                var trimmedStderr = result.Stderr.Trim();
                throw new MinutesGenerationException(
                    $"codex exec exited with code {result.ExitCode}: {(trimmedStderr.Length == 0 ? "(no stderr)" : trimmedStderr)}");
            }

            if (!File.Exists(outputFile))
            {
                throw new MinutesGenerationException("codex exec exited 0 but produced no output file.");
            }

            var minutesText = (await File.ReadAllTextAsync(outputFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false)).Trim();
            if (minutesText.Length == 0)
            {
                throw new MinutesGenerationException("codex exec produced no output.");
            }

            return MinutesTextUtils.StripCodeFence(minutesText);
        }
        finally
        {
            try
            {
                if (File.Exists(outputFile))
                {
                    File.Delete(outputFile);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp -o file; a leftover file in %TEMP% is
                // harmless and not worth failing minutes generation over.
            }
            catch (UnauthorizedAccessException)
            {
                // Same rationale as above.
            }
        }
    }
}
