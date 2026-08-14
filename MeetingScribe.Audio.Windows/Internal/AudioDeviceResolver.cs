using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Resolves a device-id-or-null option into a live <see cref="MMDevice"/> at recording
/// start time. The returned device is owned by the caller (its <see cref="SourcePipeline"/>)
/// and must be disposed by it when capture ends.
/// </summary>
internal static class AudioDeviceResolver
{
    public static MMDevice ResolveCapture(string? deviceId) => Resolve(DataFlow.Capture, deviceId);

    public static MMDevice ResolveRender(string? deviceId) => Resolve(DataFlow.Render, deviceId);

    private static MMDevice Resolve(DataFlow flow, string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
        }
        // Verified against a real bad-device-id run: MMDeviceEnumerator.GetDevice's COM
        // interop marshaling surfaces an unrecognized id as ArgumentException (.NET's
        // stock mapping for the underlying E_INVALIDARG HRESULT), not COMException -
        // catching only COMException let a bad id crash the process instead of
        // producing AudioDeviceNotFoundException. Genuinely missing/disabled devices
        // (unplugged mid-session, no default device for this flow) surface as
        // COMException, so both are handled here.
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            var kind = flow == DataFlow.Capture ? "capture" : "render";
            var which = string.IsNullOrEmpty(deviceId) ? "(default)" : deviceId;
            throw new AudioDeviceNotFoundException(
                $"Could not resolve {kind} device '{which}'. It may be unplugged, disabled, " +
                $"or (for the default device) no {kind} device exists on this machine.",
                ex);
        }
    }
}
