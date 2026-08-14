namespace MeetingScribe.Whisper;

/// <summary>
/// One decoded segment of speech with timing, text and the confidence figures
/// whisper.cpp exposes per segment (populated only when probabilities are requested).
/// </summary>
/// <param name="Start">Segment start offset, relative to the start of the audio passed to that transcription call.</param>
/// <param name="End">Segment end offset, relative to the start of the audio passed to that transcription call.</param>
/// <param name="Text">Decoded text for the segment.</param>
/// <param name="Probability">Average token probability for the segment (0-1).</param>
/// <param name="MinProbability">Minimum token probability found in the segment (0-1).</param>
/// <param name="MaxProbability">Maximum token probability found in the segment (0-1).</param>
/// <param name="NoSpeechProbability">Probability whisper.cpp assigns to the segment being non-speech (0-1).</param>
/// <param name="Language">Language code whisper.cpp attached to this segment (relevant when auto-detecting per chunk).</param>
public sealed record TranscriptionSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    float Probability,
    float MinProbability,
    float MaxProbability,
    float NoSpeechProbability,
    string? Language);
