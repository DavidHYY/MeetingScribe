using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MeetingScribe.App.Models;
using MeetingScribe.Audio;
using MeetingScribe.Whisper;

namespace MeetingScribe.App.Services;

/// <summary>Raised whenever the live engine has decoded a new rough segment for one track.</summary>
public sealed class LiveSegmentEventArgs(TranscriptLine line) : EventArgs
{
    public TranscriptLine Line { get; } = line;
}

/// <summary>
/// Raised whenever a queued live English translation for a previously-emitted
/// <see cref="TranscriptLine"/> settles - successfully, with a failure, or skipped because the
/// translator fell behind. Always arrives strictly after the <see cref="LiveSegmentEventArgs"/>
/// for the same <see cref="LineId"/> (translation is a second, secondary decode of audio the
/// transcribe pass already finished with - see <see cref="LiveTranscriptionEngine"/>'s remarks).
/// </summary>
public sealed class LiveTranslationEventArgs(
    long lineId,
    AudioSourceKind source,
    string? translation,
    TranslationStatus status,
    string? error) : EventArgs
{
    public long LineId { get; } = lineId;
    public AudioSourceKind Source { get; } = source;
    public string? Translation { get; } = translation;
    public TranslationStatus Status { get; } = status;
    public string? Error { get; } = error;
}

/// <summary>
/// Drives stage 1 (during-the-meeting) live transcription: buffers raw samples from
/// <see cref="MeetingRecorder.SamplesAvailable"/> per source into a
/// <see cref="StreamingWhisperSession"/>, and flushes each session through the shared
/// fast model whenever it signals it is due. Optionally also drives live English translation
/// of that same rough transcript (see the "Live translation" section below).
/// </summary>
/// <remarks>
/// <para>
/// All samples - mic and system alike - are funneled through one bounded channel and
/// processed by a single consumer loop, deliberately serializing every whisper.cpp
/// transcribe call. Whisper.net/whisper.cpp's thread-safety for two <c>WhisperProcessor</c>
/// instances sharing one <c>WhisperFactory</c> running concurrently on the same GPU context is
/// not documented, and there is no throughput reason to risk it: this machine has one iGPU,
/// so "concurrent" transcription would only contend for the same hardware anyway.
/// </para>
/// <para>
/// <b>Live translation.</b> When constructed with <c>translateToEnglish: true</c>, every flushed
/// chunk whose transcribe-pass detected language (<see cref="TranscriptionResult.DetectedLanguage"/>,
/// reused - never re-detected) is not already English is also handed to
/// <see cref="WhisperTranscriber.TranslateSamplesAsync"/> - a second decode of the exact same
/// audio, via whisper.cpp's own <c>translate</c> task (English-only; there is no
/// translate-to-anything-else in whisper.cpp). This runs on its own dedicated background worker,
/// reading from a small bounded queue, never inline with the transcribe consumer loop above:
/// <see cref="SegmentReady"/> fires immediately once a chunk is transcribed, and translation
/// fills in later via <see cref="TranslationReady"/>, whenever it is ready. If the translator
/// falls behind (its queue is full when a new chunk arrives), the oldest still-queued chunk is
/// dropped - never audio, never a transcript segment, only the option to also see it in English -
/// and its lines are reported via <see cref="TranslationReady"/> with
/// <see cref="TranslationStatus.Skipped"/> so the UI can show that visibly rather than leaving
/// them stuck on "Pending" forever. The actual translate decode call still goes through the same
/// <see cref="WhisperTranscriber"/>-owned native-call gate the transcribe call does (see
/// <see cref="WhisperTranscriber"/>'s own remarks on why), so the two calls take turns rather than
/// truly running concurrently - this is the "roughly double the live transcription cost" budget
/// the live-translation feature was designed against, not genuine parallel decoding.
/// </para>
/// </remarks>
public sealed class LiveTranscriptionEngine : IAsyncDisposable
{
    /// <summary>
    /// How many chunks' worth of translation work may sit queued (beyond the one currently being
    /// translated) before the oldest queued one is dropped to make room. Small on purpose: a
    /// backlog here means translation is falling behind live audio, and the point of dropping is
    /// to let it catch back up rather than grow an ever-larger queue of increasingly stale English
    /// text nobody asked to wait for.
    /// </summary>
    private const int TranslationQueueCapacity = 2;

    /// <summary>
    /// Upper bound on how long <see cref="FlushAllAsync"/> waits for already-queued translations
    /// to finish before giving up on the rest. Bounded so a meeting Stop can never hang
    /// indefinitely on the secondary, best-effort translation pass - a few chunks at "roughly
    /// double" the transcribe budget is expected; anything past this is treated as "won't finish
    /// in reasonable time" and the session moves on with whatever translations did land.
    /// </summary>
    private static readonly TimeSpan TranslationDrainTimeout = TimeSpan.FromSeconds(45);

    private readonly WhisperTranscriber _transcriber;
    private readonly Channel<(AudioSourceKind Source, short[] Samples)> _channel;
    private readonly Dictionary<AudioSourceKind, StreamingWhisperSession> _sessions;
    private readonly Dictionary<AudioSourceKind, double> _cumulativeSeconds;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, TranscriptLine> _ledger = new();
    private long _nextLineId;
    private bool _disposed;

    /// <summary>Non-null only when this engine was constructed with translation enabled.</summary>
    private readonly Channel<TranslationWorkItem>? _translationChannel;
    private readonly Task? _translationWorkerTask;

    public event EventHandler<LiveSegmentEventArgs>? SegmentReady;

    /// <summary>Raised only when this engine was constructed with translation enabled - see the class remarks.</summary>
    public event EventHandler<LiveTranslationEventArgs>? TranslationReady;

    public event EventHandler<Exception>? EngineError;

    /// <param name="translateToEnglish">
    /// Off by default. When true, every non-English chunk is also queued for a live English
    /// translation pass - see the class remarks. When false, no translation channel/worker is even
    /// created, so this feature costs nothing beyond one extra constructor parameter when unused.
    /// </param>
    public LiveTranscriptionEngine(
        WhisperTranscriber transcriber,
        IEnumerable<AudioSourceKind> enabledSources,
        bool translateToEnglish = false)
    {
        ArgumentNullException.ThrowIfNull(transcriber);
        _transcriber = transcriber;

        _channel = Channel.CreateUnbounded<(AudioSourceKind, short[])>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _sessions = [];
        _cumulativeSeconds = [];
        foreach (var source in enabledSources.Distinct())
        {
            _sessions[source] = transcriber.CreateStreamingSession();
            _cumulativeSeconds[source] = 0.0;
        }

        _consumerTask = Task.Run(() => ConsumeLoopAsync(_cts.Token));

        if (translateToEnglish)
        {
            // FullMode = Wait (never auto-drops) is deliberate: eviction is done explicitly by
            // EnqueueTranslation below so the dropped chunk's lines can be reported via
            // TranslationReady(..., TranslationStatus.Skipped) instead of vanishing silently.
            _translationChannel = Channel.CreateBounded<TranslationWorkItem>(new BoundedChannelOptions(TranslationQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            _translationWorkerTask = Task.Run(() => TranslationWorkerLoopAsync(_cts.Token));
        }
    }

    /// <summary>Enqueues one block of raw 16kHz mono PCM16 samples for <paramref name="source"/>. Never blocks.</summary>
    public void Enqueue(AudioSourceKind source, short[] samples)
    {
        if (_disposed || !_sessions.ContainsKey(source))
        {
            return;
        }

        // TryWrite on an unbounded channel never blocks and never fails except when the
        // writer side has already been completed (meeting stop in progress) - dropping
        // a straggling block in that narrow race is correct, not a bug.
        _channel.Writer.TryWrite((source, samples));
    }

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (source, samples) in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_sessions.TryGetValue(source, out var session))
                {
                    continue;
                }

                session.AppendSamples(ToFloat(samples));

                if (session.MustFlush || session.ShouldFlush)
                {
                    await FlushAsync(source, session, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path (DisposeAsync cancels _cts).
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, ex);
        }
    }

    private async Task FlushAsync(AudioSourceKind source, StreamingWhisperSession session, CancellationToken ct)
    {
        var baseOffsetSeconds = _cumulativeSeconds[source];

        StreamingFlushResult flushResult;
        try
        {
            flushResult = await session.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EngineError?.Invoke(this, ex);
            return;
        }

        var result = flushResult.Transcription;
        _cumulativeSeconds[source] = baseOffsetSeconds + result.AudioDuration.TotalSeconds;
        var baseOffset = TimeSpan.FromSeconds(baseOffsetSeconds);

        // Skip the translate pass entirely when this chunk's own (already-computed) detected
        // language is English - translating English to English would just burn a second decode
        // for no benefit. "unknown"/blank (no speech resolved a language at all) is treated the
        // same as English here: nothing worth translating either way.
        var sourceLanguage = result.DetectedLanguage;
        var chunkNeedsTranslation = _translationChannel is not null
            && !string.IsNullOrWhiteSpace(sourceLanguage)
            && !string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sourceLanguage, "unknown", StringComparison.OrdinalIgnoreCase);

        List<TranslationLineSpan>? lineSpans = chunkNeedsTranslation ? [] : null;

        foreach (var segment in result.Segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var lineId = Interlocked.Increment(ref _nextLineId);
            var translationStatus = chunkNeedsTranslation ? TranslationStatus.Pending : TranslationStatus.NotApplicable;

            var line = new TranscriptLine(
                source,
                baseOffset + segment.Start,
                baseOffset + segment.End,
                text,
                segment.Language,
                segment.Probability)
            {
                Id = lineId,
                TranslationStatus = translationStatus,
            };

            _ledger[lineId] = line;
            lineSpans?.Add(new TranslationLineSpan(lineId, segment.Start, segment.End));

            SegmentReady?.Invoke(this, new LiveSegmentEventArgs(line));
        }

        if (lineSpans is { Count: > 0 })
        {
            EnqueueTranslation(new TranslationWorkItem(source, flushResult.Samples, sourceLanguage, lineSpans));
        }
    }

    // ----- Live translation -----------------------------------------------------------------

    private readonly record struct TranslationLineSpan(long LineId, TimeSpan Start, TimeSpan End);

    private readonly record struct TranslationWorkItem(
        AudioSourceKind Source,
        ReadOnlyMemory<float> Samples,
        string SourceLanguage,
        IReadOnlyList<TranslationLineSpan> Lines);

    /// <summary>Never blocks the caller (the main transcribe consumer loop). See <see cref="TranslationQueueCapacity"/>.</summary>
    private void EnqueueTranslation(TranslationWorkItem item)
    {
        var channel = _translationChannel;
        if (channel is null)
        {
            return;
        }

        while (!channel.Writer.TryWrite(item))
        {
            // Queue is full. Evict the oldest queued (not yet started) item ourselves - rather
            // than relying on an invisible drop-oldest channel mode - so its lines can be reported
            // as Skipped instead of just vanishing. If TryRead fails here, the worker task raced
            // us and already dequeued the head item for processing; the queue has room again and
            // the next loop iteration's TryWrite will simply succeed with nothing to report.
            if (channel.Reader.TryRead(out var evicted))
            {
                ReportSkipped(evicted);
            }
        }
    }

    private void ReportSkipped(TranslationWorkItem evicted)
    {
        foreach (var span in evicted.Lines)
        {
            RaiseTranslationReady(
                span.LineId,
                evicted.Source,
                translation: null,
                TranslationStatus.Skipped,
                "Translator fell behind live audio; this chunk was skipped rather than delay transcription.");
        }
    }

    private async Task TranslationWorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _translationChannel!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await ProcessTranslationItemAsync(item, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            // Not expected - ProcessTranslationItemAsync already catches its own decode failures -
            // but translation must never take the whole engine down with it if something
            // unforeseen slips past that.
            EngineError?.Invoke(this, ex);
        }
    }

    private async Task ProcessTranslationItemAsync(TranslationWorkItem item, CancellationToken ct)
    {
        IReadOnlyList<TranscriptionSegment> translated;
        try
        {
            translated = await _transcriber.TranslateSamplesAsync(item.Samples, item.SourceLanguage, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Non-fatal by design: surface the failure on every line this chunk would have
            // translated, and move on to the next queued chunk. A bad translate call must never
            // take down transcription or the rest of the meeting.
            foreach (var span in item.Lines)
            {
                RaiseTranslationReady(span.LineId, item.Source, translation: null, TranslationStatus.Failed, ex.Message);
            }

            return;
        }

        foreach (var span in item.Lines)
        {
            var text = BestOverlapText(translated, span.Start, span.End);
            RaiseTranslationReady(span.LineId, item.Source, text, TranslationStatus.Completed, error: null);
        }
    }

    /// <summary>
    /// Picks the translated segment whose time range overlaps <paramref name="start"/>-<paramref name="end"/>
    /// the most and returns its text. Needed because the translate pass decodes the whole chunk in
    /// one call and can produce different segment boundaries than the transcribe pass did for the
    /// same audio (different token stream, since it is a different task) - there is no guaranteed
    /// 1:1 mapping between "one transcribed line" and "one translated segment". Falls back to the
    /// translated segment starting closest to <paramref name="start"/> when nothing overlaps at
    /// all (e.g. the translate pass merged everything into fewer, longer segments). Returns an
    /// empty string, never null, when the chunk produced no translated segments at all - a
    /// legitimate "translation completed, nothing to show" outcome, not a failure.
    /// </summary>
    private static string BestOverlapText(IReadOnlyList<TranscriptionSegment> translated, TimeSpan start, TimeSpan end)
    {
        if (translated.Count == 0)
        {
            return string.Empty;
        }

        TranscriptionSegment? best = null;
        var bestOverlap = TimeSpan.Zero;

        foreach (var candidate in translated)
        {
            var overlapStart = candidate.Start > start ? candidate.Start : start;
            var overlapEnd = candidate.End < end ? candidate.End : end;
            var overlap = overlapEnd > overlapStart ? overlapEnd - overlapStart : TimeSpan.Zero;

            if (best is null || overlap > bestOverlap)
            {
                best = candidate;
                bestOverlap = overlap;
            }
        }

        if (bestOverlap > TimeSpan.Zero)
        {
            return best!.Text.Trim();
        }

        var nearest = translated.OrderBy(s => Math.Abs((s.Start - start).Ticks)).First();
        return nearest.Text.Trim();
    }

    /// <summary>Updates the in-memory ledger (for <see cref="GetSnapshot"/>) and raises <see cref="TranslationReady"/>.</summary>
    private void RaiseTranslationReady(long lineId, AudioSourceKind source, string? translation, TranslationStatus status, string? error)
    {
        _ledger.AddOrUpdate(
            lineId,
            addValueFactory: static _ => throw new InvalidOperationException(
                "RaiseTranslationReady called for a line id that was never recorded via FlushAsync."),
            updateValueFactory: (_, existing) => existing with
            {
                Translation = translation,
                TranslationStatus = status,
                TranslationError = error,
            });

        TranslationReady?.Invoke(this, new LiveTranslationEventArgs(lineId, source, translation, status, error));
    }

    /// <summary>
    /// Every line produced so far, in chronological order, reflecting whatever translation state
    /// each one is currently in (Completed/Pending/Skipped/Failed/NotApplicable - see
    /// <see cref="TranslationStatus"/>). Callers that want translations included should call this
    /// after <see cref="FlushAllAsync"/> has returned, so pending translations had a chance to
    /// land first.
    /// </summary>
    public IReadOnlyList<TranscriptLine> GetSnapshot() => [.. _ledger.Values.OrderBy(l => l.Start)];

    /// <summary>
    /// Stops accepting new samples, drains everything already queued through the normal
    /// per-threshold flush path, then force-flushes each session's remaining partial
    /// buffer so no trailing seconds of live audio are silently lost. Call this once,
    /// right after the recorder stops, before disposing.
    /// </summary>
    /// <remarks>
    /// If translation is enabled, this also gives already-queued (or just-enqueued-by-the-final-
    /// flush) translations up to <see cref="TranslationDrainTimeout"/> to finish, so
    /// <see cref="GetSnapshot"/> reflects them. This is a bounded wait, not a hard requirement -
    /// translation is best-effort by design (see the class remarks), so a Stop can never hang
    /// indefinitely on it. Whatever has not completed by the timeout stays at whatever status it
    /// was last in (typically <see cref="TranslationStatus.Pending"/>).
    /// </remarks>
    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        _channel.Writer.TryComplete();

        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected if DisposeAsync races this call.
        }

        foreach (var (source, session) in _sessions)
        {
            await FlushAsync(source, session, ct).ConfigureAwait(false);
        }

        if (_translationChannel is not null && _translationWorkerTask is not null)
        {
            _translationChannel.Writer.TryComplete();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TranslationDrainTimeout);

            try
            {
                await _translationWorkerTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // TranslationDrainTimeout elapsed, not caller cancellation - proceed with
                // whatever translations landed in time; see this method's remarks.
                Trace.TraceWarning(
                    $"LiveTranscriptionEngine.FlushAllAsync: translation did not finish draining within {TranslationDrainTimeout}; continuing without waiting further.");
            }
        }
    }

    private static float[] ToFloat(short[] samples)
    {
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            result[i] = samples[i] / 32768f;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _channel.Writer.TryComplete();
        _translationChannel?.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Mid-run failures inside ConsumeLoopAsync are already reported via EngineError, so
            // this catch normally only guards the final await during teardown -- but if
            // something still surfaces here it must not vanish silently. Trace.TraceWarning (not
            // Debug.WriteLine) so this survives in Release builds, where [Conditional("DEBUG")]
            // would otherwise strip it. Do not rethrow: nothing left to salvage out of Dispose.
            Trace.TraceWarning(
                $"LiveTranscriptionEngine.DisposeAsync: consumer task faulted during shutdown: {ex}");
        }

        if (_translationWorkerTask is not null)
        {
            try
            {
                await _translationWorkerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Same rationale as the transcribe consumer task's catch above.
                Trace.TraceWarning(
                    $"LiveTranscriptionEngine.DisposeAsync: translation worker faulted during shutdown: {ex}");
            }
        }

        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}
