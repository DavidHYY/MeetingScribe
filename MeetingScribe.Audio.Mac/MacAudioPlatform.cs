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
}
