using System.Diagnostics;
using System.Runtime.CompilerServices;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace MeetingScribe.Whisper;

/// <summary>
/// Whisper.net-backed transcriber. Loads one ggml model via the Whisper.net native
/// runtime loader (Vulkan on Windows, Metal/CPU on macOS - see
/// <c>MeetingScribe.Whisper.csproj</c> for the host-OS-conditional runtime package and
/// <see cref="Backend"/> for proof of what actually loaded on this machine) and drives
/// both whole-file and streaming/chunked transcription through it.
/// </summary>
public sealed class WhisperTranscriber : IAsyncDisposable
{
    private readonly WhisperTranscriberOptions _options;
    private readonly WhisperFactory _factory;
    private readonly IDisposable _logSubscription;
    private readonly List<string> _nativeLogLines;
    private readonly object _logLock = new();
    private bool _disposed;

    /// <summary>
    /// Serializes every native whisper.cpp decode call this transcriber makes - the main
    /// transcribe processor (<see cref="TranscribeSamplesAsync"/>, used by both
    /// <see cref="TranscribeFileAsync"/> and every <see cref="StreamingWhisperSession"/>) and the
    /// lazily-built translate processor (<see cref="TranslateSamplesAsync"/>) alike. Whisper.net/
    /// whisper.cpp's thread-safety for two <c>WhisperProcessor</c> instances built from one
    /// <c>WhisperFactory</c> running concurrently on the same GPU context is not documented (see
    /// <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>'s class remarks for the
    /// same concern applied to mic/system audio), and there is no throughput reason to risk it:
    /// this machine has one iGPU, so two "concurrent" decode calls would only contend for the
    /// same hardware anyway. Live translation deliberately runs on its own background worker
    /// (never inline with transcription) so it cannot delay emitting the original transcript
    /// line, but the two decode calls themselves still take turns through this gate rather than
    /// truly overlapping - the live translation design's "roughly double the live transcription
    /// cost" budget already accounts for this serialization, not for genuine parallelism.
    /// </summary>
    private readonly SemaphoreSlim _nativeCallGate = new(1, 1);

    /// <summary>
    /// Lazily-built, reused for the transcriber's whole lifetime: one whisper.cpp decode context
    /// permanently configured for the translate task (see <see cref="TranslateSamplesAsync"/>).
    /// Built lazily (rather than eagerly in <see cref="CreateAsync"/>) so a transcriber that never
    /// translates (live translation off, or the stage-2 accurate-pass transcriber, which never
    /// calls <see cref="TranslateSamplesAsync"/> at all) never pays for a second decode context.
    /// </summary>
    private WhisperProcessor? _translateProcessor;
    private readonly object _translateProcessorLock = new();

    /// <summary>
    /// Proof of which native backend loaded and ran at construction time. See
    /// <see cref="BackendInfo"/> for why each field matters — this is the guard against a
    /// silent CPU fallback.
    /// </summary>
    public BackendInfo Backend { get; }

    private WhisperTranscriber(
        WhisperTranscriberOptions options,
        WhisperFactory factory,
        IDisposable logSubscription,
        List<string> nativeLogLines,
        BackendInfo backend)
    {
        _options = options;
        _factory = factory;
        _logSubscription = logSubscription;
        _nativeLogLines = nativeLogLines;
        Backend = backend;
    }

    /// <summary>
    /// Resolves (downloading if needed) the configured model, loads it, and captures which
    /// native backend Whisper.net's loader actually selected.
    /// </summary>
    public static async Task<WhisperTranscriber> CreateAsync(
        WhisperTranscriberOptions options,
        IProgress<double>? modelDownloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var nativeLogLines = new List<string>();
        var logLock = new object();

        // Subscribed before FromPath() so it also captures native log lines emitted during
        // model/context load (where whisper.cpp's Vulkan device enumeration happens).
        var logSubscription = LogProvider.AddLogger((level, message) =>
        {
            lock (logLock)
            {
                nativeLogLines.Add($"[{level}] {message}");
            }
        });

        WhisperFactory factory;
        try
        {
            var modelPath = await ModelManager.EnsureModelAsync(
                    options.ModelSize, options.ModelCacheDirectory, modelDownloadProgress, cancellationToken)
                .ConfigureAwait(false);

            var factoryOptions = new WhisperFactoryOptions
            {
                UseGpu = options.UseGpu,
                GpuDevice = options.GpuDevice,
                UseFlashAttention = options.UseFlashAttention,
            };

            factory = WhisperFactory.FromPath(modelPath, factoryOptions);
        }
        catch
        {
            logSubscription.Dispose();
            throw;
        }

        string runtimeInfo;
        try
        {
            // GetRuntimeInfo is static: it reports on the process-wide loaded native
            // library, not on this particular factory instance.
            runtimeInfo = WhisperFactory.GetRuntimeInfo() ?? "<empty>";
        }
        catch (Exception ex)
        {
            // GetRuntimeInfo talks to the native library; keep going with a placeholder
            // rather than failing transcriber creation over a diagnostics call.
            runtimeInfo = $"<unavailable: {ex.Message}>";
        }

        var loadedLibrary = RuntimeOptions.LoadedLibrary;
        var loadedLibraryName = loadedLibrary?.ToString() ?? "Unknown";
        var isGpuBackend = loadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12
            or RuntimeLibrary.CoreML or RuntimeLibrary.OpenVino;

        List<string> logSnapshot;
        lock (logLock)
        {
            logSnapshot = [.. nativeLogLines];
        }

        // Whisper.net's RuntimeLibrary enum has no "Metal" member: on macOS/iOS, Metal support
        // is compiled directly into the same native library RuntimeOptions.LoadedLibrary reports
        // as Cpu (confirmed by decompiling Whisper.net.dll 1.9.1 - the RuntimeLibrary enum only
        // has Cpu/Vulkan/Cuda/Cuda12/CoreML/OpenVino/NoAvx, no Metal case at all; WhisperFactory.
        // GetRuntimeInfo()'s "MTL : EMBED_LIBRARY = 1" flag is the only static signal). Left
        // unchecked, a Mac that is genuinely decoding on the GPU via Metal would be reported as
        // CPU-only by this type - the same silent-misreport risk BackendInfo exists to catch,
        // just inverted (looks like CPU, is actually GPU). "ggml_metal_device_init: GPU name:"
        // appearing in the captured native log lines is runtime proof Metal actually initialized
        // (not just that the binary was built with Metal support), the same evidentiary standard
        // already applied to the Vulkan device-enumeration lines above.
        var metalDeviceLine = logSnapshot.FirstOrDefault(
            l => l.Contains("ggml_metal_device_init", StringComparison.Ordinal)
                 && l.Contains("GPU name", StringComparison.Ordinal));

        if (loadedLibrary == RuntimeLibrary.Cpu && metalDeviceLine is not null)
        {
            loadedLibraryName = "Metal";
            isGpuBackend = true;
        }

        var backend = new BackendInfo(loadedLibraryName, isGpuBackend, runtimeInfo, logSnapshot);

        return new WhisperTranscriber(options, factory, logSubscription, nativeLogLines, backend);
    }

    /// <summary>Snapshot of every native log line captured since this transcriber was created.</summary>
    public IReadOnlyList<string> GetNativeLogSnapshot()
    {
        lock (_logLock)
        {
            return [.. _nativeLogLines];
        }
    }

    /// <summary>
    /// Transcribes a WAV file, streaming segments to the caller as they are decoded. Accepts
    /// standard PCM/IEEE-float WAV at any sample rate/channel count/bit depth — see
    /// <see cref="WavAudioLoader"/> — decoding and resampling to 16kHz mono happen before
    /// whisper.cpp ever sees the audio, so the stream does not need to be seekable and does
    /// not need to already be 16kHz.
    /// </summary>
    public async IAsyncEnumerable<TranscriptionSegment> TranscribeFileSegmentsAsync(
        Stream wavStream,
        IProgress<int>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavStream);

        var (samples, _) = await WavAudioLoader.LoadMono16kAsync(wavStream, cancellationToken).ConfigureAwait(false);

        using var processor = BuildProcessor(progress);

        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
        {
            yield return Map(segment);
        }
    }

    /// <summary>
    /// Length whisper.cpp windows are targeted/capped to when <see cref="TranscribeFileAsync"/>
    /// internally chunks a file. Deliberately the same constants
    /// <see cref="StreamingWhisperSession"/> already uses for live capture (proven on the same
    /// hallucination-loop failure: many short <c>ProcessAsync</c> calls instead of one call over
    /// the whole file, so <c>no_context</c> resets prompt history often enough that one bad
    /// window cannot poison the rest) rather than a second, independently-tunable pair of
    /// numbers that could drift out of sync with the streaming path.
    /// </summary>
    private const double FileChunkTargetSeconds = StreamingWhisperSession.TargetChunkSeconds;
    private const double FileChunkMaxSeconds = StreamingWhisperSession.MaxChunkSeconds;
    private const double FileChunkMinTailSeconds = StreamingWhisperSession.MinFlushSeconds;

    /// <summary>
    /// How many speech-like chunks (see <see cref="AudioChunkPlanner.SelectSpeechLikeChunkIndices"/>)
    /// to sample for the initial-language seed in <see cref="TranscribeFileAsync"/>. This is only
    /// a starting point, not a lock — see the per-chunk switching described in that method's
    /// remarks. One sample is not enough even for a seed: measured on the real meeting recording
    /// this was built against, the first speech-like chunk alone detects as English (a brief
    /// aside), while the file is Japanese-dominant overall. 7 independent samples resolved to the
    /// correct majority language (ja, 4/7) on that recording; kept as a named constant since it is
    /// a real, measured tuning choice, not an arbitrary guess.
    /// </summary>
    private const int LanguageIdentificationSampleCount = 7;

    /// <summary>
    /// Transcribes a WAV file and returns the aggregated result: full text, detected
    /// language, timing and the realtime factor. See <see cref="TranscribeFileSegmentsAsync"/>
    /// for the accepted input formats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internally splits the audio into <see cref="FileChunkTargetSeconds"/>-<see cref="FileChunkMaxSeconds"/>-second
    /// chunks (see <see cref="AudioChunkPlanner"/>) and issues one <c>ProcessAsync</c> call per
    /// chunk, mirroring <see cref="StreamingWhisperSession"/>'s proven call pattern, instead of
    /// one call over the whole file. A single whole-file call reproduced whisper.cpp's
    /// hallucination-loop failure mode on a real 26-minute meeting recording: 373 of 513
    /// segments (72.7%) were a single repeated phrase, because whisper.cpp's per-window context
    /// rebuild (whisper.cpp:7627-7638) keeps re-seeding a wrong phrase once the decoder latches
    /// onto one, and <c>no_context</c> only clears prompt history once at the start of the
    /// whole call. Many short calls means <c>no_context</c> resets often enough that one bad
    /// chunk cannot poison the ones after it. Segment timestamps in the returned result are
    /// absolute (offset by each chunk's start position within the file), not relative to their
    /// own chunk, so downstream consumers (VTT/JSON writers, track merging) do not need to know
    /// chunking happened at all.
    /// </para>
    /// <para>
    /// When <see cref="WhisperTranscriberOptions.SkipSilentLeadIn"/> is true (default), a
    /// near-silent run of chunks at the very start of the file — room tone before anyone starts
    /// speaking — is never sent to whisper.cpp at all: see
    /// <see cref="AudioChunkPlanner.CountLeadingSilentChunks"/> for why this is necessary
    /// (whisper.cpp's own no-speech guard does not catch it: it reports
    /// <c>no_speech_probability = 0.000</c> on this failure mode) and why it is deliberately
    /// lead-in-only rather than a file-wide silence gate. Skipped chunks still advance
    /// <paramref name="progress"/> and do not affect later chunks' absolute timestamps, since
    /// each chunk's offset comes from its own position in <paramref name="wavStream"/>, not from
    /// a running count of chunks actually decoded.
    /// </para>
    /// <para>
    /// Language handling (when <see cref="WhisperTranscriberOptions.AutoDetectLanguage"/> is
    /// true): the file's active decode language is not locked once for the whole file. A seed
    /// language is picked from a majority vote over several speech-like chunks (see
    /// <see cref="LanguageIdentificationSampleCount"/>), restricted to
    /// <see cref="WhisperTranscriberOptions.AllowedLanguages"/> when set, but that seed is only a
    /// starting point. From there, each chunk gets its own fast language-ID pass (not a full
    /// decode), and the active language switches whenever
    /// <see cref="WhisperTranscriberOptions.LanguageSwitchConfirmationChunks"/> chunks in a row
    /// agree on the same new in-set language — confirmed by looking ahead, so a genuine sustained
    /// switch takes effect starting at the first chunk of the new language, not one or more chunks
    /// late. A detection outside the allowed set never triggers a switch (this is what stopped the
    /// historical "ko"/"nn" noise flapping) but is still counted per language on
    /// <see cref="TranscriptionResult.OutOfSetLanguageDetections"/>, so a language that actually
    /// needs adding to the allowed set is visible instead of being silently mistranscribed as one
    /// of the allowed languages forever. Every confirmed switch is recorded on
    /// <see cref="TranscriptionResult.LanguageSwitches"/> with its timestamp.
    /// </para>
    /// <para>
    /// <b>Hard limit, not a bug:</b> whisper.cpp assigns exactly one language to an entire decode
    /// window (one chunk, target 15s / cap 25s here). This scheme changes language at chunk
    /// boundaries, so it handles passage-level code-switching (a few minutes of Japanese, then a
    /// stretch of Mandarin interpreting, etc.). It cannot fix intra-sentence mixing within a single
    /// chunk — this is measured on real recordings, not hypothetical. An illustrative example of
    /// the shape (constructed, not a transcript excerpt): 「所以我們的前提是プランA」or
    /// 「因為預算はもう決まっている」, mixing Chinese and Japanese inside one utterance. A chunk
    /// containing that kind of switch decodes entirely in whichever language wins the chunk; the
    /// other language's words inside it are transcribed as best-effort noise in the winning
    /// language, not fixed by anything in this method.
    /// </para>
    /// </remarks>
    public async Task<TranscriptionResult> TranscribeFileAsync(
        Stream wavStream,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(wavStream);

        var (samples, audioDuration) = await WavAudioLoader.LoadMono16kAsync(wavStream, cancellationToken)
            .ConfigureAwait(false);

        var chunks = AudioChunkPlanner.PlanChunks(
            samples,
            FileChunkTargetSeconds,
            FileChunkMaxSeconds,
            FileChunkMinTailSeconds,
            WavAudioLoader.TargetSampleRateHz);

        // How many chunks at the start of the file to skip as a near-silent lead-in (see
        // AudioChunkPlanner.CountLeadingSilentChunks remarks for why this is lead-in-only, not a
        // file-wide gate). 0 when the option is disabled or there is no quiet lead-in to trim.
        var leadingSilentChunkCount = _options.SkipSilentLeadIn
            ? AudioChunkPlanner.CountLeadingSilentChunks(
                AudioChunkPlanner.ComputeChunkRms(samples, chunks), _options.SilentLeadInRatio)
            : 0;

        // Progress is reported per chunk below (BuildProcessor's own progress handler reports
        // 0-100% *within* a single ProcessAsync call, which is not meaningful across many
        // chunked calls), so no progress callback is wired into the processor itself.
        using var processor = BuildProcessor(progress: null);
        var stopwatch = Stopwatch.StartNew();

        var autoDetect = _options.AutoDetectLanguage && chunks.Count > 0;

        if (autoDetect && _options.LanguageSwitchConfirmationChunks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WhisperTranscriberOptions.LanguageSwitchConfirmationChunks),
                _options.LanguageSwitchConfirmationChunks,
                "Must be >= 1.");
        }

        // Case-insensitive membership set for the configured allowed-language restriction. Null
        // (both when the option is null and when it is an empty collection) means unrestricted -
        // see WhisperTranscriberOptions.AllowedLanguages.
        var allowedLanguages = _options.AllowedLanguages is { Count: > 0 } configured
            ? new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase)
            : null;

        // Memoizes each chunk's standalone language-ID result (see GetChunkLanguage) so neither
        // the seed vote below nor the per-chunk look-ahead confirmation in the main loop ever
        // re-runs whisper.cpp's LID pass on the same chunk's samples twice.
        var lidCache = new Dictionary<int, (string? Language, float Probability)>();

        // Detected-but-rejected languages: recorded, not discarded, so a language that actually
        // needs adding to WhisperTranscriberOptions.AllowedLanguages is visible on the result
        // instead of being silently mistranscribed as one of the allowed languages forever.
        var outOfSetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var languageSwitches = new List<LanguageSwitchEvent>();

        // Seed only - not a lock. Detect on several real-speech chunks up front (skipping the
        // quiet opening), majority-vote restricted to the allowed set, and use that as the
        // starting active language. The per-chunk loop below is what actually decides each
        // chunk's language from here on; this just gives it a sane starting point instead of
        // letting the very first chunk (which can be an unrepresentative aside, or land in the
        // near-silent lead-in's "nn"-style noise) decide the whole file.
        string? activeLanguage = null;
        if (autoDetect)
        {
            var candidateIndices = AudioChunkPlanner.SelectSpeechLikeChunkIndices(
                samples, chunks, LanguageIdentificationSampleCount);
            activeLanguage = DetectDominantLanguage(
                processor, samples, chunks, candidateIndices, allowedLanguages, lidCache, outOfSetCounts);
            if (activeLanguage is not null)
            {
                processor.ChangeLanguage(activeLanguage);
            }
        }

        var segments = new List<TranscriptionSegment>();
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i < leadingSilentChunkCount)
            {
                // Near-silent lead-in chunk: never sent to whisper.cpp, so it emits no segments.
                // Progress still advances so the caller sees a monotonic 0-100% regardless of
                // how many leading chunks were skipped; later chunks' absolute timestamps are
                // unaffected since each one is computed from its own Start below, not from a
                // running count of chunks actually decoded.
                progress?.Report((int)Math.Round((i + 1) / (double)chunks.Count * 100.0));
                continue;
            }

            var (start, length) = chunks[i];
            var chunkOffset = TimeSpan.FromSeconds(start / (double)WavAudioLoader.TargetSampleRateHz);

            if (autoDetect)
            {
                var resolvedLanguage = ResolveChunkLanguage(
                    processor, samples, chunks, i, activeLanguage, allowedLanguages,
                    _options.LanguageSwitchConfirmationChunks, lidCache, outOfSetCounts);

                if (!string.Equals(resolvedLanguage, activeLanguage, StringComparison.OrdinalIgnoreCase)
                    && resolvedLanguage is not null)
                {
                    languageSwitches.Add(new LanguageSwitchEvent(chunkOffset, activeLanguage ?? "unknown", resolvedLanguage));
                    activeLanguage = resolvedLanguage;
                    processor.ChangeLanguage(activeLanguage);
                }
            }

            var chunkResult = await TranscribeSamplesAsync(processor, samples.AsMemory(start, length), cancellationToken)
                .ConfigureAwait(false);

            foreach (var segment in chunkResult.Segments)
            {
                segments.Add(segment with { Start = chunkOffset + segment.Start, End = chunkOffset + segment.End });
            }

            progress?.Report((int)Math.Round((i + 1) / (double)chunks.Count * 100.0));
        }

        stopwatch.Stop();

        return BuildResult(segments, audioDuration, stopwatch.Elapsed, languageSwitches, outOfSetCounts);
    }

    /// <summary>
    /// Runs whisper.cpp's standalone language-ID call (not a full decode, see
    /// <see cref="GetChunkLanguage"/>) on each of <paramref name="candidateIndices"/> and returns
    /// the in-set language with the most votes, ties broken by summed detection probability.
    /// Candidates outside <paramref name="allowedLanguages"/> are counted into
    /// <paramref name="outOfSetCounts"/> and excluded from voting - the seed is restricted to the
    /// allowed set the same way the per-chunk switching later is, so it never hands the file a
    /// starting language the rest of the pipeline would immediately reject. Null when every
    /// candidate chunk is out-of-set or returns a null/empty language (native call failure), in
    /// which case the caller falls back to whatever <see cref="WhisperProcessorBuilder.WithLanguageDetection"/>
    /// would have done per-chunk on its own.
    /// </summary>
    private static string? DetectDominantLanguage(
        WhisperProcessor processor,
        float[] samples,
        IReadOnlyList<(int Start, int Length)> chunks,
        IReadOnlyList<int> candidateIndices,
        HashSet<string>? allowedLanguages,
        Dictionary<int, (string? Language, float Probability)> lidCache,
        Dictionary<string, int> outOfSetCounts)
    {
        var votes = new Dictionary<string, (int Count, double ProbabilitySum)>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in candidateIndices)
        {
            var (language, probability) = GetChunkLanguage(processor, samples, chunks, index, lidCache);

            if (language is null)
            {
                continue;
            }

            if (allowedLanguages is not null && !allowedLanguages.Contains(language))
            {
                outOfSetCounts[language] = outOfSetCounts.GetValueOrDefault(language) + 1;
                continue;
            }

            var current = votes.TryGetValue(language, out var existing) ? existing : (0, 0.0);
            votes[language] = (current.Item1 + 1, current.Item2 + probability);
        }

        if (votes.Count == 0)
        {
            return null;
        }

        return votes
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.ProbabilitySum)
            .First().Key;
    }

    /// <summary>
    /// Decides what language chunk <paramref name="index"/> should be decoded in, given the
    /// currently <paramref name="activeLanguage"/>. Runs chunk <paramref name="index"/>'s own
    /// language-ID pass (see <see cref="GetChunkLanguage"/>) and, only when that disagrees with
    /// <paramref name="activeLanguage"/> and is in-set, looks ahead at the next
    /// <paramref name="confirmationChunks"/> - 1 chunks (also memoized) to confirm the candidate
    /// before committing to it. Confirming by looking ahead rather than waiting for the same
    /// language to repeat on the far side of a switch means a genuine sustained switch takes
    /// effect starting at chunk <paramref name="index"/> itself, at zero added lag - only an
    /// unconfirmed false alarm costs anything, and what it costs is one chunk staying on the old
    /// language, not delaying a real switch.
    /// </summary>
    /// <returns>
    /// The language chunk <paramref name="index"/> should be decoded in: <paramref name="activeLanguage"/>
    /// unchanged (no candidate, an out-of-set detection, or an unconfirmed candidate), a newly
    /// confirmed switch target, or null when there is no active language yet and this chunk's own
    /// detection did not resolve one either - in which case the caller leaves whisper.cpp's own
    /// per-call auto-detect (<see cref="WhisperProcessorBuilder.WithLanguageDetection"/>) to run
    /// unforced, exactly as it would without any of this logic.
    /// </returns>
    private static string? ResolveChunkLanguage(
        WhisperProcessor processor,
        float[] samples,
        IReadOnlyList<(int Start, int Length)> chunks,
        int index,
        string? activeLanguage,
        HashSet<string>? allowedLanguages,
        int confirmationChunks,
        Dictionary<int, (string? Language, float Probability)> lidCache,
        Dictionary<string, int> outOfSetCounts)
    {
        var (ownLanguage, _) = GetChunkLanguage(processor, samples, chunks, index, lidCache);

        if (ownLanguage is null)
        {
            // Native LID call itself failed to return anything for this chunk: keep whatever is
            // already active rather than treat silence/failure as a switch signal.
            return activeLanguage;
        }

        if (allowedLanguages is not null && !allowedLanguages.Contains(ownLanguage))
        {
            // Out-of-set noise (the historical ko/nn failure mode). Never switch to it, but do
            // not throw the detection away either - see WhisperTranscriberOptions.AllowedLanguages.
            outOfSetCounts[ownLanguage] = outOfSetCounts.GetValueOrDefault(ownLanguage) + 1;
            return activeLanguage;
        }

        if (string.Equals(ownLanguage, activeLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return activeLanguage;
        }

        // Candidate switch: in-set and different from what's active. Confirm by requiring the
        // next (confirmationChunks - 1) chunks to independently agree, looking ahead of index.
        for (var lookahead = 1; lookahead < confirmationChunks; lookahead++)
        {
            var confirmIndex = index + lookahead;
            if (confirmIndex >= chunks.Count)
            {
                // Not enough chunks left in the file to confirm; stay on the current language
                // rather than switch on a single unconfirmed detection this close to the end.
                return activeLanguage;
            }

            var (confirmLanguage, _) = GetChunkLanguage(processor, samples, chunks, confirmIndex, lidCache);
            if (!string.Equals(confirmLanguage, ownLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return activeLanguage;
            }
        }

        return ownLanguage;
    }

    /// <summary>
    /// Fast (LID-only, not a full decode) detected language for the chunk at
    /// <paramref name="index"/>, memoized in <paramref name="lidCache"/> so no chunk's audio is
    /// ever run through <see cref="WhisperProcessor.DetectLanguageWithProbability(float[])"/>
    /// more than once, regardless of how many times the seed vote and the per-chunk look-ahead
    /// confirmation both end up asking about the same chunk index.
    /// </summary>
    private static (string? Language, float Probability) GetChunkLanguage(
        WhisperProcessor processor,
        float[] samples,
        IReadOnlyList<(int Start, int Length)> chunks,
        int index,
        Dictionary<int, (string? Language, float Probability)> lidCache)
    {
        if (lidCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        var (start, length) = chunks[index];
        var chunkSamples = samples.AsSpan(start, length).ToArray();
        var (language, probability) = processor.DetectLanguageWithProbability(chunkSamples);
        var result = (string.IsNullOrEmpty(language) ? null : language, probability);
        lidCache[index] = result;
        return result;
    }

    /// <summary>
    /// Creates a streaming session that accumulates raw 16kHz mono float samples and flushes
    /// them through this transcriber's model in chunks. See <see cref="StreamingWhisperSession"/>
    /// for the accumulate/flush contract.
    /// </summary>
    public StreamingWhisperSession CreateStreamingSession(IProgress<int>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var processor = BuildProcessor(progress);
        return new StreamingWhisperSession(this, processor);
    }

    /// <summary>
    /// Runs one chunk of raw samples through an already-built processor. Used by
    /// <see cref="StreamingWhisperSession"/>; not exposed publicly because callers should go
    /// through the session so the accumulate/flush constants are respected.
    /// </summary>
    internal async Task<TranscriptionResult> TranscribeSamplesAsync(
        WhisperProcessor processor,
        ReadOnlyMemory<float> samples,
        CancellationToken cancellationToken)
    {
        var audioDuration = TimeSpan.FromSeconds(samples.Length / (double)StreamingWhisperSession.SampleRateHz);
        var segments = new List<TranscriptionSegment>();
        var stopwatch = Stopwatch.StartNew();

        await _nativeCallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                segments.Add(Map(segment));
            }
        }
        finally
        {
            _nativeCallGate.Release();
        }

        stopwatch.Stop();

        return BuildResult(segments, audioDuration, stopwatch.Elapsed);
    }

    /// <summary>
    /// Translates one chunk of raw 16kHz mono samples into English via whisper.cpp's own
    /// <c>translate</c> task (Whisper.net's <c>WithTranslate()</c>) - a second, independent decode
    /// of the same audio already handed to <see cref="TranscribeSamplesAsync"/>, not a text-level
    /// translation of its output. There is no translate-to-anything-else: whisper.cpp's translate
    /// task only ever produces English, which is the entire reason live translation in this app is
    /// English-only (see <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>).
    /// </summary>
    /// <param name="samples">Raw 16kHz mono float samples for one chunk (e.g. a <see cref="StreamingFlushResult.Samples"/>).</param>
    /// <param name="sourceLanguage">
    /// The language to translate <em>from</em> - whisper.cpp's translate task still needs a source
    /// language, it does not auto-detect it independently. Callers should reuse whatever language
    /// the transcribe pass already detected for this same chunk (its
    /// <see cref="TranscriptionResult.DetectedLanguage"/>) rather than paying for a second
    /// detection pass.
    /// </param>
    /// <returns>
    /// Decoded English segments, timestamped relative to the start of <paramref name="samples"/> -
    /// the same timeline <see cref="TranscribeSamplesAsync"/>'s segments use for the same chunk, so
    /// a caller can align the two by timestamp overlap. Empty (not null) when the chunk produced no
    /// translated speech.
    /// </returns>
    public async Task<IReadOnlyList<TranscriptionSegment>> TranslateSamplesAsync(
        ReadOnlyMemory<float> samples,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);

        if (samples.IsEmpty)
        {
            return [];
        }

        var processor = GetOrCreateTranslateProcessor();
        var segments = new List<TranscriptionSegment>();

        await _nativeCallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Mirrors TranscribeFileAsync's own processor.ChangeLanguage(activeLanguage) pattern:
            // ChangeLanguage sets which language this call decodes *from*; WithTranslate() (baked
            // in at Build() time below, permanent for this processor's lifetime) is what makes it
            // translate to English instead of transcribing verbatim.
            processor.ChangeLanguage(sourceLanguage);

            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                segments.Add(Map(segment));
            }
        }
        finally
        {
            _nativeCallGate.Release();
        }

        return segments;
    }

    /// <summary>
    /// Builds (once) or returns the already-built translate-mode processor. Not protected by
    /// <see cref="_nativeCallGate"/> - that only needs to guard the actual decode call, not
    /// construction - so this has its own narrower lock purely against two callers racing to
    /// build the processor twice (not expected in practice: <see cref="TranslateSamplesAsync"/> is
    /// only ever called from one dedicated translation worker, but this stays defensively correct
    /// regardless of caller count).
    /// </summary>
    private WhisperProcessor GetOrCreateTranslateProcessor()
    {
        if (_translateProcessor is not null)
        {
            return _translateProcessor;
        }

        lock (_translateProcessorLock)
        {
            if (_translateProcessor is null)
            {
                // Initial WithLanguage() value is a placeholder - every real call goes through
                // ChangeLanguage() first (see TranslateSamplesAsync), same as the file pass does
                // for its own processor. WithTranslate() is what is actually load-bearing here:
                // it is baked in at Build() time and stays in effect for every call this processor
                // ever makes, for its entire lifetime.
                var builder = _factory.CreateBuilder()
                    .WithProbabilities()
                    .WithLanguage("en")
                    .WithTranslate();

                builder = ApplyDecodeQualityOptions(builder);

                _translateProcessor = builder.Build();
            }
        }

        return _translateProcessor;
    }

    private WhisperProcessor BuildProcessor(IProgress<int>? progress)
    {
        var builder = _factory.CreateBuilder().WithProbabilities();

        builder = _options.AutoDetectLanguage
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(_options.LanguageOverride!);

        builder = ApplyDecodeQualityOptions(builder);

        if (progress is not null)
        {
            builder = builder.WithProgressHandler(p => progress.Report(p));
        }

        return builder.Build();
    }

    /// <summary>
    /// Applies the decode-quality settings every processor this transcriber builds shares -
    /// currently <see cref="BuildProcessor"/> (transcribe, both file and streaming) and
    /// <see cref="GetOrCreateTranslateProcessor"/> (translate). Split out purely so the translate
    /// processor gets the same anti-hallucination-loop guards as transcription, without a second,
    /// independently-tunable (and driftable) copy of these six settings.
    /// </summary>
    private WhisperProcessorBuilder ApplyDecodeQualityOptions(WhisperProcessorBuilder builder)
    {
        // Decoder guards against the hallucination-loop failure mode (see
        // WhisperTranscriberOptions.NoContext for the full root-cause writeup): without these,
        // whisper.cpp's greedy decoder can latch onto a wrong phrase on quiet/ambiguous audio,
        // feed it back into the next window as context, and repeat it verbatim for the rest of
        // the file. NoContext is the primary fix; the threshold/temperature settings are
        // whisper.cpp's standard escape hatch (temperature fallback) for the same failure class.
        if (_options.NoContext)
        {
            builder = builder.WithNoContext();
        }

        if (_options.NoSpeechThreshold is { } noSpeechThreshold)
        {
            builder = builder.WithNoSpeechThreshold(noSpeechThreshold);
        }

        if (_options.EntropyThreshold is { } entropyThreshold)
        {
            builder = builder.WithEntropyThreshold(entropyThreshold);
        }

        if (_options.LogProbThreshold is { } logProbThreshold)
        {
            builder = builder.WithLogProbThreshold(logProbThreshold);
        }

        if (_options.Temperature is { } temperature)
        {
            builder = builder.WithTemperature(temperature);
        }

        if (_options.TemperatureIncrement is { } temperatureIncrement)
        {
            builder = builder.WithTemperatureInc(temperatureIncrement);
        }

        if (_options.Threads is { } threads)
        {
            builder = builder.WithThreads(threads);
        }

        return builder;
    }

    private TranscriptionResult BuildResult(
        IReadOnlyList<TranscriptionSegment> segments,
        TimeSpan audioDuration,
        TimeSpan wallClock,
        IReadOnlyList<LanguageSwitchEvent>? languageSwitches = null,
        IReadOnlyDictionary<string, int>? outOfSetLanguageDetections = null)
    {
        var (language, languageProbability) = DetermineLanguage(segments);
        var fullText = string.Join(
            ' ',
            segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
        var realtimeFactor = wallClock.TotalSeconds > 0
            ? audioDuration.TotalSeconds / wallClock.TotalSeconds
            : 0.0;

        return new TranscriptionResult(
            language,
            languageProbability,
            segments,
            fullText,
            audioDuration,
            wallClock,
            realtimeFactor,
            Backend,
            languageSwitches ?? [],
            outOfSetLanguageDetections ?? new Dictionary<string, int>());
    }

    private (string Language, float Probability) DetermineLanguage(IReadOnlyList<TranscriptionSegment> segments)
    {
        if (!_options.AutoDetectLanguage)
        {
            return (_options.LanguageOverride!, 1.0f);
        }

        if (segments.Count == 0)
        {
            return ("unknown", 0f);
        }

        // Meetings code-switch (Japanese/Mandarin/English in the same recording), so report
        // the majority language across segments rather than assuming a single language holds
        // for the whole audio.
        var dominant = segments
            .Where(s => !string.IsNullOrEmpty(s.Language))
            .GroupBy(s => s.Language!)
            .Select(g => new { Language = g.Key, Count = g.Count(), AvgProbability = g.Average(s => (double)s.Probability) })
            .OrderByDescending(g => g.Count)
            .FirstOrDefault();

        return dominant is null
            ? ("unknown", 0f)
            : (dominant.Language, (float)dominant.AvgProbability);
    }

    private static TranscriptionSegment Map(SegmentData segment) => new(
        segment.Start,
        segment.End,
        segment.Text,
        segment.Probability,
        segment.MinProbability,
        segment.MaxProbability,
        segment.NoSpeechProbability,
        segment.Language);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_translateProcessor is not null)
            {
                await _translateProcessor.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _factory.Dispose();
            }
            finally
            {
                _logSubscription.Dispose();
                _nativeCallGate.Dispose();
            }
        }
    }
}
