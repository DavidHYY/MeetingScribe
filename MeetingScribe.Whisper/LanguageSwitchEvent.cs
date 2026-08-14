namespace MeetingScribe.Whisper;

/// <summary>
/// One confirmed change of the active decode language during
/// <see cref="WhisperTranscriber.TranscribeFileAsync"/>: from <see cref="FromLanguage"/> to
/// <see cref="ToLanguage"/>, taking effect starting at the chunk at offset <see cref="At"/>
/// (absolute, same clock as <see cref="TranscriptionSegment.Start"/>).
/// </summary>
/// <param name="At">Absolute offset, from the start of the file, of the chunk this switch took effect at.</param>
/// <param name="FromLanguage">
/// Language code active immediately before this switch. <c>"unknown"</c> when no language had
/// been resolved yet — the very first switch of the file, before any seed or per-chunk detection
/// had settled on anything.
/// </param>
/// <param name="ToLanguage">Language code the file switched to.</param>
public sealed record LanguageSwitchEvent(TimeSpan At, string FromLanguage, string ToLanguage);
