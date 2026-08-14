namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Validates a device-id-or-null option against the real device list at recording
/// start time, before any capture session opens or file is created - mirrors
/// <c>MeetingScribe.Audio.Windows.Internal.AudioDeviceResolver</c>'s "fail fast" shape.
/// </summary>
internal static class MacDeviceResolver
{
    public static void ValidateCapture(string? deviceId) =>
        Validate(deviceId, MacDeviceEnumerator.ListCaptureDevices(), "microphone");

    /// <summary>
    /// Validates a requested system-audio (render) device id against the real output
    /// device list. Informational only: ScreenCaptureKit captures the whole system
    /// audio mix and has no per-render-device loopback selection the way WASAPI does
    /// on Windows, so this never actually narrows what gets captured - it only
    /// confirms the id refers to a real device rather than silently ignoring a typo.
    /// </summary>
    public static void ValidateRender(string? deviceId) =>
        Validate(deviceId, MacDeviceEnumerator.ListRenderDevices(), "system-audio (render)");

    private static void Validate(string? deviceId, IReadOnlyList<AudioDeviceInfo> devices, string kind)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            if (devices.Count == 0)
            {
                throw new AudioDeviceNotFoundException($"No {kind} device exists on this machine.");
            }

            return;
        }

        foreach (var device in devices)
        {
            if (device.Id == deviceId)
            {
                return;
            }
        }

        throw new AudioDeviceNotFoundException(
            $"Could not resolve {kind} device '{deviceId}'. It may be unplugged, disabled, or no longer present.");
    }
}
