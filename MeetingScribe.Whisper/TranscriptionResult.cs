namespace MeetingScribe.Whisper;

/// <summary>
/// Full result of transcribing one audio input (a whole file, or one streaming chunk).
/// </summary>
/// <param name="DetectedLanguage">
/// Language code whisper.cpp settled on. When an explicit language override was configured,
/// this simply echoes it back; otherwise it comes from whisper.cpp's own language-detection pass.
/// </param>
/// <param name="LanguageProbability">Confidence for <see cref="DetectedLanguage"/> (0-1). 0 when an override was used (no detection ran).</param>
/// <param name="Segments">Decoded segments in chronological order.</param>
/// <param name="FullText">All segment texts concatenated, whitespace-trimmed and joined with single spaces.</param>
/// <param name="AudioDuration">Duration of the audio that was transcribed.</param>
/// <param name="WallClock">Wall-clock time the transcription call took.</param>
/// <param name="RealtimeFactor">
/// <see cref="AudioDuration"/> divided by <see cref="WallClock"/>. Greater than 1.0 means
/// faster than real time (e.g. 2.0x transcribes a 60s clip in 30s).
/// </param>
/// <param name="Backend">Which native backend actually ran this transcription.</param>
/// <param name="LanguageSwitches">
/// Every confirmed active-language change made while decoding this file (see
/// <see cref="WhisperTranscriber.TranscribeFileAsync"/>'s per-chunk, hysteresis-confirmed
/// switching), in chronological order. Empty for a single-chunk result (a streaming-session flush
/// via <see cref="WhisperTranscriber.CreateStreamingSession"/>), since there is only one chunk and
/// nothing to switch between.
/// </param>
/// <param name="OutOfSetLanguageDetections">
/// Per-language chunk counts for languages a per-chunk detection reported that fell outside
/// <see cref="WhisperTranscriberOptions.AllowedLanguages"/> and were therefore never switched to.
/// Keys are language codes, values are how many chunks detected that language. Empty when the
/// allowed-language restriction is off, or every detection stayed in-set.
/// </param>
public sealed record TranscriptionResult(
    string DetectedLanguage,
    float LanguageProbability,
    IReadOnlyList<TranscriptionSegment> Segments,
    string FullText,
    TimeSpan AudioDuration,
    TimeSpan WallClock,
    double RealtimeFactor,
    BackendInfo Backend,
    IReadOnlyList<LanguageSwitchEvent> LanguageSwitches,
    IReadOnlyDictionary<string, int> OutOfSetLanguageDetections);
