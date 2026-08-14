using System.IO;
using MeetingScribe.Audio;
using MeetingScribe.Whisper;

namespace MeetingScribe.App.Services;

/// <summary>One track's full accurate-pass result.</summary>
public sealed record FinalTrackResult(AudioSourceKind Source, TranscriptionResult Result);

/// <summary>Status update for the determinate final-pass progress bar.</summary>
public readonly record struct FinalProgress(string Status, double PercentComplete);

/// <summary>
/// Drives stage 2 (after Stop): re-transcribes each enabled track's saved WAV file with
/// the accurate model, sequentially (see <see cref="LiveTranscriptionEngine"/> for why
/// whisper calls are never run concurrently against one factory here). Tracks are
/// transcribed separately, not mixed, specifically so the resulting transcript keeps its
/// per-track (Mic/System) labels all the way through to the minutes prompt.
/// </summary>
public sealed class FinalTranscriptionEngine
{
    public async Task<IReadOnlyList<FinalTrackResult>> TranscribeTracksAsync(
        WhisperTranscriber transcriber,
        IReadOnlyList<(AudioSourceKind Source, string WavPath)> tracks,
        IProgress<FinalProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcriber);
        ArgumentNullException.ThrowIfNull(tracks);

        var results = new List<FinalTrackResult>(tracks.Count);

        for (var i = 0; i < tracks.Count; i++)
        {
            var (source, path) = tracks[i];
            var label = source == AudioSourceKind.Microphone ? "microphone" : "system audio";
            var indexLabel = tracks.Count > 1 ? $" ({i + 1} of {tracks.Count})" : string.Empty;

            progress?.Report(new FinalProgress(
                $"Transcribing {label} track{indexLabel}...",
                PercentComplete: i / (double)tracks.Count * 100.0));

            var fileProgress = new Progress<int>(p =>
            {
                var overall = (i + p / 100.0) / tracks.Count * 100.0;
                progress?.Report(new FinalProgress(
                    $"Transcribing {label} track{indexLabel}... {p}%",
                    overall));
            });

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);

            var result = await transcriber.TranscribeFileAsync(stream, fileProgress, cancellationToken)
                .ConfigureAwait(false);

            results.Add(new FinalTrackResult(source, result));
        }

        progress?.Report(new FinalProgress("Final transcription complete.", 100.0));
        return results;
    }
}
