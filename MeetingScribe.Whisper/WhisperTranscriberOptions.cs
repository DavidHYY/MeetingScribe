namespace MeetingScribe.Whisper;

/// <summary>
/// Configuration for a <see cref="WhisperTranscriber"/> instance. Immutable after construction;
/// build a new transcriber to change model, language policy, or GPU settings.
/// </summary>
public sealed class WhisperTranscriberOptions
{
    /// <summary>Which model size to load.</summary>
    public required WhisperModelSize ModelSize { get; init; }

    /// <summary>
    /// Local directory used to cache ggml model files. Created if missing. Models are
    /// downloaded here on demand by <see cref="ModelManager"/>; nothing is ever bundled
    /// with the app.
    /// </summary>
    public required string ModelCacheDirectory { get; init; }

    /// <summary>
    /// Two-letter language code to force (e.g. "en", "ja", "zh"), or null/"auto" (the
    /// default) to auto-detect. Meetings mix Japanese, Mandarin and English, so forcing a
    /// language should be an explicit opt-in, not the default.
    /// </summary>
    public string? LanguageOverride { get; init; }

    /// <summary>Whether to request GPU acceleration from the loaded native runtime. Default true.</summary>
    public bool UseGpu { get; init; } = true;

    /// <summary>GPU device index to use when <see cref="UseGpu"/> is true. Default 0.</summary>
    public int GpuDevice { get; init; }

    /// <summary>
    /// Whether to use whisper.cpp's FlashAttention kernel. Default true. On this machine's
    /// Vulkan/Intel UHD setup this is the single biggest lever for realtime factor: the
    /// verified whisper.cpp CLI baseline (1.5x-2.05x realtime on the medium model) was run
    /// with flash attention on; Whisper.net's own default for this flag is false, which
    /// measured ~1.2x on the same clip/model/GPU.
    /// </summary>
    public bool UseFlashAttention { get; init; } = true;

    /// <summary>
    /// CPU thread count for the portions of the pipeline that still run on CPU (decode,
    /// feature extraction) regardless of GPU use. Null lets whisper.cpp pick its own default.
    /// </summary>
    public int? Threads { get; init; }

    /// <summary>
    /// Whether to disable using previously decoded text as context for the next decode window
    /// (whisper.cpp's <c>no_context</c> flag, exposed by Whisper.net as
    /// <c>WhisperProcessorBuilder.WithNoContext()</c>). Default true.
    /// </summary>
    /// <remarks>
    /// This is the primary fix for a reproduced hallucination-loop failure: on quiet or
    /// ambiguous audio, whisper.cpp's greedy decoder can emit a wrong phrase, and by default
    /// whisper.cpp feeds that phrase back into the next window as "previous text" context —
    /// entrenching it, since the model now treats its own hallucination as ground truth. Once
    /// entrenched the loop does not self-correct: it repeats verbatim for the rest of the file
    /// (reproduced on a 26-minute meeting recording: 373 of 513 segments, 72.7%, were a single
    /// repeated phrase). The Python side of this project hit the identical failure on the same
    /// recording; setting <c>condition_on_previous_text=False</c> there was the fix. This flag
    /// is the C#/Whisper.net equivalent and was missing from <c>BuildProcessor()</c>.
    /// </remarks>
    public bool NoContext { get; init; } = true;

    /// <summary>
    /// no_speech probability above which whisper.cpp treats a decode window as non-speech.
    /// Null skips calling <c>WithNoSpeechThreshold</c> and leaves whisper.cpp's compiled-in
    /// default in effect. Default 0.6f (whisper.cpp's own default), set explicitly here rather
    /// than left implicit.
    /// </summary>
    public float? NoSpeechThreshold { get; init; } = 0.6f;

    /// <summary>
    /// Entropy threshold: a decode whose per-token entropy exceeds this is considered too
    /// uncertain and triggers temperature fallback (see <see cref="Temperature"/> and
    /// <see cref="TemperatureIncrement"/>). Null skips calling <c>WithEntropyThreshold</c>.
    /// Default 2.4f, whisper.cpp's own default.
    /// </summary>
    public float? EntropyThreshold { get; init; } = 2.4f;

    /// <summary>
    /// Average log-probability threshold over sampled tokens: a decode below this is
    /// considered failed and triggers temperature fallback. Null skips calling
    /// <c>WithLogProbThreshold</c>. Default -1.0f, whisper.cpp's own default.
    /// </summary>
    public float? LogProbThreshold { get; init; } = -1.0f;

    /// <summary>
    /// Initial sampling temperature. Null skips calling <c>WithTemperature</c>. Default 0.0f
    /// (greedy decoding), whisper.cpp's own default.
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="TemperatureIncrement"/>, this is whisper.cpp's standard escape
    /// hatch from a repetition loop: a decode that fails the entropy, log-probability, or
    /// no-speech thresholds above is re-attempted at <c>temperature + increment</c> (stepping up
    /// to a cap of 1.0) instead of committing to the same bad greedy output.
    /// </remarks>
    public float? Temperature { get; init; } = 0.0f;

    /// <summary>
    /// Temperature increment applied on each fallback retry after a failed decode. Null skips
    /// calling <c>WithTemperatureInc</c>. Default 0.2f, whisper.cpp's own default.
    /// </summary>
    public float? TemperatureIncrement { get; init; } = 0.2f;

    /// <summary>Whether to auto-detect language rather than use <see cref="LanguageOverride"/>.</summary>
    public bool AutoDetectLanguage => string.IsNullOrWhiteSpace(LanguageOverride) ||
                                       LanguageOverride.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <see cref="WhisperTranscriber.TranscribeFileAsync"/> should skip a near-silent
    /// lead-in at the start of the file (room tone before anyone starts speaking) instead of
    /// sending it to whisper.cpp. Default true.
    /// </summary>
    /// <remarks>
    /// Fixes a residual defect in the chunked-transcription fix (see
    /// <see cref="NoContext"/>'s remarks for that fix): whisper.cpp reports
    /// <c>no_speech_probability = 0.000</c> even on the fabricated boilerplate it emits over
    /// genuine silence (YouTube-outro-style filler — "おやすみなさい", "ご覧いただきありがとう
    /// ございます" — measured on a real 26-minute meeting recording, concentrated in the
    /// near-silent lead-in before the meeting starts), so the built-in no-speech/entropy/logprob
    /// guards never fire on it. Not fixable via decoder flags; the only fix is to not decode
    /// that audio at all. See <see cref="AudioChunkPlanner.CountLeadingSilentChunks"/> for why
    /// this is a lead-in-only skip, not a file-wide silence gate.
    /// </remarks>
    public bool SkipSilentLeadIn { get; init; } = true;

    /// <summary>
    /// Threshold for <see cref="SkipSilentLeadIn"/>: a chunk at the start of the file counts as
    /// part of the silent lead-in when its RMS is below this fraction of the file's own median
    /// chunk RMS. Must be in (0, 1]. Default 0.65 — see
    /// <see cref="AudioChunkPlanner.CountLeadingSilentChunks"/> for the measured reasoning behind
    /// that number and how to retune it for a different recording setup.
    /// </summary>
    public double SilentLeadInRatio { get; init; } = 0.65;

    /// <summary>
    /// Two-letter language codes <see cref="WhisperTranscriber.TranscribeFileAsync"/> is allowed
    /// to switch the active decode language into. Defaults to Japanese/English/Mandarin/Polish/
    /// French — the five languages this project's real meetings mix, sometimes several within one
    /// call with live interpreting between sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A per-chunk detection outside this set (the "ko"/"nn" noise measured on a real recording's
    /// near-silent lead-in) never switches the active language — the file keeps decoding in
    /// whichever in-set language is already active — but is not silently dropped either: it is
    /// counted per language and surfaced on <see cref="TranscriptionResult.OutOfSetLanguageDetections"/>.
    /// This list is not meant to be exhaustive forever: a meeting that brings in a language not yet
    /// added here would otherwise be mistranscribed as one of these five with no visible sign
    /// anything was wrong, which is exactly the silent-wrongness failure this project has hit
    /// before (a single locked-language guess on a multilingual meeting).
    /// </para>
    /// <para>
    /// Null or empty disables the restriction entirely: any language whisper.cpp's per-chunk
    /// language-ID pass reports is accepted as a switch candidate (still subject to
    /// <see cref="LanguageSwitchConfirmationChunks"/> hysteresis, just not to set membership) — the
    /// "no restriction, auto-detect whatever shows up" behaviour. Comparisons against this set are
    /// case-insensitive.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string>? AllowedLanguages { get; init; } = DefaultAllowedLanguages;

    /// <summary>Default value of <see cref="AllowedLanguages"/> — see that property's remarks.</summary>
    public static IReadOnlyList<string> DefaultAllowedLanguages { get; } = ["ja", "en", "zh", "pl", "fr"];

    /// <summary>
    /// How many chunks in a row must independently detect the same new in-set language before
    /// <see cref="WhisperTranscriber.TranscribeFileAsync"/> switches the active decode language to
    /// it, instead of a single odd chunk (a brief interpreter aside, a misheard word) flipping the
    /// whole file. Must be &gt;= 1. Default 2.
    /// </summary>
    /// <remarks>
    /// Confirmation is look-ahead, not look-behind: before chunk N is decoded, this many chunks
    /// starting at N are cheaply language-ID'd (a standalone detection pass, not a full decode) and
    /// checked for agreement, so a genuine sustained switch takes effect starting at chunk N itself
    /// — no added lag — rather than only being believed one or more chunks after the fact. A false
    /// alarm (the candidate does not hold for the whole confirmation window) costs staying on the
    /// previous language for that one chunk; it never delays a real switch. Set to 1 to disable
    /// hysteresis entirely (switch on the very first in-set detection that differs from the current
    /// language) — useful if a meeting's languages change faster than 2-chunk confirmation can track.
    /// </remarks>
    public int LanguageSwitchConfirmationChunks { get; init; } = 2;
}
