using System.Globalization;
using MeetingScribe.Audio;
using MeetingScribe.Audio.TestHarness;

// UTF-8 everywhere: device friendly names can contain Japanese/Chinese/Polish text,
// and the default Windows console codepage here is cp950 - explicit UTF-8 avoids the
// mangled-text corruption this machine has hit before.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // stdout redirected to a file/pipe that doesn't support codepage changes - fine,
    // the underlying bytes written are still UTF-8 either way.
}

var platform = AudioPlatformProvider.Current;
var options = HarnessOptions.Parse(args);

Console.WriteLine("=== MeetingScribe.Audio test harness ===");
Console.WriteLine();

Console.WriteLine("-- Capture (input) devices --");
foreach (var d in platform.ListCaptureDevices())
{
    Console.WriteLine($"  [{(d.IsDefault ? "default" : "       ")}] {d.Name}  ({d.Id})");
}

Console.WriteLine();
Console.WriteLine("-- Render (playback) devices --");
foreach (var d in platform.ListRenderDevices())
{
    Console.WriteLine($"  [{(d.IsDefault ? "default" : "       ")}] {d.Name}  ({d.Id})");
}

string? systemDeviceId = null;
if (options.SystemDeviceNameContains is { } needle)
{
    var match = platform.ListRenderDevices()
        .FirstOrDefault(d => d.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.WriteLine($"FAILED: no render device name contains '{needle}'.");
        return 1;
    }

    systemDeviceId = match.Id;
    Console.WriteLine($"Using explicit system-audio device: {match.Name}");
}

Console.WriteLine();
Console.WriteLine($"Recording for {options.Seconds}s -> {options.OutputDirectory}");
Console.WriteLine($"  microphone enabled : {options.MicEnabled}");
Console.WriteLine($"  system audio enabled: {options.SystemEnabled}");
Console.WriteLine("Play something through your speakers now if you want a non-silent system-audio track.");
Console.WriteLine();

var recorderOptions = new MeetingRecorderOptions
{
    OutputDirectory = options.OutputDirectory,
    MicrophoneEnabled = options.MicEnabled,
    SystemAudioEnabled = options.SystemEnabled,
    SystemAudioDeviceId = systemDeviceId,
};

await using var recorder = platform.CreateRecorder(recorderOptions);

var lastPrint = DateTime.MinValue;
recorder.LevelUpdated += (_, e) =>
{
    // Throttle console spam to ~2 lines/sec regardless of how often blocks arrive.
    var now = DateTime.UtcNow;
    if ((now - lastPrint).TotalMilliseconds < 500)
    {
        return;
    }

    lastPrint = now;
    Console.WriteLine(
        $"  [{now:HH:mm:ss}] {e.Source,-11} rms={e.Level.Rms:F4} peak={e.Level.Peak:F4}");
};

recorder.CaptureError += (_, e) =>
{
    Console.WriteLine($"  !! CAPTURE ERROR [{e.Source}]: {e.Exception.GetType().Name}: {e.Exception.Message}");
};

try
{
    recorder.Start();
}
catch (AudioDeviceNotFoundException ex)
{
    Console.WriteLine($"FAILED to start: {ex.Message}");
    return 1;
}

var sw = System.Diagnostics.Stopwatch.StartNew();
while (sw.Elapsed < TimeSpan.FromSeconds(options.Seconds))
{
    await Task.Delay(200);
}

Console.WriteLine();
Console.WriteLine("Stopping...");
await recorder.StopAsync();
Console.WriteLine("Stopped.");
Console.WriteLine();

if (recorder.Errors.Count > 0)
{
    Console.WriteLine("-- Errors recorded during capture --");
    foreach (var err in recorder.Errors)
    {
        Console.WriteLine($"  {err}");
    }

    Console.WriteLine();
}

Console.WriteLine("-- Per-track analysis (read back from disk, independent of the live meter) --");
var overallNonSilent = true;
overallNonSilent &= AnalyzeAndReport("mic.wav", recorder.MicWavPath, options.MicEnabled);
overallNonSilent &= AnalyzeAndReport("system.wav", recorder.SystemWavPath, options.SystemEnabled);
overallNonSilent &= AnalyzeAndReport("raw.wav (mixed)", recorder.MixedWavPath, options.MicEnabled || options.SystemEnabled);

if (options.MicEnabled && options.SystemEnabled && File.Exists(recorder.MicWavPath) && File.Exists(recorder.SystemWavPath))
{
    Console.WriteLine();
    Console.WriteLine("-- mic vs system independence check --");
    var micSamples = WavAnalyzer.ReadRawSamples(recorder.MicWavPath);
    var systemSamples = WavAnalyzer.ReadRawSamples(recorder.SystemWavPath);
    var identical = micSamples.Length == systemSamples.Length && micSamples.AsSpan().SequenceEqual(systemSamples);
    var correlation = WavAnalyzer.Correlate(micSamples, systemSamples);
    Console.WriteLine($"  byte-identical: {identical}");
    Console.WriteLine($"  Pearson correlation: {correlation.ToString("F4", CultureInfo.InvariantCulture)}");
    if (identical)
    {
        Console.WriteLine("  WARNING: mic.wav and system.wav are byte-identical - one source is likely not capturing real audio.");
        overallNonSilent = false;
    }
}

Console.WriteLine();
Console.WriteLine(overallNonSilent
    ? "RESULT: all enabled tracks captured non-silent, independent audio."
    : "RESULT: at least one enabled track was silent, near-silent, or not independent - see above. Reporting this plainly, not as a pass.");

return overallNonSilent ? 0 : 2;

static bool AnalyzeAndReport(string label, string path, bool expected)
{
    if (!expected)
    {
        Console.WriteLine($"  {label,-16} skipped (source not enabled)");
        return true;
    }

    if (!File.Exists(path))
    {
        Console.WriteLine($"  {label,-16} MISSING FILE: {path}");
        return false;
    }

    WavAnalyzer.Result result;
    try
    {
        result = WavAnalyzer.Analyze(path);
    }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"  {label,-16} UNREADABLE: {ex.Message}");
        return false;
    }

    // A generous silence floor: true digital silence measures exactly 0. Anything
    // above ~0.0003 RMS reflects real captured signal (room noise floor and up).
    const double silenceFloor = 0.0003;
    var nonSilent = result.Rms > silenceFloor;

    Console.WriteLine(
        $"  {label,-16} {result.DurationSeconds,6:F2}s  {result.SampleRate}Hz {result.Channels}ch {result.BitsPerSample}bit  " +
        $"rms={result.Rms.ToString("F5", CultureInfo.InvariantCulture)} peak={result.Peak.ToString("F5", CultureInfo.InvariantCulture)}  " +
        $"[{(nonSilent ? "NON-SILENT" : "SILENT")}]");

    return nonSilent;
}

internal sealed record HarnessOptions(
    int Seconds, string OutputDirectory, bool MicEnabled, bool SystemEnabled, string? SystemDeviceNameContains)
{
    public static HarnessOptions Parse(string[] args)
    {
        var seconds = 10;
        var outputDirectory = Path.Combine(Path.GetTempPath(), "MeetingScribeAudioHarness", DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        var micEnabled = true;
        var systemEnabled = true;
        string? systemDeviceNameContains = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" when i + 1 < args.Length:
                    seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--output" when i + 1 < args.Length:
                    outputDirectory = args[++i];
                    break;
                case "--mic-only":
                    systemEnabled = false;
                    break;
                case "--system-only":
                    micEnabled = false;
                    break;
                // Debugging aid: pick a specific render device to loopback (by friendly-
                // name substring) instead of the OS default - useful on a machine where
                // the default render endpoint (e.g. a monitor's HDMI/DP audio) isn't
                // wired to real speakers, which otherwise makes system-audio capture
                // look silent for reasons that have nothing to do with the capture code.
                case "--system-device" when i + 1 < args.Length:
                    systemDeviceNameContains = args[++i];
                    break;
            }
        }

        return new HarnessOptions(seconds, outputDirectory, micEnabled, systemEnabled, systemDeviceNameContains);
    }
}
