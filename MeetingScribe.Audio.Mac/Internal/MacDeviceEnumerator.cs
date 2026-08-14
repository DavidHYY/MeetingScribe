using System.Runtime.InteropServices;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Lists real macOS audio endpoints. Capture (input) devices come from AVFoundation's
/// <c>AVCaptureDeviceDiscoverySession</c>; render (output) devices come from CoreAudio's
/// HAL device list filtered to those with output streams. Both cross the native
/// boundary via <see cref="NativeMethods"/>; every native array is freed here before
/// returning.
/// </summary>
internal static class MacDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices() => ListDevices(isRender: false);

    public static IReadOnlyList<AudioDeviceInfo> ListRenderDevices() => ListDevices(isRender: true);

    private static IReadOnlyList<AudioDeviceInfo> ListDevices(bool isRender)
    {
        var count = isRender
            ? NativeMethods.msc_list_render_devices(out var devicesPtr)
            : NativeMethods.msc_list_capture_devices(out devicesPtr);

        if (count <= 0 || devicesPtr == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var results = new List<AudioDeviceInfo>(count);
            var structSize = Marshal.SizeOf<NativeMethods.NativeDeviceInfo>();

            for (var i = 0; i < count; i++)
            {
                var entryPtr = IntPtr.Add(devicesPtr, i * structSize);
                var entry = Marshal.PtrToStructure<NativeMethods.NativeDeviceInfo>(entryPtr);

                var id = entry.DeviceId != IntPtr.Zero ? Marshal.PtrToStringUTF8(entry.DeviceId) ?? string.Empty : string.Empty;
                var name = entry.Name != IntPtr.Zero ? Marshal.PtrToStringUTF8(entry.Name) ?? string.Empty : string.Empty;

                if (id.Length == 0)
                {
                    // A device we couldn't even get a stable ID for is not selectable -
                    // skip it rather than surfacing a half-built entry (matches the
                    // Windows enumerator's handling of a mid-enumeration COM failure).
                    continue;
                }

                results.Add(new AudioDeviceInfo(id, name, entry.IsDefault != 0));
            }

            return results;
        }
        finally
        {
            NativeMethods.msc_free_devices(devicesPtr, count);
        }
    }
}
