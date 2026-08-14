namespace MeetingScribe.App.Services;

/// <summary>
/// Prompt rendering and output cleanup shared by every <see cref="IMinutesProvider"/>, so a fix
/// applies uniformly instead of needing to be re-implemented (and potentially missed) per
/// provider - see <see cref="StripCodeFence"/>'s doc comment for the real bug this guards
/// against.
/// </summary>
public static class MinutesTextUtils
{
    /// <summary>
    /// Fills <c>{title}</c>/<c>{date}</c> placeholders via plain <see cref="string.Replace"/>
    /// rather than <see cref="string.Format"/> - the template is user-edited free text and
    /// may legitimately contain other literal <c>{</c>/<c>}</c> characters (e.g. inside a
    /// Markdown table example), which would throw on <c>Format</c>.
    /// </summary>
    public static string RenderPrompt(string template, string title, string date) =>
        template.Replace("{title}", title).Replace("{date}", date);

    /// <summary>
    /// Strips a single wrapping ``` / ```markdown / ```md code fence around the entire
    /// output, if present. Leaves the text untouched if it isn't fenced, or if a closing
    /// fence can't be found (never guesses at truncated input). This is the fix for a real bug:
    /// the Python prototype shipped a wrapping fence verbatim into minutes.md.
    /// </summary>
    internal static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lines = trimmed.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2)
        {
            return trimmed;
        }

        var closingIndex = -1;
        for (var i = lines.Length - 1; i >= 1; i--)
        {
            if (lines[i].Trim() == "```")
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 1)
        {
            return trimmed;
        }

        var inner = string.Join('\n', lines[1..closingIndex]);
        return inner.Trim();
    }
}
