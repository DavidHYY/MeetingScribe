namespace MeetingScribe.Audio;

/// <summary>Raised whenever a source finishes writing a fresh block and has an updated level reading.</summary>
public sealed class AudioLevelEventArgs : EventArgs
{
    public AudioSourceKind Source { get; }
    public AudioLevel Level { get; }

    public AudioLevelEventArgs(AudioSourceKind source, AudioLevel level)
    {
        Source = source;
        Level = level;
    }
}

/// <summary>
/// Raised when a capture source hits a non-fatal error (e.g. the device was unplugged
/// mid-recording). The affected source stops; recording of the other source (if any)
/// continues uninterrupted.
/// </summary>
public sealed class AudioCaptureErrorEventArgs : EventArgs
{
    public AudioSourceKind Source { get; }
    public Exception Exception { get; }

    public AudioCaptureErrorEventArgs(AudioSourceKind source, Exception exception)
    {
        Source = source;
        Exception = exception;
    }
}

/// <summary>
/// Raised on every resampled block from either source, carrying the raw 16kHz mono
/// PCM16 samples themselves (the same block just written to that source's WAV file and
/// handed to the mixer). Intended for a live consumer such as a rough real-time
/// transcription feed - subscribing costs nothing extra (no resampling, no re-read from
/// disk): this is the exact block already produced by the capture pipeline.
/// </summary>
public sealed class AudioSamplesEventArgs : EventArgs
{
    public AudioSourceKind Source { get; }

    /// <summary>16kHz mono PCM16 samples for this block. Do not mutate - shared with the writer/mixer.</summary>
    public IReadOnlyList<short> Samples { get; }

    public AudioSamplesEventArgs(AudioSourceKind source, short[] samples)
    {
        Source = source;
        Samples = samples;
    }
}
