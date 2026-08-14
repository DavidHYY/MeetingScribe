namespace MeetingScribe.Audio;

/// <summary>Configuration for a single <see cref="MeetingRecorder"/> recording session.</summary>
public sealed class MeetingRecorderOptions
{
    /// <summary>
    /// Directory the three output WAV files are written into. Created if it does not
    /// already exist.
    /// </summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Capture the microphone ("our side"). Default true.</summary>
    public bool MicrophoneEnabled { get; init; } = true;

    /// <summary>
    /// <see cref="AudioDeviceInfo.Id"/> of the capture device to use for the microphone
    /// track. Null (default) uses the system default recording device.
    /// </summary>
    public string? MicrophoneDeviceId { get; init; }

    /// <summary>Capture system audio via WASAPI loopback ("the remote side"). Default true.</summary>
    public bool SystemAudioEnabled { get; init; } = true;

    /// <summary>
    /// <see cref="AudioDeviceInfo.Id"/> of the render device to loop back. Null
    /// (default) uses the system default playback device.
    /// </summary>
    public string? SystemAudioDeviceId { get; init; }

    /// <summary>
    /// Throws if the option combination cannot produce any audio at all. Public (not
    /// internal) because <see cref="MeetingRecorderOptions"/> lives in the cross-platform
    /// abstractions assembly while every real <c>IMeetingRecorder</c> implementation - the
    /// only caller today, and any future platform provider - lives in a separate
    /// platform-specific assembly.
    /// </summary>
    public void Validate()
    {
        if (!MicrophoneEnabled && !SystemAudioEnabled)
        {
            throw new ArgumentException(
                "At least one of MicrophoneEnabled or SystemAudioEnabled must be true.",
                nameof(MicrophoneEnabled));
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("OutputDirectory must be a non-empty path.", nameof(OutputDirectory));
        }
    }
}
