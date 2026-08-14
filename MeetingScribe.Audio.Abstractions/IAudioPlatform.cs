namespace MeetingScribe.Audio;

/// <summary>
/// The seam a platform-specific audio backend implements: device enumeration plus a
/// recorder factory. This is the only audio surface application code (the App project)
/// touches directly - never a concrete recorder/enumerator type, never NAudio.
///
/// Today, <c>MeetingScribe.Audio.Windows</c>'s <c>WindowsAudioPlatform</c> is the only
/// real implementation (NAudio/WASAPI). <c>MeetingScribe.Audio.Mac</c>'s
/// <c>MacAudioPlatform</c> is a placeholder that throws
/// <see cref="PlatformNotSupportedException"/> from every member - a future macOS
/// implementation (CoreAudio for the microphone, ScreenCaptureKit or a virtual device
/// such as BlackHole for system audio) replaces that stub without touching this
/// interface or any caller.
/// </summary>
public interface IAudioPlatform
{
    /// <summary>Active capture (input/microphone) devices.</summary>
    IReadOnlyList<AudioDeviceInfo> ListCaptureDevices();

    /// <summary>Active render (playback/speaker) devices — the set loopback capture can be attached to.</summary>
    IReadOnlyList<AudioDeviceInfo> ListRenderDevices();

    /// <summary>Creates a new, not-yet-started recorder for one meeting session.</summary>
    IMeetingRecorder CreateRecorder(MeetingRecorderOptions options);
}
