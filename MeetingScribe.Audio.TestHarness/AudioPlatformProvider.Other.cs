using MeetingScribe.Audio;

namespace MeetingScribe.Audio.TestHarness;

/// <summary>
/// Selects the macOS <see cref="IAudioPlatform"/> implementation. Compiled for every
/// host OS other than Windows - see <c>AudioPlatformProvider.Windows.cs</c> for the
/// Windows counterpart and <c>MeetingScribe.Audio.TestHarness.csproj</c> for the
/// <c>&lt;Compile Remove&gt;</c> conditions that make exactly one of the two build.
/// </summary>
internal static class AudioPlatformProvider
{
    public static IAudioPlatform Current { get; } = new MacAudioPlatform();
}
