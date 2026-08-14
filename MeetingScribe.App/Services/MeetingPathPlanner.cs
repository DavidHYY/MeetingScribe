using System.Globalization;
using System.IO;
using System.Text;

namespace MeetingScribe.App.Services;

/// <summary>
/// Builds meeting output folder paths compatible with the existing Python app's layout:
/// <c>&lt;output_root&gt;\&lt;YYYY-MM-DD_HHMM&gt;_&lt;title&gt;\</c>.
/// </summary>
public static class MeetingPathPlanner
{
    /// <summary>
    /// Builds the folder path for a new meeting. If the exact path already exists (e.g.
    /// two meetings started in the same minute with the same title), a numeric suffix is
    /// appended so an existing meeting's files are never overwritten.
    /// </summary>
    public static string BuildMeetingFolder(string outputRoot, string title, DateTime localStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var stamp = localStart.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
        var safeTitle = SanitizeForPath(title);
        var baseName = safeTitle.Length == 0 ? stamp : $"{stamp}_{safeTitle}";

        var candidate = Path.Combine(outputRoot, baseName);
        var suffix = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(outputRoot, $"{baseName}_{suffix}");
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeForPath(string title)
    {
        var trimmed = title.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(trimmed.Length);

        foreach (var c in trimmed)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        // Collapse the common case of trailing/leading underscores left behind by
        // sanitizing something like "Q3 review: budget?" -> "Q3 review_ budget_".
        return sb.ToString().Trim('_', ' ');
    }
}
