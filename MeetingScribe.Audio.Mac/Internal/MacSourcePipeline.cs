using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Owns one capture source end-to-end on macOS: the native capture session (mic via
/// AVFoundation, system audio via ScreenCaptureKit - both already resample and downmix
/// to 16kHz mono PCM16 on the native side, see native/meetingscribe_mac_audio.m), that
/// source's own WAV file (written and flushed on every native callback), live RMS/peak
/// metering, and a bounded channel of blocks that <see cref="MacMeetingRecorder"/>'s
/// mixer thread drains to build the combined track. Mirrors
/// <c>MeetingScribe.Audio.Windows.Internal.SourcePipeline</c>'s shape and guarantees.
///
/// Unlike the Windows pipeline (which pulls on a dedicated thread), delivery here is
/// push-based: the native side calls back on its own private serial dispatch queue
/// whenever a resampled block is ready. <see cref="HandleSamples"/> runs on that
/// native thread.
/// </summary>
internal abstract class MacSourcePipeline : IDisposable
{
    public const int TargetSampleRate = 16_000;
    public const int TargetChannels = 1;
    public const int TargetBitsPerSample = 16;

    private readonly AudioSourceKind _kind;
    private readonly string _logName;
    private readonly PcmWavWriter _writer;
    private readonly Channel<short[]> _channel;
    private readonly Action<AudioSourceKind, AudioLevel> _onLevel;
    private readonly Action<AudioSourceKind, Exception> _onError;
    private readonly Action<AudioSourceKind, short[]>? _onSamples;
    private readonly object _levelLock = new();

    private AudioLevel _level;
    private GCHandle _selfHandle;
    private bool _disposed;

    /// <summary>Resampled 16kHz mono PCM16 blocks, one array per native callback, for the mixer to consume.</summary>
    public ChannelReader<short[]> Reader => _channel.Reader;

    public AudioLevel Level
    {
        get { lock (_levelLock) { return _level; } }
    }

    protected MacSourcePipeline(
        AudioSourceKind kind,
        string wavPath,
        Action<AudioSourceKind, AudioLevel> onLevel,
        Action<AudioSourceKind, Exception> onError,
        Action<AudioSourceKind, short[]>? onSamples)
    {
        _kind = kind;
        _logName = kind == AudioSourceKind.Microphone ? "mic" : "system";
        _onLevel = onLevel;
        _onError = onError;
        _onSamples = onSamples;

        _writer = new PcmWavWriter(wavPath, TargetSampleRate, TargetChannels, TargetBitsPerSample);

        _channel = Channel.CreateBounded<short[]>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        // Rooted for the pipeline's lifetime (freed in Dispose) so the GC cannot
        // collect the target of the GCHandle the native side holds as its user_data -
        // the [UnmanagedCallersOnly] static callbacks themselves are always rooted
        // (they are ordinary static methods, not delegate instances), but the pipeline
        // *instance* they resolve via GCHandle.FromIntPtr needs this explicit root.
        _selfHandle = GCHandle.Alloc(this);
    }

    /// <summary>Passed as native user_data - resolved back to this instance via <see cref="GCHandle.FromIntPtr"/>.</summary>
    protected IntPtr UserData => GCHandle.ToIntPtr(_selfHandle);

    public abstract void Start();

    /// <summary>
    /// Stops native capture and marks the mixer channel complete immediately after.
    /// <see cref="StopCore"/> (msc_mic_stop/msc_system_stop) drains any callback
    /// already in flight via a synchronous dispatch before returning, so no further
    /// <see cref="HandleSamples"/> call can happen once it returns - completing the
    /// channel here is therefore safe and, crucially, immediate.
    ///
    /// Completing it here rather than only in <see cref="Dispose"/> (the previous
    /// behaviour) matters: <see cref="MacMeetingRecorder"/>'s mixer thread's only
    /// exit condition is both channels' <c>Reader.Completion</c> becoming completed,
    /// and <see cref="MacMeetingRecorder.StopAsync"/> joins that mixer thread
    /// *before* calling Dispose() on either pipeline - so with completion deferred to
    /// Dispose(), the mixer could never observe completion until after the join it
    /// was itself blocking on had already exhausted its own timeout. Measured: this
    /// made every StopAsync() call burn the full 10s join timeout before returning,
    /// on every session, regardless of which sources were enabled.
    /// </summary>
    public void Stop()
    {
        StopCore();
        _channel.Writer.TryComplete();
    }

    /// <summary>Platform-specific native teardown, wrapped by <see cref="Stop"/>.</summary>
    protected abstract void StopCore();

    /// <summary>Called from the native callback thread with a freshly resampled 16kHz mono PCM16 block.</summary>
    internal unsafe void HandleSamples(short* samples, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var array = new short[count];
        new ReadOnlySpan<short>(samples, count).CopyTo(array);

        try
        {
            _writer.WriteSamples(array);
            _writer.Flush();
        }
        catch (IOException ex)
        {
            HandleError($"WAV write failed: {ex.Message}");
            return;
        }

        UpdateLevel(array);

        // Bounded + DropOldest: never blocks, never grows unbounded if the mixer
        // momentarily falls behind.
        _channel.Writer.TryWrite(array);

        if (_onSamples is not null)
        {
            try
            {
                _onSamples(_kind, array);
            }
            catch (Exception ex)
            {
                HandleError($"SamplesAvailable handler threw: {ex.Message}");
            }
        }
    }

    /// <summary>Called from the native callback thread (or Start/Stop) on any non-fatal error.</summary>
    internal void HandleError(string message)
    {
        _onError(_kind, new InvalidOperationException($"{_logName}: {message}"));
    }

    private void UpdateLevel(short[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        double sumSquares = 0;
        var peak = 0;
        foreach (var sample in samples)
        {
            sumSquares += (double)sample * sample;
            var abs = Math.Abs((int)sample);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        var rms = Math.Sqrt(sumSquares / samples.Length) / 32768.0;
        var peakNorm = peak / 32768.0;
        var level = new AudioLevel(rms, peakNorm, DateTime.UtcNow);

        lock (_levelLock)
        {
            _level = level;
        }

        _onLevel(_kind, level);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop() already completes the channel (see its doc comment); calling it
        // again here would be a harmless no-op via TryComplete's idempotence, but
        // Stop() itself already guards re-entry (session handle nulled after first
        // call in both derived classes), so this is just the normal teardown path.
        Stop();

        try
        {
            _writer.Dispose();
        }
        catch (IOException ex)
        {
            HandleError($"WAV close failed: {ex.Message}");
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}
