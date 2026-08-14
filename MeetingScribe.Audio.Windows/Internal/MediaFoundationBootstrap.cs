using NAudio.MediaFoundation;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Process-wide, once-only <c>MFStartup</c> call. Windows Media Foundation is an OS
/// component (present on every Windows 11 install, no admin rights required); we start
/// it once and deliberately never call <c>MediaFoundationApi.Shutdown()</c>, since a
/// second <see cref="MeetingRecorder"/> instance (or another component in the same
/// process) may still have a live <c>MediaFoundationResampler</c> depending on it.
/// </summary>
internal static class MediaFoundationBootstrap
{
    private static int _started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            MediaFoundationApi.Startup();
        }
    }
}
