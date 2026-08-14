using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetingScribe.Audio;

/// <summary>
/// Lists real WASAPI audio endpoints on the machine. Backed by
/// <see cref="MMDeviceEnumerator"/>; every <see cref="MMDevice"/> touched here is
/// disposed before the method returns, so nothing is leaked.
/// </summary>
public static class AudioDeviceEnumerator
{
    /// <summary>Active capture (input/microphone) devices.</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => ListDevices(DataFlow.Capture);

    /// <summary>Active render (playback/speaker) devices — the set loopback capture can be attached to.</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListRenderDevices() => ListDevices(DataFlow.Render);

    private static IReadOnlyList<AudioDeviceInfo> ListDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (COMException)
        {
            // No default device for this flow direction (e.g. no playback device present).
            // Leave defaultId null; every device below will report IsDefault = false.
        }

        var results = new List<AudioDeviceInfo>();
        var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var device in collection)
        {
            using (device)
            {
                try
                {
                    results.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                }
                catch (COMException)
                {
                    // Device dropped out (unplugged/disabled) between enumeration and property
                    // read. Skip it rather than surfacing a half-built entry.
                }
            }
        }

        return results;
    }
}
