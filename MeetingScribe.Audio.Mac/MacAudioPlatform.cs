using MeetingScribe.Audio.Internal;

namespace MeetingScribe.Audio;

/// <summary>
/// The macOS <see cref="IAudioPlatform"/> implementation: device enumeration and
/// recorder creation both backed by a small native Objective-C helper
/// (<c>libmeetingscribe_mac_audio.dylib</c>, built from
/// <c>MeetingScribe.Audio.Mac/native/meetingscribe_mac_audio.m</c>) that wraps
/// AVFoundation (microphone) and ScreenCaptureKit (system audio) behind a plain C API -
/// see <see cref="MacMeetingRecorder"/> and <c>Internal/NativeMethods.cs</c>.
///
/// This assembly only ever produces a working <c>libmeetingscribe_mac_audio.dylib</c>
/// when built on a macOS host (see the <c>BuildMacNativeAudioHelper</c> MSBuild target
/// in this project's .csproj); on any other host the managed types below still compile
/// (proving the <see cref="IAudioPlatform"/> seam holds), but P/Invoking into them at
/// runtime would fail to resolve the native library - this type is only meant to be
/// instantiated when actually running on macOS.
/// </summary>
public sealed class MacAudioPlatform : IAudioPlatform
{
    public IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => MacDeviceEnumerator.ListCaptureDevices();

    public IReadOnlyList<AudioDeviceInfo> ListRenderDevices() => MacDeviceEnumerator.ListRenderDevices();

    public IMeetingRecorder CreateRecorder(MeetingRecorderOptions options) => new MacMeetingRecorder(options);

    /// <summary>
    /// Not implemented on macOS. <see cref="MeetingScribe.Audio.Windows.WindowsAudioPlatform"/>'s
    /// counterpart goes through NAudio's Windows Media Foundation wrapper - there is no
    /// equivalent wired up here (would be AVFoundation/AVAssetReader via the native
    /// <c>meetingscribe_mac_audio</c> helper this assembly already builds for capture, but that
    /// decode path does not exist yet). Importing a WAV recording still works on macOS without
    /// this method at all (<c>WavAudioLoader</c> reads WAV natively); only non-WAV imports
    /// (MP3/M4A/MP4) hit this and get an honest, actionable error instead of either a crash or a
    /// silent no-op.
    /// </summary>
    public Task<Stream> DecodeAudioFileToWavAsync(string filePath, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException(
            "Importing a compressed recording (MP3/M4A/MP4) is not supported on macOS yet - " +
            "only WAV files can be imported here. Convert the recording to WAV first (e.g. " +
            "'ffmpeg -i input.m4a output.wav'), or import it on a Windows machine.");
}
