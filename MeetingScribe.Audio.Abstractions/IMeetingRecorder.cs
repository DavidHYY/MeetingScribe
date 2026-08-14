namespace MeetingScribe.Audio;

/// <summary>
/// Platform-neutral contract for a meeting recorder: three continuously-flushed 16kHz
/// mono PCM16 WAV files (<c>mic.wav</c>, <c>system.wav</c>, <c>raw.wav</c> mixed), live
/// level metering, and a raw-samples feed for a live transcription consumer. Implemented
/// today by <c>MeetingScribe.Audio.Windows</c>'s NAudio/WASAPI-backed <c>MeetingRecorder</c>;
/// a future macOS implementation (CoreAudio + ScreenCaptureKit/BlackHole) slots in here
/// without any change to callers.
///
/// Not thread-safe for concurrent <see cref="Start"/>/<see cref="StopAsync"/> calls - call
/// from one owner (e.g. the UI thread).
/// </summary>
public interface IMeetingRecorder : IDisposable, IAsyncDisposable
{
    string OutputDirectory { get; }
    string MicWavPath { get; }
    string SystemWavPath { get; }
    string MixedWavPath { get; }

    bool IsRecording { get; }

    /// <summary>Latest RMS/peak reading for the microphone track. Default value until capture starts producing blocks.</summary>
    AudioLevel MicLevel { get; }

    /// <summary>Latest RMS/peak reading for the system-audio track. Default value until capture starts producing blocks.</summary>
    AudioLevel SystemLevel { get; }

    /// <summary>Non-fatal errors accumulated during the current (or most recent) recording session.</summary>
    IReadOnlyList<string> Errors { get; }

    /// <summary>Fires on every resampled block from either source - drive a UI meter from this.</summary>
    event EventHandler<AudioLevelEventArgs>? LevelUpdated;

    /// <summary>Fires when a source hits a non-fatal capture error and stops; the other source keeps recording.</summary>
    event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;

    /// <summary>
    /// Fires on every resampled block from either source, carrying the raw 16kHz mono
    /// PCM16 samples - drive a live transcription feed from this. Handlers run
    /// synchronously on that source's dedicated pull thread; do not block in a handler
    /// (hand off to a queue/background task instead).
    /// </summary>
    event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    /// <summary>
    /// Resolves the requested device(s), opens capture, and starts writing all enabled
    /// tracks. Throws <see cref="AudioDeviceNotFoundException"/> if a requested (or
    /// default) device cannot be opened - fails fast before any file is created, rather
    /// than silently recording nothing.
    /// </summary>
    void Start();

    /// <summary>Stops capture, drains and closes all three WAV files. Safe to call when not recording (no-op).</summary>
    Task StopAsync();
}
