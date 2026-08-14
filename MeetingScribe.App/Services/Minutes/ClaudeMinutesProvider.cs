namespace MeetingScribe.App.Services;

/// <summary>
/// Shells out to the Claude Code CLI (<c>claude -p</c>) - the original, still-default minutes
/// backend. No API key handling: this calls the already-authenticated <c>claude</c> CLI exactly
/// like a user would from a terminal. Unchanged behaviour from the pre-provider-interface
/// implementation; only moved behind <see cref="IMinutesProvider"/>.
/// </summary>
public sealed class ClaudeMinutesProvider : IMinutesProvider
{
    private const string Exe = "claude";

    public MinutesProviderKind Kind => MinutesProviderKind.Claude;

    public string DisplayName => "Claude (claude -p)";

    public Task<MinutesProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        CliAvailabilityChecker.CheckAsync(Exe, ["--version"], TimeSpan.FromSeconds(10), cancellationToken);

    /// <summary>
    /// Runs <c>claude -p "&lt;rendered prompt&gt;"</c> with <paramref name="transcriptText"/>
    /// piped to stdin (a full-meeting transcript can be tens of kilobytes; piping avoids
    /// Windows' ~32767-character command-line limit that an argument would hit).
    /// </summary>
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

        var prompt = MinutesTextUtils.RenderPrompt(promptTemplate, meetingTitle, meetingDate);

        var result = await CliProcessRunner.RunAsync(
            Exe,
            ["-p", prompt],
            workingDirectory: null,
            transcriptText,
            timeout,
            progress,
            cancellationToken,
            notFoundHint: "Install/authenticate the Claude Code CLI first.").ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            var trimmedStderr = result.Stderr.Trim();
            throw new MinutesGenerationException(
                $"claude -p exited with code {result.ExitCode}: {(trimmedStderr.Length == 0 ? "(no stderr)" : trimmedStderr)}");
        }

        var minutesText = result.Stdout.Trim();
        if (minutesText.Length == 0)
        {
            throw new MinutesGenerationException("claude -p produced no output.");
        }

        return MinutesTextUtils.StripCodeFence(minutesText);
    }
}
