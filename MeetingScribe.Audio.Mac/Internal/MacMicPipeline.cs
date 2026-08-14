using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MeetingScribe.Audio.Internal;

/// <summary>Microphone capture via AVFoundation (<c>msc_mic_start</c>/<c>msc_mic_stop</c>).</summary>
internal sealed unsafe class MacMicPipeline : MacSourcePipeline
{
    private readonly string? _deviceId;
    private IntPtr _session;

    public MacMicPipeline(
        string? deviceId,
        string wavPath,
        Action<AudioSourceKind, AudioLevel> onLevel,
        Action<AudioSourceKind, Exception> onError,
        Action<AudioSourceKind, short[]>? onSamples)
        : base(AudioSourceKind.Microphone, wavPath, onLevel, onError, onSamples)
    {
        _deviceId = deviceId;
    }

    public override void Start()
    {
        var session = NativeMethods.msc_mic_start(_deviceId, &OnAudioSamples, &OnError, UserData, out var errorPtr);
        if (session == IntPtr.Zero)
        {
            var message = ReadAndFreeError(errorPtr);
            throw new AudioDeviceNotFoundException($"Could not start microphone capture: {message}");
        }

        _session = session;
    }

    protected override void StopCore()
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.msc_mic_stop(_session);
        NativeMethods.msc_mic_free(_session);
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
