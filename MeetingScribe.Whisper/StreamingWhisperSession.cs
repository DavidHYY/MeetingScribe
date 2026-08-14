using Whisper.net;

namespace MeetingScribe.Whisper;

/// <summary>
/// Result of one <see cref="StreamingWhisperSession.FlushAsync"/> call: the transcription itself,
/// plus the exact raw 16kHz mono samples that were decoded to produce it.
/// </summary>
/// <param name="Transcription">The transcribe-pass result - unchanged from what <see cref="StreamingWhisperSession.FlushAsync"/> always returned.</param>
/// <param name="Samples">
/// The flushed chunk's raw samples, at chunk-relative time (sample 0 = the chunk's own t=0, same
/// timeline as <see cref="Transcription"/>'s segment timestamps before a caller adds its own
/// running offset). Backed by an array that is never written to again after this result is
/// returned (the session swaps in a fresh buffer on every flush - see <see cref="StreamingWhisperSession.FlushAsync"/>),
/// so it is safe for a caller to hold onto this memory for as long as it needs, e.g. to run a
/// second, independent decode (live English translation) over the same audio without re-buffering it.
/// </param>
public readonly record struct StreamingFlushResult(TranscriptionResult Transcription, ReadOnlyMemory<float> Samples);

/// <summary>
/// Accumulates raw 16kHz mono float32 samples from a live capture and flushes them through
/// the owning <see cref="WhisperTranscriber"/>'s model in chunks.
/// </summary>
/// <remarks>
/// <para>
/// Whisper pads every input window to 30s internally regardless of how much audio it
/// actually contains, so transcribing a 5s fragment costs almost as much compute as a 30s
/// one. This session exists to avoid paying that cost repeatedly: it holds audio in a
/// buffer and only tells the caller to flush once <see cref="TargetChunkSeconds"/> has
/// accumulated, with <see cref="MaxChunkSeconds"/> as a hard cap so a talkative speaker
/// with no pauses doesn't grow the buffer (and the transcription latency) without bound.
/// </para>
/// <para>
/// This class only manages the buffer and decides *whether* a flush is due
/// (<see cref="ShouldFlush"/>/<see cref="MustFlush"/>/<see cref="NotifyPause"/>) — it never
/// flushes on its own. The caller's capture loop stays in control of when a whisper.cpp call
/// actually happens.
/// </para>
/// </remarks>
public sealed class StreamingWhisperSession : IAsyncDisposable
{
    /// <summary>Sample rate this session (and whisper.cpp) requires: 16kHz mono.</summary>
    public const int SampleRateHz = 16_000;

    /// <summary>Soft target: once this much audio has accumulated, <see cref="ShouldFlush"/> turns true.</summary>
    public const double TargetChunkSeconds = 15.0;

    /// <summary>Hard cap: once this much audio has accumulated, <see cref="MustFlush"/> turns true regardless of pauses.</summary>
    public const double MaxChunkSeconds = 25.0;

    /// <summary>
    /// Minimum buffered audio before a caller-signalled pause (<see cref="NotifyPause"/>) is
    /// honored. Below this, a flush would mostly be paying the 30s padding cost for almost
    /// nothing decoded, so it is not worth cutting the chunk early.
    /// </summary>
    public const double MinFlushSeconds = 1.0;

    private static readonly int TargetSampleCount = (int)(TargetChunkSeconds * SampleRateHz);
    private static readonly int MaxSampleCount = (int)(MaxChunkSeconds * SampleRateHz);
    private static readonly int MinFlushSampleCount = (int)(MinFlushSeconds * SampleRateHz);

    private readonly WhisperTranscriber _transcriber;
    private readonly WhisperProcessor _processor;
    private readonly object _lock = new();

    private float[] _buffer;
    private int _count;
    private bool _disposed;

    internal StreamingWhisperSession(WhisperTranscriber transcriber, WhisperProcessor processor)
    {
        _transcriber = transcriber;
        _processor = processor;
        _buffer = new float[MaxSampleCount];
    }

    /// <summary>How much audio is currently buffered, awaiting a flush.</summary>
    public double BufferedSeconds
    {
        get { lock (_lock) return _count / (double)SampleRateHz; }
    }

    /// <summary>True once <see cref="TargetChunkSeconds"/> of audio has accumulated. Advisory — caller decides when to actually flush.</summary>
    public bool ShouldFlush
    {
        get { lock (_lock) return _count >= TargetSampleCount; }
    }

    /// <summary>True once <see cref="MaxChunkSeconds"/> of audio has accumulated. The caller should flush now to avoid unbounded latency.</summary>
    public bool MustFlush
    {
        get { lock (_lock) return _count >= MaxSampleCount; }
    }

    /// <summary>Appends newly captured samples to the pending buffer. Never triggers a flush by itself.</summary>
    public void AppendSamples(ReadOnlyMemory<float> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            EnsureCapacity(_count + samples.Length);
            samples.Span.CopyTo(_buffer.AsSpan(_count));
            _count += samples.Length;
        }
    }

    /// <summary>
    /// Call when an external signal (VAD, UI "stop talking" gap) detects a real pause.
    /// Returns true when there is enough buffered audio (&gt;= <see cref="MinFlushSeconds"/>)
    /// that flushing now is worthwhile; the caller should then call <see cref="FlushAsync"/>.
    /// Returns false for a near-empty buffer, where flushing would mostly pay padding cost
    /// for little content.
    /// </summary>
    public bool NotifyPause()
    {
        lock (_lock)
        {
            return _count >= MinFlushSampleCount;
        }
    }

    /// <summary>
    /// Runs whisper.cpp over everything buffered so far and clears the buffer. Safe to call
    /// with an empty buffer (returns an empty result) so callers can flush unconditionally at
    /// session end without checking first.
    /// </summary>
    public async Task<StreamingFlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        float[] chunk;
        int chunkLength;

        lock (_lock)
        {
            chunkLength = _count;
            chunk = _buffer;
            _buffer = new float[MaxSampleCount];
            _count = 0;
        }

        if (chunkLength == 0)
        {
            return new StreamingFlushResult(EmptyResult(), ReadOnlyMemory<float>.Empty);
        }

        // chunk is the buffer this session held while it was accumulating - swapped out for a
        // fresh array above, so it is never written to again and safe for the returned
        // StreamingFlushResult to keep referencing after this method returns (see that type's
        // remarks).
        var samples = chunkLength == chunk.Length ? chunk : chunk[..chunkLength];
        var transcription = await _transcriber.TranscribeSamplesAsync(_processor, samples, cancellationToken)
            .ConfigureAwait(false);
        return new StreamingFlushResult(transcription, samples);
    }

    private TranscriptionResult EmptyResult() => new(
        DetectedLanguage: "unknown",
        LanguageProbability: 0f,
        Segments: [],
        FullText: string.Empty,
        AudioDuration: TimeSpan.Zero,
        WallClock: TimeSpan.Zero,
        RealtimeFactor: 0.0,
        Backend: _transcriber.Backend,
        LanguageSwitches: [],
        OutOfSetLanguageDetections: new Dictionary<string, int>());

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var newCapacity = Math.Max(required, _buffer.Length * 2);
        Array.Resize(ref _buffer, newCapacity);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _processor.DisposeAsync().ConfigureAwait(false);
    }
}
