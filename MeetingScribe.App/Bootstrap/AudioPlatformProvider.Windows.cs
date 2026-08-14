using MeetingScribe.Audio;

namespace MeetingScribe.App;

/// <summary>
/// Selects the Windows <see cref="IAudioPlatform"/> implementation. Compiled only for the
/// <c>net10.0-windows</c> leg of this multi-targeted project - see
/// <c>AudioPlatformProvider.Other.cs</c> for the counterpart compiled everywhere else,
/// and <c>MeetingScribe.App.csproj</c> for the <c>&lt;Compile Remove&gt;</c> conditions
/// that make exactly one of the two build.
/// </summary>
internal static class AudioPlatformProvider
{
    public static IAudioPlatform Current { get; } = new WindowsAudioPlatform();
}
