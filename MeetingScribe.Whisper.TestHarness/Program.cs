using System.Diagnostics;
using System.Text;
using MeetingScribe.Whisper;

// This machine's default console/file codepage is cp950; every write in this harness must be
// explicit UTF-8 (hard project requirement — Japanese/Traditional Chinese/Polish text has
// corrupted here before under the default codepage).
Console.OutputEncoding = Encoding.UTF8;

var options = HarnessOptions.Parse(args);

Console.WriteLine("=== MeetingScribe.Whisper test harness ===");
Console.WriteLine($"Audio file     : {options.AudioPath}");
Console.WriteLine($"Model size     : {options.ModelSize}");
Console.WriteLine($"Model cache dir: {options.ModelCacheDirectory}");
Console.WriteLine($"Language       : {(options.Language ?? "auto")}");
Console.WriteLine($"Allowed langs  : {(options.AllowedLanguages is { Count: > 0 } al0 ? string.Join(',', al0) : "(unrestricted)")}");
Console.WriteLine($"Switch confirm : {options.LanguageSwitchConfirmationChunks} chunk(s)");
Console.WriteLine($"Translate      : {options.Translate}");
Console.WriteLine();

if (!File.Exists(options.AudioPath))
{
    Console.Error.WriteLine($"FATAL: audio file not found: {options.AudioPath}");
    return 1;
}

var audioInfo = new FileInfo(options.AudioPath);
Console.WriteLine($"Audio file size: {audioInfo.Length:N0} bytes");
if (audioInfo.Length == 0)
{
    Console.Error.WriteLine("FATAL: audio file is empty (0 bytes) — refusing to benchmark a silent/empty file.");
    return 1;
}

var transcriberOptions = new WhisperTranscriberOptions
{
    ModelSize = options.ModelSize,
    ModelCacheDirectory = options.ModelCacheDirectory,
    LanguageOverride = options.Language,
    UseGpu = true,
    AllowedLanguages = options.AllowedLanguages,
    LanguageSwitchConfirmationChunks = options.LanguageSwitchConfirmationChunks,
};

Console.WriteLine();
Console.WriteLine("--- Loading model ---");
var loadStopwatch = Stopwatch.StartNew();

var downloadProgress = new Progress<double>(p =>
    Console.WriteLine($"  model download: {p * 100.0,6:F1}%"));

await using var transcriber = await WhisperTranscriber.CreateAsync(
    transcriberOptions,
    modelDownloadProgress: downloadProgress);

loadStopwatch.Stop();
Console.WriteLine($"Model loaded in {loadStopwatch.Elapsed.TotalSeconds:F2}s");

// --- Backend proof: this is the part that catches a silent CPU fallback. ---
Console.WriteLine();
Console.WriteLine("--- Backend proof (is this actually Vulkan?) ---");
Console.WriteLine($"LoadedLibrary : {transcriber.Backend.LoadedLibrary}");
Console.WriteLine($"IsGpuBackend  : {transcriber.Backend.IsGpuBackend}");
Console.WriteLine($"RuntimeInfo   : {transcriber.Backend.RuntimeInfo}");
Console.WriteLine($"Native log lines captured at load: {transcriber.Backend.NativeLogLines.Count}");

var vulkanLogLines = transcriber.Backend.NativeLogLines
    .Where(l => l.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
             || l.Contains("gpu", StringComparison.OrdinalIgnoreCase))
    .ToList();

if (vulkanLogLines.Count > 0)
{
    Console.WriteLine("Vulkan/GPU-related native log lines:");
    foreach (var line in vulkanLogLines)
    {
        Console.WriteLine($"  {line}");
    }
}
else
{
    Console.WriteLine("(no Vulkan/GPU-related native log lines captured at load time)");
}

if (!transcriber.Backend.IsGpuBackend)
{
    Console.WriteLine();
    Console.WriteLine("WARNING: loaded backend is NOT a GPU backend. This run will be CPU-only.");
    Console.WriteLine("         Expect realtime factor near 0.6x, not the 1.5x-2.05x Vulkan figure.");
}

// --- Transcription ---
Console.WriteLine();
Console.WriteLine("--- Transcribing ---");

var lastReportedProgress = -1;
var progress = new Progress<int>(p =>
{
    // whisper.cpp reports progress in coarse steps; only print on change to avoid spam.
    if (p == lastReportedProgress)
    {
        return;
    }

    lastReportedProgress = p;
    Console.WriteLine($"  progress: {p,3}%");
});

await using var audioStream = File.OpenRead(options.AudioPath);
var result = await transcriber.TranscribeFileAsync(audioStream, progress);

// --- Report (real measured numbers only) ---
Console.WriteLine();
Console.WriteLine("=== Result ===");
Console.WriteLine($"Detected language     : {result.DetectedLanguage} (p={result.LanguageProbability:F2})");
Console.WriteLine($"Allowed languages       : {(options.AllowedLanguages is { Count: > 0 } al ? string.Join(',', al) : "(unrestricted)")}");
Console.WriteLine($"Language switches       : {result.LanguageSwitches.Count}");
foreach (var sw in result.LanguageSwitches)
{
    Console.WriteLine($"    [{sw.At:hh\\:mm\\:ss\\.fff}] {sw.FromLanguage} -> {sw.ToLanguage}");
}
if (result.OutOfSetLanguageDetections.Count > 0)
{
    Console.WriteLine("Out-of-set detections (never switched to, but counted):");
    foreach (var (lang, count) in result.OutOfSetLanguageDetections.OrderByDescending(kv => kv.Value))
    {
        Console.WriteLine($"    {lang}: {count} chunk(s)");
    }
}
else
{
    Console.WriteLine("Out-of-set detections   : none");
}
Console.WriteLine($"Segment count          : {result.Segments.Count}");
Console.WriteLine($"Character count         : {result.FullText.Length:N0}");
Console.WriteLine($"Audio duration          : {result.AudioDuration.TotalSeconds:F2}s");
Console.WriteLine($"Wall-clock (transcribe) : {result.WallClock.TotalSeconds:F2}s");
Console.WriteLine($"Realtime factor         : {result.RealtimeFactor:F2}x");
Console.WriteLine($"Backend                 : {result.Backend.LoadedLibrary} (GPU={result.Backend.IsGpuBackend})");
Console.WriteLine();
Console.WriteLine("Known Vulkan figure on this machine (whisper.cpp CLI, medium, 60s clip): 1.5x-2.05x realtime.");
Console.WriteLine($"This run                                                              : {result.RealtimeFactor:F2}x realtime.");

if (result.Segments.Count == 0 || result.FullText.Length == 0)
{
    Console.WriteLine();
    Console.Error.WriteLine("WARNING: transcript is empty. Do not report this run's speed as a valid number —");
    Console.Error.WriteLine("         an empty transcript most likely means silent/empty input audio, not a fast model.");
}

Console.WriteLine();
Console.WriteLine("--- First 5 segments ---");
foreach (var segment in result.Segments.Take(5))
{
    Console.WriteLine(
        $"  [{segment.Start:hh\\:mm\\:ss\\.fff} -> {segment.End:hh\\:mm\\:ss\\.fff}] " +
        $"(p={segment.Probability:F2}, lang={segment.Language}) {segment.Text.Trim()}");
}

// Full transcript written explicitly as UTF-8 so Japanese/Mandarin/Polish text survives —
// this machine's default codepage (cp950) has corrupted such text before.
var transcriptPath = Path.ChangeExtension(options.AudioPath, ".transcript.utf8.txt");
await File.WriteAllTextAsync(transcriptPath, result.FullText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
Console.WriteLine();
Console.WriteLine($"Full transcript written to: {transcriptPath}");

// Per-segment dump (timestamp/probability/language/text), so a hallucination-loop regression
// (a single segment's text repeated verbatim across many segments) is directly measurable from
// this harness's own output, not by reparsing FullText where segment boundaries are lost.
var segmentsPath = Path.ChangeExtension(options.AudioPath, ".segments.utf8.tsv");
await using (var segmentsWriter = new StreamWriter(
    segmentsPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
{
    await segmentsWriter.WriteLineAsync("start\tend\tprobability\tno_speech_probability\tlanguage\ttext");
    foreach (var segment in result.Segments)
    {
        await segmentsWriter.WriteLineAsync(
            $"{segment.Start:hh\\:mm\\:ss\\.fff}\t{segment.End:hh\\:mm\\:ss\\.fff}\t{segment.Probability:F3}\t" +
            $"{segment.NoSpeechProbability:F3}\t{segment.Language}\t{segment.Text.Trim().Replace('\t', ' ')}");
    }
}

Console.WriteLine($"Per-segment dump written to: {segmentsPath}");

if (result.Segments.Count > 0)
{
    var mostRepeated = result.Segments
        .Select(s => s.Text.Trim())
        .Where(t => t.Length > 0)
        .GroupBy(t => t, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .First();
    var repeatPercent = 100.0 * mostRepeated.Count() / result.Segments.Count;

    Console.WriteLine();
    Console.WriteLine($"Most-repeated single segment text: \"{Truncate(mostRepeated.Key, 60)}\"");
    Console.WriteLine($"  occurs {mostRepeated.Count()} / {result.Segments.Count} segments ({repeatPercent:F1}%)");
}

// --- Translate pass (--translate): exercises WhisperTranscriber.TranslateSamplesAsync, the
// whisper.cpp `translate` task LiveTranscriptionEngine drives for live English translation in
// the GUI - otherwise unverifiable without running the full app. Runs over the same audio file
// already transcribed above, reusing that pass's DetectedLanguage as the translate task's
// required source language (TranslateSamplesAsync does not auto-detect it independently). ---
if (options.Translate)
{
    Console.WriteLine();
    Console.WriteLine("--- Translating (translate task, English output) ---");

    if (string.IsNullOrWhiteSpace(result.DetectedLanguage))
    {
        Console.Error.WriteLine(
            "SKIPPED: the transcribe pass above did not detect a source language " +
            "(DetectedLanguage is empty) - TranslateSamplesAsync requires one.");
    }
    else
    {
        await using var translateAudioStream = File.OpenRead(options.AudioPath);
        var (translateSamples, translateAudioDuration) = await WavAudioLoader.LoadMono16kAsync(translateAudioStream);

        var translateStopwatch = Stopwatch.StartNew();
        var translatedSegments = await transcriber.TranslateSamplesAsync(
            translateSamples, result.DetectedLanguage, CancellationToken.None);
        translateStopwatch.Stop();

        var translatedText = string.Join(' ', translatedSegments.Select(s => s.Text.Trim()));
        var translateRealtimeFactor = translateStopwatch.Elapsed.TotalSeconds > 0
            ? translateAudioDuration.TotalSeconds / translateStopwatch.Elapsed.TotalSeconds
            : 0.0;

        Console.WriteLine($"Source language (from transcribe pass) : {result.DetectedLanguage}");
        Console.WriteLine($"Segment count                           : {translatedSegments.Count}");
        Console.WriteLine($"Character count                         : {translatedText.Length:N0}");
        Console.WriteLine($"Audio duration                          : {translateAudioDuration.TotalSeconds:F2}s");
        Console.WriteLine($"Wall-clock (translate)                  : {translateStopwatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Realtime factor                         : {translateRealtimeFactor:F2}x");

        if (translatedSegments.Count == 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(
                "WARNING: translated output is empty. Do not report this run's speed as a valid number - " +
                "an empty translation most likely means silent/empty input audio, not a fast model.");
        }

        Console.WriteLine();
        Console.WriteLine("--- First 5 translated segments ---");
        foreach (var segment in translatedSegments.Take(5))
        {
            Console.WriteLine(
                $"  [{segment.Start:hh\\:mm\\:ss\\.fff} -> {segment.End:hh\\:mm\\:ss\\.fff}] " +
                $"(p={segment.Probability:F2}) {segment.Text.Trim()}");
        }

        // UTF-8 explicit, same reasoning as the transcript file above - the translate task's
        // English output is ASCII in practice, but the source clip's language name/metadata
        // written alongside it in future extensions might not be, so keep the encoding explicit.
        var translationPath = Path.ChangeExtension(options.AudioPath, ".translation.en.utf8.txt");
        await File.WriteAllTextAsync(translationPath, translatedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Console.WriteLine();
        Console.WriteLine($"Full translation written to: {translationPath}");
    }
}

// --- Streaming session demo: proves the accumulate/flush contract (requirement 4) against
// the same model already loaded above. Simulates a live capture handing over small buffers
// (2s at a time, well below the 15s target) so ShouldFlush/MustFlush actually get exercised. ---
Console.WriteLine();
Console.WriteLine("--- Streaming session demo (accumulate/flush) ---");

await using (var streamingAudio = File.OpenRead(options.AudioPath))
{
    var (streamingSamples, _) = await WavAudioLoader.LoadMono16kAsync(streamingAudio);

    const double simulatedCaptureChunkSeconds = 2.0;
    var samplesPerCapture = (int)(simulatedCaptureChunkSeconds * StreamingWhisperSession.SampleRateHz);

    await using var session = transcriber.CreateStreamingSession();
    var flushCount = 0;

    for (var offset = 0; offset < streamingSamples.Length; offset += samplesPerCapture)
    {
        var length = Math.Min(samplesPerCapture, streamingSamples.Length - offset);
        session.AppendSamples(streamingSamples.AsMemory(offset, length));

        if (session.MustFlush || session.ShouldFlush)
        {
            flushCount++;
            var chunkResult = (await session.FlushAsync()).Transcription;
            Console.WriteLine(
                $"  flush #{flushCount}: buffered {chunkResult.AudioDuration.TotalSeconds,4:F1}s -> " +
                $"{chunkResult.WallClock.TotalSeconds,5:F2}s wall ({chunkResult.RealtimeFactor,4:F2}x), " +
                $"{chunkResult.Segments.Count} segments: \"{Truncate(chunkResult.FullText, 70)}\"");
        }
    }

    // Drain whatever is left below the target threshold, same as end-of-recording in a real session.
    var finalResult = (await session.FlushAsync()).Transcription;
    if (finalResult.Segments.Count > 0)
    {
        flushCount++;
        Console.WriteLine(
            $"  flush #{flushCount} (final): buffered {finalResult.AudioDuration.TotalSeconds,4:F1}s -> " +
            $"{finalResult.WallClock.TotalSeconds,5:F2}s wall ({finalResult.RealtimeFactor,4:F2}x), " +
            $"{finalResult.Segments.Count} segments: \"{Truncate(finalResult.FullText, 70)}\"");
    }

    Console.WriteLine(
        $"Streaming session flushed {flushCount} chunk(s); " +
        $"target={StreamingWhisperSession.TargetChunkSeconds}s, max={StreamingWhisperSession.MaxChunkSeconds}s.");
}

return 0;

static string Truncate(string text, int maxLength) =>
    text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

internal sealed class HarnessOptions
{
    public required string AudioPath { get; init; }
    public required WhisperModelSize ModelSize { get; init; }
    public required string ModelCacheDirectory { get; init; }
    public string? Language { get; init; }
    public IReadOnlyCollection<string>? AllowedLanguages { get; init; }
    public int LanguageSwitchConfirmationChunks { get; init; } = 2;
    public bool Translate { get; init; }

    public static HarnessOptions Parse(string[] args)
    {
        // Defaults assume a local benchmark data folder next to the harness (not shipped in
        // the repo). Override both with --audio and --model-cache for your own audio file
        // and whisper.cpp model cache directory.
        var defaultDataDir = Path.Combine(Directory.GetCurrentDirectory(), "sample-data");

        var audioPath = Path.Combine(defaultDataDir, "sample_audio.wav");
        var modelCacheDirectory = Path.Combine(defaultDataDir, "models");
        var modelSize = WhisperModelSize.Medium;
        string? language = null; // auto-detect
        IReadOnlyCollection<string>? allowedLanguages = WhisperTranscriberOptions.DefaultAllowedLanguages;
        var confirmationChunks = 2;
        var translate = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--audio" when i + 1 < args.Length:
                    audioPath = args[++i];
                    break;
                case "--model-cache" when i + 1 < args.Length:
                    modelCacheDirectory = args[++i];
                    break;
                case "--model-size" when i + 1 < args.Length:
                    modelSize = Enum.Parse<WhisperModelSize>(args[++i], ignoreCase: true);
                    break;
                case "--language" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--allowed-languages" when i + 1 < args.Length:
                    // Empty string explicitly disables the restriction; any other value is
                    // comma-split. "default" keeps WhisperTranscriberOptions.DefaultAllowedLanguages.
                    var raw = args[++i];
                    allowedLanguages = raw.Length == 0
                        ? null
                        : raw.Equals("default", StringComparison.OrdinalIgnoreCase)
                            ? WhisperTranscriberOptions.DefaultAllowedLanguages
                            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--language-switch-confirmation-chunks" when i + 1 < args.Length:
                    confirmationChunks = int.Parse(args[++i]);
                    break;
                case "--translate":
                    translate = true;
                    break;
            }
        }

        return new HarnessOptions
        {
            AudioPath = audioPath,
            ModelSize = modelSize,
            ModelCacheDirectory = modelCacheDirectory,
            Language = language,
            AllowedLanguages = allowedLanguages,
            LanguageSwitchConfirmationChunks = confirmationChunks,
            Translate = translate,
        };
    }
}
