namespace MeetingScribe.Audio;

/// <summary>
/// Thrown by <see cref="MeetingRecorder.Start"/> when a requested capture or render
/// device cannot be resolved (bad device ID, or no default device exists for the
/// requested data-flow direction, e.g. no playback device present on the machine).
/// </summary>
public sealed class AudioDeviceNotFoundException : Exception
{
    public AudioDeviceNotFoundException(string message)
        : base(message)
    {
    }

    public AudioDeviceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
