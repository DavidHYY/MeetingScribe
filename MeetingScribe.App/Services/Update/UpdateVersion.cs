namespace MeetingScribe.App.Services.Update;

/// <summary>
/// Parses and compares MeetingScribe's release-version numbers (semantic, 3-part
/// Major.Minor.Patch, e.g. "1.1.0") - used to compare the running app's own assembly version
/// (from <see cref="Directory"/>-level <c>Directory.Build.props</c>' <c>&lt;Version&gt;</c>, baked
/// into every assembly by MSBuild) against a GitHub Release's <c>tag_name</c> (e.g. "v1.0.0").
///
/// Deliberately a small parser of its own rather than comparing raw <see cref="System.Version"/>
/// instances end-to-end: <see cref="System.Version.TryParse(string,out System.Version)"/> happily
/// accepts a 2-part string (leaving <c>Build</c> as -1), and the running assembly's own
/// <c>AssemblyVersion</c> always carries a 4th (<c>Revision</c>) field MSBuild appends from the
/// 3-part <c>&lt;Version&gt;</c> - comparing those two shapes directly can produce a wrong
/// ordering. Both sides are normalized to exactly Major.Minor.Patch here before any comparison.
/// </summary>
internal static class UpdateVersion
{
    /// <summary>
    /// Parses a version string, tolerating one leading 'v'/'V' (GitHub tag convention) and
    /// surrounding whitespace. Returns false (never throws) for anything that is not a valid
    /// non-negative Major.Minor[.Patch] numeric version - a malformed or missing
    /// <c>tag_name</c> from the GitHub API must not crash the update check.
    /// </summary>
    public static bool TryParse(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        if (!Version.TryParse(trimmed, out var parsed) || parsed.Major < 0 || parsed.Minor < 0)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    /// <summary>
    /// The running app's own version, read from its own entry assembly at runtime (never
    /// hardcoded anywhere), normalized to Major.Minor.Patch the same way <see cref="TryParse"/>
    /// normalizes a release tag so the two are always compared on equal footing regardless of
    /// the Revision field MSBuild appends to <c>AssemblyVersion</c>.
    /// </summary>
    public static Version GetRunningVersion()
    {
        var raw = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version
                  ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                  ?? new Version(0, 0, 0);
        return new Version(raw.Major, raw.Minor, Math.Max(raw.Build, 0));
    }

    /// <summary>True if <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(Version candidate, Version current) => candidate.CompareTo(current) > 0;
}
