namespace MeetingScribe.Audio;

/// <summary>
/// One real audio endpoint (a microphone/input device, or a playback/render device
/// that can be captured via WASAPI loopback), as reported by NAudio's
/// <see cref="NAudio.CoreAudioApi.MMDeviceEnumerator"/>.
/// </summary>
/// <param name="Id">
/// The endpoint's stable device ID (<c>MMDevice.ID</c>). Pass this back into
/// <see cref="MeetingRecorderOptions.MicrophoneDeviceId"/> or
/// <see cref="MeetingRecorderOptions.SystemAudioDeviceId"/> to select this device.
/// </param>
/// <param name="Name">Human-readable friendly name, e.g. "Microphone Array (Realtek Audio)".</param>
/// <param name="IsDefault">
/// True if this was the OS-designated default device (Multimedia role) for its
/// data-flow direction at the time of enumeration.
/// </param>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);
