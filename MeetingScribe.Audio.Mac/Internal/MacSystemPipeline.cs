using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MeetingScribe.Audio.Internal;

/// <summary>System-audio capture via ScreenCaptureKit (<c>msc_system_start</c>/<c>msc_system_stop</c>).</summary>
internal sealed unsafe class MacSystemPipeline : MacSourcePipeline
{
    private IntPtr _session;

    public MacSystemPipeline(
        string wavPath,
        Action<AudioSourceKind, AudioLevel> onLevel,
        Action<AudioSourceKind, Exception> onError,
        Action<AudioSourceKind, short[]>? onSamples)
        : base(AudioSourceKind.SystemAudio, wavPath, onLevel, onError, onSamples)
    {
    }

    public override void Start()
    {
        var session = NativeMethods.msc_system_start(&OnAudioSamples, &OnError, UserData, out var errorPtr);
        if (session == IntPtr.Zero)
        {
            var message = ReadAndFreeError(errorPtr);
            throw new AudioDeviceNotFoundException($"Could not start system-audio capture: {message}");
        }

        _session = session;
    }

    protected override void StopCore()
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.msc_system_stop(_session);
        NativeMethods.msc_system_free(_session);
        _session = IntPtr.Zero;
    }

    private static string ReadAndFreeError(IntPtr errorPtr)
    {
        if (errorPtr == IntPtr.Zero)
        {
            return "unknown error (native layer did not report a message)";
        }

        var message = Marshal.PtrToStringUTF8(errorPtr) ?? "unknown error";
        NativeMethods.msc_free_string(errorPtr);
        return message;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAudioSamples(short* samples, int count, IntPtr userData)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        if (GCHandle.FromIntPtr(userData).Target is MacSourcePipeline pipeline)
        {
            pipeline.HandleSamples(samples, count);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(byte* message, IntPtr userData)
    {
        if (userData == IntPtr.Zero)
        {
            return;
        }

        if (GCHandle.FromIntPtr(userData).Target is MacSourcePipeline pipeline)
        {
            var text = message != null ? Marshal.PtrToStringUTF8((IntPtr)message) : null;
            pipeline.HandleError(text ?? "unknown native error");
        }
    }
}
