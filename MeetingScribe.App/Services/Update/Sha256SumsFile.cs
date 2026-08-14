namespace MeetingScribe.App.Services.Update;

/// <summary>
/// Parses a sha256sum-style sidecar file: one line per artifact, "&lt;hex-hash&gt;  &lt;filename&gt;"
/// (two spaces, the format <c>installer\build-installer.ps1</c>'s <c>Get-FileHash</c> step emits
/// as <c>SHA256SUMS.txt</c>). Tolerant of a single space instead of two, an optional leading '*'
/// binary-mode marker some sha256sum implementations emit, extra whitespace, and blank/comment
/// lines - this is a small trust boundary (bytes downloaded from a public GitHub release get
/// compared against this), so parsing is deliberately permissive about formatting but strict
/// about the actual hash/filename match.
/// </summary>
internal static class Sha256SumsFile
{
    /// <summary>
    /// Returns the hex SHA-256 hash listed for <paramref name="fileName"/> (case-insensitive
    /// filename match), or null if the file is not listed - e.g. the sidecar is empty, corrupt,
    /// or simply does not mention this artifact. Never throws.
    /// </summary>
    public static string? FindHash(string sumsFileContent, string fileName)
    {
        ArgumentNullException.ThrowIfNull(sumsFileContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        foreach (var rawLine in sumsFileContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            var hash = parts[0].Trim();
            var name = parts[1].Trim().TrimStart('*').Trim();
            if (hash.Length == 64 && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }

        return null;
    }
}
