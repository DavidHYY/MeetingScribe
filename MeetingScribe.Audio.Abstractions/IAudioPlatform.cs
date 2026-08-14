namespace MeetingScribe.Audio;

/// <summary>
/// The seam a platform-specific audio backend implements: device enumeration, a recorder
/// factory, and arbitrary-file audio decoding. This is the only audio surface application code
/// (the App project) touches directly - never a concrete recorder/enumerator type, never NAudio.
///
/// <c>MeetingScribe.Audio.Windows</c>'s <c>WindowsAudioPlatform</c> is the real implementation
/// (NAudio/WASAPI for capture, NAudio's Media Foundation wrapper for
/// <see cref="DecodeAudioFileToWavAsync"/>). <c>MeetingScribe.Audio.Mac</c>'s
/// <c>MacAudioPlatform</c> implements capture natively (AVFoundation/ScreenCaptureKit via a small
/// Objective-C helper) but not <see cref="DecodeAudioFileToWavAsync"/> - see that method's remarks
/// for why that one member is a deliberate, honest gap rather than a full stub.
/// </summary>
public interface IAudioPlatform
{
    /// <summary>Active capture (input/microphone) devices.</summary>
    IReadOnlyList<AudioDeviceInfo> ListCaptureDevices();

    /// <summary>Active render (playback/speaker) devices — the set loopback capture can be attached to.</summary>
    IReadOnlyList<AudioDeviceInfo> ListRenderDevices();

    /// <summary>Creates a new, not-yet-started recorder for one meeting session.</summary>
    IMeetingRecorder CreateRecorder(MeetingRecorderOptions options);

    /// <summary>
    /// Decodes an arbitrary compressed audio file (a phone/voice-recorder/meeting-tool export -
    /// MP3, M4A/AAC, MP4 audio, etc.) into a seekable, standard PCM RIFF/WAVE stream: 16kHz mono
    /// 16-bit PCM, the exact format <c>MeetingScribe.Whisper.WavAudioLoader</c> and
    /// <c>WhisperTranscriber.TranscribeFileAsync</c> already consume natively. Callers only ever
    /// need this for a non-WAV import - a WAV file goes straight to
    /// <c>WhisperTranscriber.TranscribeFileAsync</c> without touching this method at all, since
    /// <c>WavAudioLoader</c> already reads WAV (any sample rate/channel count/bit depth) directly.
    ///
    /// The returned stream owns a temporary file and deletes it on <see cref="Stream.Dispose()"/>
    /// (<c>FileOptions.DeleteOnClose</c>) - callers must dispose it once transcription is done,
    /// same as any other stream, and must not assume in-memory buffering for a long recording.
    ///
    /// Throws <see cref="InvalidDataException"/> for a corrupt/empty/no-audio-stream file (message
    /// names the actual problem, not a generic failure) and <see cref="PlatformNotSupportedException"/>
    /// where the platform has no decoder for arbitrary containers at all (today: macOS - see
    /// <c>MacAudioPlatform</c>).
    /// </summary>
    Task<Stream> DecodeAudioFileToWavAsync(string filePath, CancellationToken cancellationToken = default);
}
