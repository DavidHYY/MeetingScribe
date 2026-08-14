using MeetingScribe.Whisper;

namespace MeetingScribe.App.Models;

/// <summary>
/// Verified realtime-factor figures for this exact machine's Vulkan/Intel-UHD +
/// flash-attention pipeline. Only sizes that were actually measured get a number here -
/// see the "never fabricate/guess" rule: Tiny/Small/LargeV3-live are simply not
/// benchmarked on this pipeline, so they report null rather than a made-up figure.
/// </summary>
public static class ModelBenchmarks
{
    /// <summary>Human-readable note shown next to a model size in the picker, or null if unmeasured.</summary>
    public static string? Describe(WhisperModelSize size) => size switch
    {
        WhisperModelSize.Medium =>
            "measured 2.32x realtime typical (1.68x-2.63x range) on this machine's Vulkan iGPU",
        WhisperModelSize.LargeV3 =>
            "background pass: ~63 min typical for a 60-min meeting (range ~61-80 min) - roughly real-time",
        _ => "not benchmarked on this Vulkan pipeline",
    };

    /// <summary>Estimated wall-clock duration for the final pass over one track, or null if unmeasured.</summary>
    public static TimeSpan? EstimateDuration(WhisperModelSize size, TimeSpan audioDuration)
    {
        return size switch
        {
            WhisperModelSize.Medium => TimeSpan.FromSeconds(audioDuration.TotalSeconds / 2.32),
            WhisperModelSize.LargeV3 => TimeSpan.FromSeconds(audioDuration.TotalSeconds * (63.0 / 60.0)),
            _ => null,
        };
    }
}
