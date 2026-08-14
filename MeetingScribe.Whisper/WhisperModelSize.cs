namespace MeetingScribe.Whisper;

/// <summary>
/// Whisper model sizes exposed to callers. Maps internally to the ggml/Whisper.net
/// <c>GgmlType</c> values actually used for model resolution and download.
/// </summary>
public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
    LargeV3
}
