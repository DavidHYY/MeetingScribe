using System.Threading.Channels;
using MeetingScribe.Audio.Internal;

namespace MeetingScribe.Audio;

/// <summary>
/// Records a meeting to three continuously-flushed 16kHz mono PCM16 WAV files on
/// macOS: <c>mic.wav</c> (AVFoundation microphone capture), <c>system.wav</c>
/// (ScreenCaptureKit system-audio capture), and <c>raw.wav</c> (the two mixed). Either
/// source can be enabled independently. Mirrors
/// <c>MeetingScribe.Audio.Windows.MeetingRecorder</c>'s shape, guarantees, and mixer
/// logic exactly - see that file for the reference behaviour this matches.
///
/// Not thread-safe for concurrent <see cref="Start"/>/<see cref="StopAsync"/> calls -
/// call from one owner (e.g. the UI thread).
/// </summary>
public sealed class MacMeetingRecorder : IMeetingRecorder
{
    private readonly MeetingRecorderOptions _options;
    private readonly object _stateLock = new();
    private readonly List<string> _errors = [];

    private MacSourcePipeline? _micPipeline;
    private MacSourcePipeline? _systemPipeline;
    private Internal.PcmWavWriter? _mixedWriter;
    private Thread? _mixerThread;
    private bool _disposed;

    public string OutputDirectory { get; }
    public string MicWavPath { get; }
    public string SystemWavPath { get; }
    public string MixedWavPath { get; }

    public bool IsRecording { get; private set; }

    public AudioLevel MicLevel => _micPipeline?.Level ?? default;

    public AudioLevel SystemLevel => _systemPipeline?.Level ?? default;

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_stateLock)
            {
                return _errors.ToArray();
            }
        }
    }

    public event EventHandler<AudioLevelEventArgs>? LevelUpdated;

    public event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    public MacMeetingRecorder(MeetingRecorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;

        OutputDirectory = options.OutputDirectory;
        Directory.CreateDirectory(OutputDirectory);

        MicWavPath = Path.Combine(OutputDirectory, "mic.wav");
        SystemWavPath = Path.Combine(OutputDirectory, "system.wav");
        MixedWavPath = Path.Combine(OutputDirectory, "raw.wav");
    }

    /// <summary>
    /// Resolves the requested device(s), opens capture, and starts writing all
    /// enabled tracks. Throws <see cref="AudioDeviceNotFoundException"/> if a
    /// requested device cannot be resolved, or if a capture session cannot be opened
    /// (including TCC permission denial - the exception message names the exact
    /// System Settings pane to fix) - fails fast before any file is created.
    /// </summary>
    public void Start()
    {
        lock (_stateLock)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("Already recording. Call StopAsync() first.");
            }

            _errors.Clear();
        }

        // Resolve/validate both requested devices before creating any pipeline (which
        // is what opens a WAV file) - a bad device id must not leave a half-started
        // recording with one file created and the other missing.
        if (_options.MicrophoneEnabled)
        {
            MacDeviceResolver.ValidateCapture(_options.MicrophoneDeviceId);
        }

        if (_options.SystemAudioEnabled)
        {
            MacDeviceResolver.ValidateRender(_options.SystemAudioDeviceId);
        }

        MacSourcePipeline? mic = null;
        MacSourcePipeline? system = null;
        try
        {
            if (_options.MicrophoneEnabled)
            {
                mic = new MacMicPipeline(_options.MicrophoneDeviceId, MicWavPath, OnLevel, OnError, OnSamples);
            }

            if (_options.SystemAudioEnabled)
            {
                system = new MacSystemPipeline(SystemWavPath, OnLevel, OnError, OnSamples);
            }
        }
        catch
        {
            mic?.Dispose();
            system?.Dispose();
            throw;
        }

        try
        {
            mic?.Start();
            system?.Start();
        }
        catch
        {
            mic?.Dispose();
            system?.Dispose();
            throw;
        }

        _mixedWriter = new Internal.PcmWavWriter(
            MixedWavPath, MacSourcePipeline.TargetSampleRate, MacSourcePipeline.TargetChannels, MacSourcePipeline.TargetBitsPerSample);
        _micPipeline = mic;
        _systemPipeline = system;

        _mixerThread = new Thread(MixerLoop)
        {
            IsBackground = true,
            Name = "MeetingScribe-mac-mixer",
        };
        _mixerThread.Start();

        IsRecording = true;
    }

    public async Task StopAsync()
    {
        lock (_stateLock)
        {
            if (!IsRecording)
            {
                return;
            }

            IsRecording = false;
        }

        _micPipeline?.Stop();
        _systemPipeline?.Stop();

        if (_mixerThread is not null)
        {
            await Task.Run(() => _mixerThread.Join(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        }

        _micPipeline?.Dispose();
        _systemPipeline?.Dispose();
        _micPipeline = null;
        _systemPipeline = null;
        _mixerThread = null;

        if (_mixedWriter is not null)
        {
            try
            {
                _mixedWriter.Flush();
            }
            catch (IOException ex)
            {
                OnError(AudioSourceKind.Microphone, ex); // mixed-track failure; no single-source kind applies
            }
            finally
            {
                _mixedWriter.Dispose();
                _mixedWriter = null;
            }
        }
    }

    /// <summary>
    /// Drains both source channels into per-source sample backlogs and sums
    /// overlapping samples into <c>raw.wav</c>, silence-padding whichever source has
    /// no data ready for a given round rather than truncating to
    /// <c>Math.Min(micChunk.Length, systemChunk.Length)</c> and discarding the
    /// remainder (the bug this replaced - see the backlog comment below).
    ///
    /// Deliberately NOT identical to <c>MeetingScribe.Audio.Windows.MeetingRecorder.
    /// MixerLoop</c> anymore: Windows's single shared pull loop reads both sources
    /// with the same buffer size, so chunk lengths already match block-for-block and
    /// a chunk-level Math.Min never drops real audio. macOS delivers mic and system
    /// audio via two independent, push-based native callbacks (AVFoundation vs
    /// ScreenCaptureKit) with different, uncorrelated block sizes - measured mic
    /// ~170-171 samples/callback against system ~320 samples/callback on this
    /// hardware - so a chunk-level Math.Min discarded roughly half of every system
    /// block, every round, for the whole session (measured raw.wav losing ~44% of a
    /// 15s take). Buffering at the sample level and carrying any unconsumed
    /// remainder over to the next round fixes that without dropping anything.
    /// </summary>
    private void MixerLoop()
    {
        var micReader = _micPipeline?.Reader;
        var systemReader = _systemPipeline?.Reader;

        // Per-source sample backlog, fed by draining every chunk currently queued in
        // that source's channel each round (not just one chunk at a time) and
        // consumed in emitted-sample-count units rather than native-callback-block
        // units - see the class doc comment above for why the two never line up here.
        var micBacklog = new List<short>();
        var systemBacklog = new List<short>();

        while (true)
        {
            var micDoneBeforeFill = micReader is null || micReader.Completion.IsCompleted;
            var systemDoneBeforeFill = systemReader is null || systemReader.Completion.IsCompleted;

            // Only wait on a source that currently has nothing buffered and isn't
            // finished yet - one with leftover backlog from the previous round (or
            // one that's already done) needs no further waiting, so this never blocks
            // on a source that's simply running slightly ahead of the other.
            FillBacklog(micReader, micBacklog, wait: micBacklog.Count == 0 && !micDoneBeforeFill);
            FillBacklog(systemReader, systemBacklog, wait: systemBacklog.Count == 0 && !systemDoneBeforeFill);

            // A source counts as done only once its channel is completed *and* its
            // backlog is fully drained - so nothing buffered is ever lost even if
            // completion is observed before the last chunk has been consumed.
            var micDone = micReader is null || (micReader.Completion.IsCompleted && micBacklog.Count == 0);
            var systemDone = systemReader is null || (systemReader.Completion.IsCompleted && systemBacklog.Count == 0);

            if (micDone && systemDone)
            {
                return;
            }

            if (micBacklog.Count == 0 && systemBacklog.Count == 0)
            {
                // Both sources are still live but neither delivered anything within
                // the bounded wait above (FillBacklog) - loop back and try again
                // rather than padding on what's very likely still just ordinary
                // startup/inter-callback jitter that resolves within a round or two.
                continue;
            }

            int n;
            if (micBacklog.Count > 0 && systemBacklog.Count > 0)
            {
                // Both sources have real data queued - combine as much as both
                // currently hold. Any leftover on the longer side stays buffered for
                // the next round instead of being discarded (the original bug).
                n = Math.Min(micBacklog.Count, systemBacklog.Count);
            }
            else
            {
                // One side is empty after its bounded wait - either it's finished, or
                // it genuinely didn't deliver in time - emit what the other side has,
                // silence-padding the empty side so raw.wav keeps spanning the full
                // session instead of stalling on (or truncating to) whichever source
                // is behind.
                n = Math.Max(micBacklog.Count, systemBacklog.Count);
            }

            var micSlice = TakeWithSilencePadding(micBacklog, n);
            var systemSlice = TakeWithSilencePadding(systemBacklog, n);
            var mixed = new short[n];
            for (var i = 0; i < n; i++)
            {
                // Silence-padded entries are 0, so this sums correctly whether both
                // sides have real data or one side is pure padding.
                var sum = micSlice[i] + systemSlice[i];
                mixed[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }

            try
            {
                _mixedWriter!.WriteSamples(mixed);
                _mixedWriter.Flush();
            }
            catch (IOException ex)
            {
                OnError(AudioSourceKind.Microphone, ex);
                return;
            }
        }
    }

    /// <summary>
    /// If <paramref name="wait"/>, blocks up to 200ms for <paramref name="reader"/> to
    /// produce its next chunk; either way, then drains everything else already
    /// queued (non-blocking) into <paramref name="backlog"/> so nothing is left
    /// sitting in the channel across rounds.
    /// </summary>
    private static void FillBacklog(ChannelReader<short[]>? reader, List<short> backlog, bool wait)
    {
        if (reader is null)
        {
            return;
        }

        if (wait && TryReadWithTimeout(reader, out var chunk))
        {
            backlog.AddRange(chunk!);
        }

        while (reader.TryRead(out var extra))
        {
            backlog.AddRange(extra);
        }
    }

    private static bool TryReadWithTimeout(ChannelReader<short[]> reader, out short[]? chunk)
    {
        if (reader.TryRead(out chunk))
        {
            return true;
        }

        try
        {
            if (reader.WaitToReadAsync().AsTask().Wait(TimeSpan.FromMilliseconds(200)))
            {
                return reader.TryRead(out chunk);
            }
        }
        catch (ChannelClosedException)
        {
            // Writer completed while we were waiting; fall through to "no data".
        }

        chunk = null;
        return false;
    }

    /// <summary>
    /// Copies up to <paramref name="count"/> samples out of <paramref name="backlog"/>
    /// (removing them), zero-filling (silence) whatever <paramref name="backlog"/>
    /// could not supply.
    /// </summary>
    private static short[] TakeWithSilencePadding(List<short> backlog, int count)
    {
        var result = new short[count];
        var take = Math.Min(count, backlog.Count);
        if (take > 0)
        {
            backlog.CopyTo(0, result, 0, take);
            backlog.RemoveRange(0, take);
        }

        // Anything beyond `take` stays at the array's default value, 0 - silence.
        return result;
    }

    private void OnLevel(AudioSourceKind source, AudioLevel level)
    {
        LevelUpdated?.Invoke(this, new AudioLevelEventArgs(source, level));
    }

    private void OnSamples(AudioSourceKind source, short[] samples)
    {
        SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(source, samples));
    }

    private void OnError(AudioSourceKind source, Exception ex)
    {
        lock (_stateLock)
        {
            _errors.Add($"{source}: {ex.Message}");
        }

        CaptureError?.Invoke(this, new AudioCaptureErrorEventArgs(source, ex));
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsRecording)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }
}
