using MeetingScribe.Audio;

namespace MeetingScribe.App;

/// <summary>
/// Selects the (placeholder) macOS <see cref="IAudioPlatform"/> implementation. Compiled
/// for every TFM other than <c>net10.0-windows</c> - see <c>AudioPlatformProvider.Windows.cs</c>
/// for the Windows counterpart and <c>MeetingScribe.App.csproj</c> for the
/// <c>&lt;Compile Remove&gt;</c> conditions that make exactly one of the two build. No Mac
/// build is shipped today; this exists so the abstraction seam actually compiles against
/// a non-Windows target, not just in principle.
/// </summary>
internal static class AudioPlatformProvider
{
    public static IAudioPlatform Current { get; } = new MacAudioPlatform();
}
