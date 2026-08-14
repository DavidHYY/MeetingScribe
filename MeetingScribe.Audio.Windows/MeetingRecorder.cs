using System.Threading.Channels;
using MeetingScribe.Audio.Internal;
using NAudio.Wave;

namespace MeetingScribe.Audio;

/// <summary>
/// Records a meeting to three continuously-flushed 16kHz mono PCM16 WAV files:
/// <c>mic.wav</c> (microphone only), <c>system.wav</c> (system-audio loopback only),
/// and <c>raw.wav</c> (the two mixed). Either source can be enabled independently;
/// both together is the normal online-meeting case, mic-only is an in-person room
/// meeting.
///
/// Not thread-safe for concurrent <see cref="Start"/>/<see cref="StopAsync"/> calls -
/// call from one owner (e.g. the UI thread).
/// </summary>
public sealed class MeetingRecorder : IMeetingRecorder
{
    private readonly MeetingRecorderOptions _options;
    private readonly object _stateLock = new();
    private readonly List<string> _errors = [];

    private SourcePipeline? _micPipeline;
    private SourcePipeline? _systemPipeline;
    private WaveFileWriter? _mixedWriter;
    private Thread? _mixerThread;
    private bool _disposed;

    public string OutputDirectory { get; }
    public string MicWavPath { get; }
    public string SystemWavPath { get; }
    public string MixedWavPath { get; }

    public bool IsRecording { get; private set; }

    /// <summary>Latest RMS/peak reading for the microphone track. Default value until capture starts producing blocks.</summary>
    public AudioLevel MicLevel => _micPipeline?.Level ?? default;

    /// <summary>Latest RMS/peak reading for the system-audio track. Default value until capture starts producing blocks.</summary>
    public AudioLevel SystemLevel => _systemPipeline?.Level ?? default;

    /// <summary>Non-fatal errors accumulated during the current (or most recent) recording session.</summary>
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

    /// <summary>Fires on every resampled block from either source - drive a UI meter from this.</summary>
    public event EventHandler<AudioLevelEventArgs>? LevelUpdated;

    /// <summary>Fires when a source hits a non-fatal capture error and stops; the other source keeps recording.</summary>
    public event EventHandler<AudioCaptureErrorEventArgs>? CaptureError;

    /// <summary>
    /// Fires on every resampled block from either source, carrying the raw 16kHz mono
    /// PCM16 samples - drive a live transcription feed from this. Handlers run
    /// synchronously on that source's dedicated pull thread; do not block in a handler
    /// (hand off to a queue/background task instead).
    /// </summary>
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    public MeetingRecorder(MeetingRecorderOptions options)
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
    /// requested (or default) device cannot be opened - fails fast before any file
    /// is created, rather than silently recording nothing.
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

        MediaFoundationBootstrap.EnsureStarted();

        SourcePipeline? mic = null;
        SourcePipeline? system = null;
        try
        {
            if (_options.MicrophoneEnabled)
            {
                var device = AudioDeviceResolver.ResolveCapture(_options.MicrophoneDeviceId);
                mic = new SourcePipeline(
                    AudioSourceKind.Microphone, device, isLoopback: false, MicWavPath, OnLevel, OnError, OnSamples);
            }

            if (_options.SystemAudioEnabled)
            {
                var device = AudioDeviceResolver.ResolveRender(_options.SystemAudioDeviceId);
                system = new SourcePipeline(
                    AudioSourceKind.SystemAudio, device, isLoopback: true, SystemWavPath, OnLevel, OnError, OnSamples);
            }
        }
        catch
        {
            mic?.Dispose();
            system?.Dispose();
            throw;
        }

        _mixedWriter = new WaveFileWriter(MixedWavPath, SourcePipeline.TargetFormat);
        _micPipeline = mic;
        _systemPipeline = system;

        mic?.Start();
        system?.Start();

        _mixerThread = new Thread(MixerLoop)
        {
            IsBackground = true,
            Name = "MeetingScribe-mixer",
        };
        _mixerThread.Start();

        IsRecording = true;
    }

    /// <summary>Stops capture, drains and closes all three WAV files. Safe to call when not recording (no-op).</summary>
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
    /// Drains both source channels, sums overlapping samples into <c>raw.wav</c>, and
    /// passes a source through unmixed when the other has no data ready. Runs until
    /// both channels report completion (i.e. both pipelines have stopped and fully
    /// drained), so the mixed track loses at most the tail already lost by the
    /// slower-draining source.
    /// </summary>
    private void MixerLoop()
    {
        var micReader = _micPipeline?.Reader;
        var systemReader = _systemPipeline?.Reader;

        while (true)
        {
            short[]? micChunk = null;
            short[]? systemChunk = null;
            var gotMic = micReader is not null && TryReadWithTimeout(micReader, out micChunk);
            var gotSystem = systemReader is not null && TryReadWithTimeout(systemReader, out systemChunk);

            if (!gotMic && !gotSystem)
            {
                var micDone = micReader is null || micReader.Completion.IsCompleted;
                var systemDone = systemReader is null || systemReader.Completion.IsCompleted;
                if (micDone && systemDone)
                {
                    return;
                }

                continue;
            }

            short[] mixed;
            if (gotMic && gotSystem)
            {
                var n = Math.Min(micChunk!.Length, systemChunk!.Length);
                mixed = new short[n];
                for (var i = 0; i < n; i++)
                {
                    var sum = micChunk[i] + systemChunk[i];
                    mixed[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
                }
            }
            else
            {
                mixed = gotMic ? micChunk! : systemChunk!;
            }

            try
            {
                _mixedWriter!.WriteSamples(mixed, 0, mixed.Length);
                _mixedWriter.Flush();
            }
            catch (IOException ex)
            {
                OnError(AudioSourceKind.Microphone, ex);
                return;
            }
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
