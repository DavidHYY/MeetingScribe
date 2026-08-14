using MeetingScribe.Audio;

namespace MeetingScribe.App.Models;

/// <summary>
/// One track-labeled line of transcript, either a rough live segment (stage 1) or a
/// final accurate segment (stage 2). Timestamps are elapsed-since-recording-start, not
/// wall-clock, so they line up with the elapsed timer shown in the UI.
/// </summary>
/// <remarks>
/// <see cref="Translation"/>/<see cref="TranslationStatus"/>/<see cref="TranslationError"/> are
/// live-pass-only (see <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>): the
/// stage-2 accurate pass never populates them, so a final-pass line always carries
/// <see cref="Models.TranslationStatus.NotApplicable"/> with a null <see cref="Translation"/>.
/// They are appended as optional properties (not primary-constructor parameters) specifically so
/// every existing positional <c>new TranscriptLine(...)</c> call site keeps compiling unchanged.
/// </remarks>
public sealed record TranscriptLine(
    AudioSourceKind Source,
    TimeSpan Start,
    TimeSpan End,
    string Text,
    string? Language,
    float Probability)
{
    /// <summary>
    /// Overrides <see cref="SourceLabel"/>'s Mic/System derivation from <see cref="Source"/>.
    /// Set only for lines built from an imported single-track recording (see
    /// <see cref="MeetingScribe.App.Services.MeetingSessionController.ImportAsync"/>), which has
    /// no Mic/System distinction to preserve - the underlying <see cref="Source"/> value there is
    /// an arbitrary placeholder (never shown), same optional-property pattern as
    /// <see cref="Translation"/> below so every existing positional <c>new TranscriptLine(...)</c>
    /// call site keeps compiling unchanged. Null (the default) for every recorded-meeting line.
    /// </summary>
    public string? SourceLabelOverride { get; init; }

    public string SourceLabel => SourceLabelOverride ?? (Source == AudioSourceKind.Microphone ? "Mic" : "System");

    /// <summary>
    /// Stable identity for this line within its owning <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>
    /// session, used to route an asynchronously-arriving translation back to the right displayed
    /// line. 0 for lines that never go through the live engine (e.g. the stage-2 accurate pass).
    /// </summary>
    public long Id { get; init; }

    /// <summary>English translation of <see cref="Text"/>, or null until one arrives (or never, if
    /// <see cref="TranslationStatus"/> stays <see cref="Models.TranslationStatus.NotApplicable"/>).</summary>
    public string? Translation { get; init; }

    public TranslationStatus TranslationStatus { get; init; } = TranslationStatus.NotApplicable;

    /// <summary>Human-readable failure reason when <see cref="TranslationStatus"/> is <see cref="Models.TranslationStatus.Failed"/>.</summary>
    public string? TranslationError { get; init; }
}
