using MeetingScribe.App.Models;

namespace MeetingScribe.App.Services;

/// <summary>Merges separately-transcribed Mic/System tracks into one chronologically-sorted transcript.</summary>
public static class TranscriptMerger
{
    public static IReadOnlyList<TranscriptLine> Merge(IEnumerable<FinalTrackResult> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var lines = new List<TranscriptLine>();
        foreach (var track in tracks)
        {
            foreach (var segment in track.Result.Segments)
            {
                var text = segment.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                lines.Add(new TranscriptLine(
                    track.Source,
                    segment.Start,
                    segment.End,
                    text,
                    segment.Language,
                    segment.Probability));
            }
        }

        // Stable sort by start time; when two segments from different tracks start at
        // the same instant, keep their original relative order (List<T>.Sort is not
        // stable, so use OrderBy which is).
        return [.. lines.OrderBy(l => l.Start)];
    }
}
