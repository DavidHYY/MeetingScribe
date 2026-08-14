namespace MeetingScribe.App.Models;

/// <summary>
/// Lifecycle of a <see cref="TranscriptLine"/>'s English translation. Translation always lands
/// later than the original text (it is a second, secondary whisper.cpp decode of the same audio
/// - see <see cref="TranscriptLine.Translation"/>), so a line's status starts at
/// <see cref="Pending"/> or <see cref="NotApplicable"/> and is updated in place once (and if) a
/// result arrives.
/// </summary>
public enum TranslationStatus
{
    /// <summary>
    /// Live translation is off, or this line's chunk was already detected as English (translating
    /// English to English would waste compute for no benefit - see
    /// <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>). No translation will ever
    /// arrive for this line.
    /// </summary>
    NotApplicable,

    /// <summary>Translation was queued and has not completed yet. Show the original text only.</summary>
    Pending,

    /// <summary>Translation completed; <see cref="TranscriptLine.Translation"/> holds the result (possibly empty).</summary>
    Completed,

    /// <summary>
    /// The translator fell behind (its bounded backlog was full) and this line's chunk was
    /// dropped to keep translation from ever delaying transcription or audio capture. Never
    /// silently blank - surfaced so the UI can show "translation skipped" instead of an
    /// indefinite "Pending".
    /// </summary>
    Skipped,

    /// <summary>The translate decode call itself threw. See <see cref="TranscriptLine.TranslationError"/>.</summary>
    Failed,
}
