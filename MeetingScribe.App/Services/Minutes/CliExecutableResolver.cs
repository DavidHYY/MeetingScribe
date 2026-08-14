namespace MeetingScribe.App.Services;

/// <summary>
/// Resolves a bare command name (e.g. "codex") to an actual launchable file, the way an
/// interactive shell would, before handing it to <see cref="System.Diagnostics.Process.Start"/>.
///
/// This matters on Windows specifically, and is not hypothetical: verified on the development
/// machine that <c>codex</c> (npm-installed) ships as <c>codex.cmd</c> (a batch shim) plus a
/// bare, extension-less <c>codex</c> POSIX shell script for Unix - no <c>.exe</c> at all. Win32
/// <c>CreateProcess</c> - what <c>Process.Start</c> uses when <c>UseShellExecute=false</c>,
/// which redirected-stdio process launching requires - does <b>not</b> perform the PATHEXT
/// extension search a command shell does for a bare name. Confirmed by direct reproduction:
/// <c>Process.Start(FileName: "codex", UseShellExecute: false)</c> throws
/// <see cref="System.ComponentModel.Win32Exception"/> ("file not found") on this machine even
/// though <c>codex --version</c> runs fine from a shell, while <c>claude</c> (installed as an
/// actual <c>.exe</c>) was unaffected - which is why this bug was latent until Codex was wired
/// up. Separately confirmed that <c>Process.Start</c> DOES run a <c>.cmd</c> file correctly when
/// given its full resolved path directly (.NET transparently handles the
/// <c>cmd.exe /c</c> indirection for a <c>.cmd</c>/<c>.bat</c> <c>FileName</c>), so resolving the
/// extension ourselves and handing Process.Start the full path - rather than re-implementing a
/// <c>cmd.exe /c</c> wrapper with its own quoting rules - is sufficient and avoids introducing a
/// second, more fragile, code path.
/// </summary>
internal static class CliExecutableResolver
{
    /// <summary>
    /// Returns the resolved full path if a match is found on PATH, otherwise the original
    /// <paramref name="command"/> unchanged - so the existing Win32Exception-based
    /// "not found on PATH" handling in <see cref="CliProcessRunner"/>/<see cref="CliAvailabilityChecker"/>
    /// still applies for a genuinely missing CLI.
    /// </summary>
    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command))
        {
            return command;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return command;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        foreach (var directory in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bareCandidate = Path.Combine(directory, command);

            // On Windows, PATHEXT-suffixed candidates MUST be tried before the bare name: an
            // extension-less file on disk is not necessarily (and for an npm shim, typically
            // is not) something CreateProcess can launch at all - it can be a POSIX shell script
            // meant for a Unix shebang, present purely so the same npm package also works via a
            // Unix PATH lookup. Reproduced directly on this machine: bare-name-first here matched
            // exactly that POSIX "codex" shim (a real file, but not launchable by CreateProcess)
            // before ever trying "codex.cmd", so the search order below is not a style choice.
            foreach (var extension in extensions)
            {
                var candidate = bareCandidate + extension;
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Bare name: the only option on non-Windows (no PATHEXT concept there - the
            // extensions list is empty and this is the sole check), and on Windows a fallback
            // for a name that already carries a real executable extension (e.g. "python.exe").
            if (File.Exists(bareCandidate))
            {
                return bareCandidate;
            }
        }

        return command;
    }
}
