using MeetingScribe.Audio;

namespace MeetingScribe.Audio.TestHarness;

/// <summary>
/// Selects the Windows <see cref="IAudioPlatform"/> implementation. Compiled only when
/// building on Windows - see <c>AudioPlatformProvider.Other.cs</c> for the macOS
/// counterpart and <c>MeetingScribe.Audio.TestHarness.csproj</c> for the
/// <c>&lt;Compile Remove&gt;</c> conditions that make exactly one of the two build.
/// </summary>
internal static class AudioPlatformProvider
{
    public static IAudioPlatform Current { get; } = new WindowsAudioPlatform();
}
