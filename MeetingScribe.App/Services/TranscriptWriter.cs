using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MeetingScribe.App.Models;

namespace MeetingScribe.App.Services;

/// <summary>
/// Renders a merged, chronologically-sorted transcript to the three on-disk formats the
/// output layout requires, plus the plain-text form fed to minutes generation. Every
/// write is explicit UTF-8 (no BOM) - never relies on the OS default codepage (cp950 on
/// this machine), which has caused real corruption with Japanese/Chinese/Polish content.
/// </summary>
public static class TranscriptWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Builds the plain-text form: one "[hh:mm:ss] [Mic|System] text" line per segment.
    /// This is both what gets written to transcript.txt and, verbatim, what is piped to
    /// minutes generation - so what the user sees on disk is exactly what Claude saw.
    /// </summary>
    public static string ToPlainText(IReadOnlyList<TranscriptLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append('[').Append(FormatTimestamp(line.Start)).Append("] [")
              .Append(line.SourceLabel).Append("] ")
              .Append(line.Text)
              .Append('\n');

            AppendTranslationLine(sb, line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends an indented "    EN: ..." line right after a transcript line that carries a live
    /// translation (see <see cref="TranscriptLine.TranslationStatus"/>), or a short bracketed
    /// marker when translation was attempted but did not produce text - never a silent blank, per
    /// the live-translation feature's requirement. A no-op (appends nothing) for
    /// <see cref="TranslationStatus.NotApplicable"/>, which is what every stage-2 accurate-pass
    /// line always is - so this is a pure no-op, byte-for-byte, on the pre-existing
    /// transcript.txt/minutes-prompt output path, which never carries translations.
    /// </summary>
    private static void AppendTranslationLine(StringBuilder sb, TranscriptLine line)
    {
        switch (line.TranslationStatus)
        {
            case TranslationStatus.Completed when !string.IsNullOrEmpty(line.Translation):
                sb.Append("    EN: ").Append(line.Translation).Append('\n');
                break;
            case TranslationStatus.Pending:
                sb.Append("    EN: (translation pending)\n");
                break;
            case TranslationStatus.Skipped:
                sb.Append("    EN: (translation skipped - translator fell behind)\n");
                break;
            case TranslationStatus.Failed:
                sb.Append("    EN: (translation failed")
                  .Append(string.IsNullOrEmpty(line.TranslationError) ? string.Empty : $": {line.TranslationError}")
                  .Append(")\n");
                break;
            // TranslationStatus.NotApplicable, or Completed with empty text: nothing to add.
        }
    }

    public static void WriteTxt(string path, IReadOnlyList<TranscriptLine> lines) =>
        File.WriteAllText(path, ToPlainText(lines), Utf8NoBom);

    public static void WriteVtt(string path, IReadOnlyList<TranscriptLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var sb = new StringBuilder();
        sb.Append("WEBVTT\n\n");

        foreach (var line in lines)
        {
            sb.Append(FormatVttTimestamp(line.Start)).Append(" --> ").Append(FormatVttTimestamp(line.End)).Append('\n');
            sb.Append('[').Append(line.SourceLabel).Append("] ").Append(line.Text).Append("\n\n");
        }

        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
    }

    public static void WriteJson(string path, IReadOnlyList<TranscriptLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var payload = lines.Select(l => new
        {
            source = l.SourceLabel,
            startSeconds = l.Start.TotalSeconds,
            endSeconds = l.End.TotalSeconds,
            text = l.Text,
            language = l.Language,
            probability = l.Probability,
            // Additive fields - always present but null/"NotApplicable" for every stage-2
            // accurate-pass line, since translation is a live-pass-only feature (see
            // TranscriptLine's remarks). Backward compatible: nothing in this repo deserializes
            // transcript.json back, so new fields cannot break an existing reader.
            translation = l.Translation,
            translationStatus = l.TranslationStatus.ToString(),
            translationError = l.TranslationError,
        });

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(path, json, Utf8NoBom);
    }

    private static string FormatTimestamp(TimeSpan t) =>
        t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatVttTimestamp(TimeSpan t) =>
        t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
