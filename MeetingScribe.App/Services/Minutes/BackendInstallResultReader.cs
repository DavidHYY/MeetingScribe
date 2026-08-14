namespace MeetingScribe.App.Services;

/// <summary>Outcome of the LAST time the MeetingScribe installer attempted to install a backend.</summary>
internal readonly record struct BackendInstallOutcome(bool Attempted, bool Succeeded, string Detail);

/// <summary>
/// Reads the installer's per-run sidecar file (installer\backends.iss's RunBackendInstalls /
/// BackendsWriteResultFile) at %LocalAppData%\MeetingScribeCS\logs\backends-install-result.txt,
/// so Settings can tell "never tried to install this" apart from "the installer tried and this
/// failed" - <see cref="CliAvailabilityChecker"/>'s live PATH probe alone can't make that
/// distinction, it only ever sees "missing right now" either way.
///
/// The file is overwritten by the installer on every run (see BackendsWriteResultFile), so this
/// only ever reflects the LAST install attempt, if any; a missing file simply means no
/// MeetingScribe installer has ever run the backends step on this machine, not that anything
/// failed - callers must treat "no outcome" and "attempted but failed" as different states.
/// </summary>
internal static class BackendInstallResultReader
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingScribeCS", "logs", "backends-install-result.txt");

    /// <param name="backendKey">One of "claude", "codex", "ollama" (installer's own naming, case-insensitive).</param>
    /// <returns>null if the sidecar doesn't exist, can't be read, or has no line for <paramref name="backendKey"/>.</returns>
    public static BackendInstallOutcome? TryRead(string backendKey)
    {
        string[] lines;
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            lines = File.ReadAllLines(FilePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in lines)
        {
            // Format: "<name>|<attempted 0/1>|<succeeded 0/1>|<detail>" - written by
            // BackendsWriteResultLine in installer\backends.iss. Comment/header lines (start
            // with '#' or "timestamp|") don't match any backend key and are skipped by the loop.
            var parts = line.Split('|', 4);
            if (parts.Length != 4 || !string.Equals(parts[0], backendKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new BackendInstallOutcome(parts[1] == "1", parts[2] == "1", parts[3]);
        }

        return null;
    }
}
