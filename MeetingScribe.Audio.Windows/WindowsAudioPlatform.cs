namespace MeetingScribe.Audio;

/// <summary>
/// The Windows <see cref="IAudioPlatform"/> implementation: device enumeration and
/// recorder creation both backed by NAudio/WASAPI (<see cref="AudioDeviceEnumerator"/>,
/// <see cref="MeetingRecorder"/>). This is the only type in this assembly the app project
/// needs to reference directly - everything else it touches through the
/// <c>MeetingScribe.Audio.Abstractions</c> interfaces and neutral model types.
/// </summary>
public sealed class WindowsAudioPlatform : IAudioPlatform
{
    public IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => AudioDeviceEnumerator.ListCaptureDevices();

    public IReadOnlyList<AudioDeviceInfo> ListRenderDevices() => AudioDeviceEnumerator.ListRenderDevices();

    public IMeetingRecorder CreateRecorder(MeetingRecorderOptions options) => new MeetingRecorder(options);
}
