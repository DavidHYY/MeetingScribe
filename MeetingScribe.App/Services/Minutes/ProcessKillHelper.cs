using System.ComponentModel;
using System.Diagnostics;

namespace MeetingScribe.App.Services;

/// <summary>Best-effort process termination shared by <see cref="CliProcessRunner"/> and <see cref="CliAvailabilityChecker"/>.</summary>
internal static class ProcessKillHelper
{
    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and Kill(); nothing left to do.
        }
        catch (Win32Exception)
        {
            // Process could not be terminated (e.g. already exiting); nothing more we can do here.
        }
    }
}
