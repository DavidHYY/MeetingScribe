using System.Runtime.InteropServices;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// P/Invoke surface for <c>libmeetingscribe_mac_audio.dylib</c> (built from
/// <c>native/meetingscribe_mac_audio.m</c> by <c>MeetingScribe.Audio.Mac.csproj</c>'s
/// <c>BuildMacNativeAudioHelper</c> target). Every function here matches
/// <c>native/meetingscribe_mac_audio.h</c> exactly - keep the two in sync.
/// </summary>
internal static unsafe class NativeMethods
{
    // DllImport("meetingscribe_mac_audio") resolves to "libmeetingscribe_mac_audio.dylib"
    // via .NET's default macOS native-library probing rules (prefixes "lib", appends
    // ".dylib"), which is exactly the file BuildMacNativeAudioHelper produces and
    // CopyToOutputDirectory places next to this assembly.
    private const string LibraryName = "meetingscribe_mac_audio";

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDeviceInfo
    {
        public IntPtr DeviceId;
        public IntPtr Name;
        public int IsDefault;
    }

    internal enum PermissionStatus
    {
        NotDetermined = 0,
        Granted = 1,
        Denied = 2,
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int msc_list_capture_devices(out IntPtr outDevices);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int msc_list_render_devices(out IntPtr outDevices);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_free_devices(IntPtr devices, int count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_free_string(IntPtr s);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PermissionStatus msc_mic_permission_status();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PermissionStatus msc_request_mic_permission();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PermissionStatus msc_screen_permission_status();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_request_screen_permission();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr msc_mic_start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceId,
        delegate* unmanaged[Cdecl]<short*, int, IntPtr, void> callback,
        delegate* unmanaged[Cdecl]<byte*, IntPtr, void> errorCallback,
        IntPtr userData,
        out IntPtr outError);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_mic_stop(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_mic_free(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr msc_system_start(
        delegate* unmanaged[Cdecl]<short*, int, IntPtr, void> callback,
        delegate* unmanaged[Cdecl]<byte*, IntPtr, void> errorCallback,
        IntPtr userData,
        out IntPtr outError);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_system_stop(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void msc_system_free(IntPtr session);
}
