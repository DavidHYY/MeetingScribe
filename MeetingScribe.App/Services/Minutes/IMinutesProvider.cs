namespace MeetingScribe.App.Services;

/// <summary>
/// Result of <see cref="IMinutesProvider.CheckAvailabilityAsync"/>: whether the backend is
/// actually usable right now, and a human-readable reason either way (found version / not found
/// on PATH / endpoint unreachable / model not pulled, etc.) - surfaced directly in Settings so a
/// user sees *why* a provider is greyed out instead of just that it is.
/// </summary>
public sealed record MinutesProviderAvailability(bool IsAvailable, string Reason);

/// <summary>
/// One pluggable backend that turns a transcript into meeting minutes Markdown. Three
/// implementations exist: <c>ClaudeMinutesProvider</c> (<c>claude -p</c>, the original
/// behaviour), <c>CodexMinutesProvider</c> (<c>codex exec</c>), and <c>OllamaMinutesProvider</c>
/// (local HTTP model server, no subscription required). All three consume the same prompt
/// template and share fence-stripping/prompt-rendering via <see cref="MinutesTextUtils"/> so a
/// fix in one place benefits every backend.
/// </summary>
public interface IMinutesProvider
{
    MinutesProviderKind Kind { get; }

    /// <summary>Short label for UI (ComboBox items, status text).</summary>
    string DisplayName { get; }

    /// <summary>
    /// Cheap, fast, never-throwing check of whether this provider is usable right now (CLI on
    /// PATH and runs, HTTP endpoint reachable and has the configured model, etc.). Called when
    /// Settings opens so an unusable provider can be greyed out with a reason instead of being
    /// offered and failing only when clicked.
    /// </summary>
    Task<MinutesProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates minutes Markdown (fence already stripped) from <paramref name="transcriptText"/>
    /// using <paramref name="promptTemplate"/> rendered with <paramref name="meetingTitle"/>/
    /// <paramref name="meetingDate"/>. <paramref name="progress"/>, if given, is reported the
    /// elapsed wall-clock time roughly every 2 seconds, so a UI can show "still running" instead
    /// of looking hung on a slow local model. Throws <see cref="MinutesGenerationException"/> on
    /// any failure (backend missing, unreachable, timed out, or errored).
    /// </summary>
    Task<string> GenerateAsync(
        string transcriptText,
        string promptTemplate,
        string meetingTitle,
        string meetingDate,
        TimeSpan timeout,
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken = default);
}
