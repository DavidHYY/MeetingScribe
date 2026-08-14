using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Owns one capture source end-to-end: the WASAPI capture object, the device's
/// native-format-to-16kHz-mono-PCM16 resample pipeline, that source's own WAV file
/// (written and flushed continuously), live RMS/peak metering, and a bounded channel
/// of resampled blocks that <see cref="MeetingScribe.Audio.MeetingRecorder"/>'s mixer
/// thread drains to build the combined track.
///
/// One instance == one dedicated background pull thread. <see cref="Dispose"/> tears
/// down the capture object, the pull thread, the file writer and the device COM
/// reference, in that order, so nothing outlives the pipeline.
/// </summary>
internal sealed class SourcePipeline : IDisposable
{
    public static readonly WaveFormat TargetFormat = new(16_000, 16, 1);

    private readonly AudioSourceKind _kind;
    private readonly string _logName;
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _bufferedProvider;
    private readonly MediaFoundationResampler _resampler;
    private readonly WaveFileWriter _writer;
    private readonly Channel<short[]> _channel;
    private readonly Action<AudioSourceKind, AudioLevel> _onLevel;
    private readonly Action<AudioSourceKind, Exception> _onError;
    private readonly Action<AudioSourceKind, short[]>? _onSamples;

    private readonly object _levelLock = new();
    private AudioLevel _level;

    private Thread? _pullThread;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <summary>Resampled 16kHz mono PCM16 blocks, one array per resampler read, for the mixer to consume.</summary>
    public ChannelReader<short[]> Reader => _channel.Reader;

    public AudioLevel Level
    {
        get { lock (_levelLock) { return _level; } }
    }

    public SourcePipeline(
        AudioSourceKind kind,
        MMDevice device,
        bool isLoopback,
        string wavPath,
        Action<AudioSourceKind, AudioLevel> onLevel,
        Action<AudioSourceKind, Exception> onError,
        Action<AudioSourceKind, short[]>? onSamples = null)
    {
        _kind = kind;
        _logName = kind == AudioSourceKind.Microphone ? "mic" : "system";
        _onSamples = onSamples;
        _device = device;
        _onLevel = onLevel;
        _onError = onError;

        // WasapiLoopbackCapture derives from WasapiCapture, so both constructions are
        // held through the same base-typed field.
        _capture = isLoopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        // Native device format (commonly 48000/44100 Hz, stereo, IEEE float) - this is
        // exactly what requirement #4 means by "whatever the device's native format is".
        _bufferedProvider = new BufferedWaveProvider(_capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(30),
            // Load-bearing: NAudio's default (ReadFully = true) silently zero-pads a
            // Read() up to the full requested count whenever the buffer holds less
            // than that - verified via a direct reflection probe against 2.3.0, since
            // this is undocumented in the property summary. Left at the default, an
            // empty buffer never produces a bytesRead of 0, so the pull-loop drain
            // condition below never triggers: it spins forever writing fabricated
            // silence and the pipeline hangs past its shutdown timeout. False makes
            // Read() return only what's actually buffered (0 when empty), which is
            // what both live metering accuracy and a clean Stop() depend on.
            ReadFully = false,
        };

        // Windows Media Foundation's resampler DSP handles sample-rate conversion,
        // channel downmix, and bit-depth/encoding conversion in one step - native
        // format straight to 16kHz mono PCM16.
        _resampler = new MediaFoundationResampler(_bufferedProvider, TargetFormat)
        {
            ResamplerQuality = 60,
        };

        _writer = new WaveFileWriter(wavPath, TargetFormat);

        _channel = Channel.CreateBounded<short[]>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public void Start()
    {
        _capture.StartRecording();
        _pullThread = new Thread(PullLoop)
        {
            IsBackground = true,
            Name = $"MeetingScribe-{_logName}-pull",
        };
        _pullThread.Start();
    }

    /// <summary>Signals capture to stop. The pull thread drains any already-buffered audio before exiting.</summary>
    public void Stop()
    {
        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        try
        {
            _capture.StopRecording();
        }
        catch (COMException ex)
        {
            _onError(_kind, ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
        {
            _bufferedProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _onError(_kind, e.Exception);
        }
    }

    /// <summary>
    /// Runs on its own thread for the pipeline's lifetime: pull resampled bytes,
    /// write+flush to this source's WAV file, update the level meter, hand a copy to
    /// the mixer channel. Continues past <see cref="Stop"/> until a few consecutive
    /// empty reads confirm the resampler is fully drained (bounded by
    /// <see cref="MaxDrainTime"/> as a hard backstop).
    /// </summary>
    private void PullLoop()
    {
        // ~200ms per read call at the target format - close to WASAPI's own callback
        // cadence, so most calls return a real, mostly-full block instead of either
        // starving (buffer too small) or reading mostly stale/empty space (too large,
        // which also made the live meter sluggish).
        var buffer = new byte[TargetFormat.AverageBytesPerSecond / 5];
        var consecutiveEmptyReads = 0;
        DateTime? stopDrainDeadline = null;

        try
        {
            while (true)
            {
                if (_stopRequested && stopDrainDeadline is null)
                {
                    stopDrainDeadline = DateTime.UtcNow + MaxDrainTime;
                }

                int bytesRead;
                try
                {
                    bytesRead = _resampler.Read(buffer, 0, buffer.Length);
                }
                catch (COMException ex)
                {
                    _onError(_kind, ex);
                    break;
                }

                if (bytesRead > 0)
                {
                    consecutiveEmptyReads = 0;

                    var sampleCount = bytesRead / sizeof(short);
                    var samples = new short[sampleCount];
                    Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead);

                    try
                    {
                        _writer.WriteSamples(samples, 0, sampleCount);
                        _writer.Flush();
                    }
                    catch (IOException ex)
                    {
                        _onError(_kind, ex);
                        break;
                    }

                    UpdateLevel(samples);

                    // Bounded + DropOldest: never blocks, never grows unbounded if the
                    // mixer momentarily falls behind.
                    _channel.Writer.TryWrite(samples);

                    // Best-effort fan-out for live consumers (e.g. a live transcription
                    // feed). Not the channel above: that one is single-reader/single-writer
                    // and owned by the mixer. A misbehaving subscriber must not be able to
                    // stall or crash this real-time pull thread.
                    if (_onSamples is not null)
                    {
                        try
                        {
                            _onSamples(_kind, samples);
                        }
                        catch (Exception ex)
                        {
                            _onError(_kind, ex);
                        }
                    }
                }
                else
                {
                    consecutiveEmptyReads++;
                    if (_stopRequested && consecutiveEmptyReads >= 3)
                    {
                        // Stop() was called and the resampler has nothing left buffered.
                        break;
                    }

                    Thread.Sleep(_stopRequested ? 15 : 30);
                }

                // Backstop: even if bytesRead keeps coming back > 0 for some unforeseen
                // reason after Stop(), never drain forever - Dispose() is waiting on
                // this thread and must not be left racing a runaway loop.
                if (stopDrainDeadline is { } deadline && DateTime.UtcNow > deadline)
                {
                    _onError(_kind, new TimeoutException(
                        $"{_logName}: pull loop still producing data {MaxDrainTime.TotalSeconds}s after Stop(); forcing exit."));
                    break;
                }
            }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private static readonly TimeSpan MaxDrainTime = TimeSpan.FromSeconds(3);

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

        Stop();

        // PullLoop's own MaxDrainTime backstop means it self-terminates well inside
        // this window in the normal case; this Join is just where we observe that.
        // If it still times out, the thread is genuinely wedged inside a call we don't
        // control (e.g. blocked in the MF resampler's COM layer) - tearing down
        // _capture/_resampler/_writer/_device out from under a thread that might still
        // be executing inside them is a use-after-free (verified: this raced into an
        // access violation during real-hardware testing). Leaking those handles is far
        // preferable to crashing the process, so we report loudly and stop here.
        if (_pullThread is not null && !_pullThread.Join(TimeSpan.FromSeconds(10)))
        {
            _onError(_kind, new TimeoutException(
                $"{_logName}: pull thread did not exit within 10s of Stop(). Abandoning its capture/resampler/" +
                "writer/device COM objects rather than disposing them out from under a still-running thread " +
                "(this leaks them for the rest of the process lifetime, but a crash is worse)."));
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();

        _resampler.Dispose();

        try
        {
            _writer.Flush();
        }
        catch (IOException ex)
        {
            _onError(_kind, ex);
        }
        finally
        {
            _writer.Dispose();
        }

        _device.Dispose();
    }
}
