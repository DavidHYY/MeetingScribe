namespace MeetingScribe.Whisper;

/// <summary>
/// Splits a full buffer of 16kHz mono float32 samples into whisper.cpp-sized chunks for
/// <see cref="WhisperTranscriber.TranscribeFileAsync"/>, choosing cut points at local
/// energy minima (silence/pause points) instead of hard fixed offsets so a chunk boundary
/// does not land mid-word or mid-sentence.
/// </summary>
/// <remarks>
/// This exists because whole-file transcription (a single <c>ProcessAsync</c> call over
/// 20+ minutes of audio) reproduced whisper.cpp's hallucination-loop failure mode: once the
/// greedy decoder latches onto a wrong phrase, <c>no_context</c> only clears prompt history
/// once at the start of the call, so the per-window context rebuild
/// (whisper.cpp <c>whisper.cpp:7627-7638</c>) keeps re-seeding the bad phrase and it repeats
/// for the rest of the file (measured: 373/513 segments, 72.7%, on the real 26-minute
/// recording). The proven fix, already used by <see cref="StreamingWhisperSession"/> for live
/// capture, is many short <c>ProcessAsync</c> calls: <c>no_context</c> resets prompt history
/// at the start of every call, so one bad chunk cannot poison the ones after it.
/// </remarks>
public static class AudioChunkPlanner
{
    /// <summary>
    /// Width of the short-time energy analysis frame used to find a low-energy cut point.
    /// 100ms is short enough to land inside a natural pause between sentences/breaths without
    /// being so short that it reacts to individual phoneme dips within a word.
    /// </summary>
    public const double EnergyFrameSeconds = 0.1;

    /// <summary>
    /// Splits <paramref name="samples"/> into chunks targeting <paramref name="targetChunkSeconds"/>
    /// with a hard cap of <paramref name="maxChunkSeconds"/>, cutting at the quietest point found
    /// in the search window between those two lengths. A trailing chunk shorter than
    /// <paramref name="minChunkSeconds"/> is folded into the previous chunk rather than issued as
    /// its own near-empty <c>ProcessAsync</c> call.
    /// </summary>
    /// <param name="samples">Full 16kHz mono float32 sample buffer for the whole file.</param>
    /// <param name="targetChunkSeconds">Soft target chunk length, in seconds.</param>
    /// <param name="maxChunkSeconds">Hard cap chunk length, in seconds; used verbatim (no silence found) if no quieter point exists in the search window.</param>
    /// <param name="minChunkSeconds">Minimum length a trailing chunk must have to stand on its own; shorter tails are merged into the previous chunk.</param>
    /// <param name="sampleRateHz">Sample rate of <paramref name="samples"/>. Must match whisper.cpp's required rate (16kHz) for the caller to get meaningful audio, but this method itself is rate-agnostic.</param>
    /// <returns>
    /// Chunk ranges in chronological order, each a (Start, Length) pair in sample counts.
    /// Empty when <paramref name="samples"/> is empty. A single chunk covering the whole
    /// buffer when its length is already within <paramref name="maxChunkSeconds"/>.
    /// </returns>
    public static IReadOnlyList<(int Start, int Length)> PlanChunks(
        ReadOnlyMemory<float> samples,
        double targetChunkSeconds,
        double maxChunkSeconds,
        double minChunkSeconds,
        int sampleRateHz)
    {
        if (targetChunkSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetChunkSeconds), targetChunkSeconds, "Must be positive.");
        }

        if (maxChunkSeconds < targetChunkSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxChunkSeconds), maxChunkSeconds, "Must be >= targetChunkSeconds.");
        }

        if (minChunkSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minChunkSeconds), minChunkSeconds, "Must be >= 0.");
        }

        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), sampleRateHz, "Must be positive.");
        }

        var total = samples.Length;
        if (total == 0)
        {
            return [];
        }

        var targetSamples = (int)Math.Round(targetChunkSeconds * sampleRateHz);
        var maxSamples = (int)Math.Round(maxChunkSeconds * sampleRateHz);
        var minSamples = (int)Math.Round(minChunkSeconds * sampleRateHz);

        if (total <= maxSamples)
        {
            return [(0, total)];
        }

        var span = samples.Span;
        var boundaries = new List<int> { 0 };
        var position = 0;

        while (total - position > maxSamples)
        {
            var searchStart = Math.Min(position + targetSamples, total);
            var searchEnd = Math.Min(position + maxSamples, total);

            var cut = FindQuietestCut(span, searchStart, searchEnd, sampleRateHz) ?? searchEnd;
            if (cut <= position)
            {
                // Defensive: never let a degenerate cut produce a zero/negative-length chunk.
                cut = searchEnd;
            }

            boundaries.Add(cut);
            position = cut;
        }

        boundaries.Add(total);

        // Fold a too-short trailing chunk into the one before it, rather than issuing a
        // near-empty ProcessAsync call that mostly pays whisper's 30s padding cost for
        // almost nothing decoded.
        if (boundaries.Count > 2 && boundaries[^1] - boundaries[^2] < minSamples)
        {
            boundaries.RemoveAt(boundaries.Count - 2);
        }

        var result = new List<(int Start, int Length)>(boundaries.Count - 1);
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var start = boundaries[i];
            var length = boundaries[i + 1] - start;
            if (length > 0)
            {
                result.Add((start, length));
            }
        }

        return result;
    }

    /// <summary>
    /// Picks up to <paramref name="maxCount"/> chunks, in chronological order, whose average
    /// energy is at or above the median energy across all chunks - i.e. chunks that look like
    /// real content rather than the recording's quiet opening (silence/room tone before anyone
    /// starts speaking, which is what produced a bogus "nn" language detection on the real
    /// meeting recording this was built against). Median rather than a fixed RMS threshold
    /// because "quiet" is relative to each recording's own gain/mic setup, not an absolute
    /// level; on a real meeting recording the active-speech region dominates total duration,
    /// so it also dominates the median.
    /// </summary>
    /// <remarks>
    /// Returns multiple chunks, not one, because a single speech-bearing chunk is not
    /// guaranteed to be representative of the file's dominant language: measured on the real
    /// meeting recording this was built against, the first speech-like chunk (t=140s) is a
    /// brief English aside ("Let's speak Japanese") inside an otherwise Japanese-dominant
    /// 26-minute meeting - detecting language from that one chunk alone would have locked the
    /// entire file to English. Sampling several chunks lets the caller take a majority vote
    /// instead of trusting one sample.
    /// </remarks>
    /// <returns>Chunk indices into <paramref name="chunks"/>, in chronological order. Empty when <paramref name="chunks"/> is empty.</returns>
    public static IReadOnlyList<int> SelectSpeechLikeChunkIndices(
        ReadOnlyMemory<float> samples,
        IReadOnlyList<(int Start, int Length)> chunks,
        int maxCount)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Must be positive.");
        }

        if (chunks.Count == 0)
        {
            return [];
        }

        var rmsPerChunk = ComputeChunkRms(samples, chunks);
        var median = Median(rmsPerChunk);

        var result = new List<int>(Math.Min(maxCount, chunks.Count));
        for (var i = 0; i < rmsPerChunk.Count && result.Count < maxCount; i++)
        {
            if (rmsPerChunk[i] >= median)
            {
                result.Add(i);
            }
        }

        if (result.Count == 0)
        {
            // Every chunk tied exactly (e.g. a single-chunk file, or perfectly uniform energy):
            // fall back to the first chunk rather than returning nothing.
            result.Add(0);
        }

        return result;
    }

    /// <summary>
    /// Per-chunk RMS (root-mean-square amplitude), chronological order, one value per entry in
    /// <paramref name="chunks"/>. Shared energy primitive used by both
    /// <see cref="SelectSpeechLikeChunkIndices"/> (language-ID sampling) and
    /// <see cref="CountLeadingSilentChunks"/> (silent lead-in skip), so both features measure
    /// energy the same way.
    /// </summary>
    public static IReadOnlyList<double> ComputeChunkRms(
        ReadOnlyMemory<float> samples, IReadOnlyList<(int Start, int Length)> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var span = samples.Span;
        var result = new double[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var (start, length) = chunks[i];
            result[i] = ComputeRms(span.Slice(start, length));
        }

        return result;
    }

    /// <summary>
    /// Counts how many chunks at the very start of the file are a near-silent lead-in - room
    /// tone before anyone starts speaking, e.g. people waiting for a meeting to start - and
    /// should be skipped rather than sent to whisper.cpp at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a *lead-in* skip, not a file-wide silence gate: stops counting at the first
    /// chunk whose RMS reaches <paramref name="silenceRatio"/> of the file-wide median (see
    /// <see cref="ComputeChunkRms"/>), and every later chunk is transcribed regardless of its own
    /// RMS, even if it later dips quiet again. This was a deliberate, measured decision, not the
    /// simpler "skip any chunk below threshold, wherever it is" reading: on the real 26-minute
    /// meeting recording this was built against, several chunks well inside the meeting measured
    /// as quiet as the pre-meeting silence — a brief "OK" acknowledgement surrounded by a pause,
    /// and continuous but soft-spoken dialogue near the very end of the meeting (t=25:03, close
    /// to the file's last segment at 26:14) — both around RMS 0.017-0.023, overlapping the
    /// 0.021-0.038 range measured for the actual pre-meeting silence. A threshold applied
    /// file-wide, at any ratio that also fully covers the lead-in, would have deleted that real
    /// content. Restricting the skip to a lead-in run from t=0 makes that structurally
    /// impossible: real content elsewhere in the file is never evaluated against the threshold.
    /// </para>
    /// <para>
    /// Threshold is relative to this file's own median chunk energy, not an absolute constant,
    /// because "quiet" depends on each recording's own gain/mic/room setup. This also
    /// self-limits on an unusual file: if the whole recording is uniformly quiet (soft speakers,
    /// distant mic), the median is dragged down with it, so real speech chunks stay close to
    /// 100% of median and are not mistaken for the lead-in.
    /// </para>
    /// </remarks>
    /// <param name="rmsPerChunk">Per-chunk RMS, chronological order — see <see cref="ComputeChunkRms"/>.</param>
    /// <param name="silenceRatio">
    /// A chunk counts as part of the silent lead-in when its RMS is below this fraction of the
    /// file-wide median RMS. Must be in (0, 1]. Measured on the real meeting recording this was
    /// tuned against: every lead-in chunk measured 34%-61% of the file median, while the first
    /// real-speech chunk measured 114% of median — 0.65 (see
    /// <see cref="WhisperTranscriberOptions.SilentLeadInRatio"/>'s default) sits with margin on
    /// both sides of that gap. Values close to 1 treat almost everything as part of the lead-in
    /// (risks eating real speech); values close to 0 almost never trigger (safe, but does
    /// nothing).
    /// </param>
    /// <returns>
    /// Number of leading chunks to skip, in [0, rmsPerChunk.Count]. 0 when
    /// <paramref name="rmsPerChunk"/> is empty or its first entry is already at/above threshold.
    /// </returns>
    public static int CountLeadingSilentChunks(IReadOnlyList<double> rmsPerChunk, double silenceRatio)
    {
        ArgumentNullException.ThrowIfNull(rmsPerChunk);

        if (silenceRatio <= 0 || silenceRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceRatio), silenceRatio, "Must be in (0, 1].");
        }

        if (rmsPerChunk.Count == 0)
        {
            return 0;
        }

        var threshold = Median(rmsPerChunk) * silenceRatio;

        var count = 0;
        while (count < rmsPerChunk.Count && rmsPerChunk[count] < threshold)
        {
            count++;
        }

        return count;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static int? FindQuietestCut(ReadOnlySpan<float> samples, int searchStart, int searchEnd, int sampleRateHz)
    {
        if (searchEnd <= searchStart)
        {
            return null;
        }

        var frameSamples = Math.Max(1, (int)(EnergyFrameSeconds * sampleRateHz));
        if (frameSamples > searchEnd - searchStart)
        {
            // Search window narrower than one analysis frame: nothing meaningful to compare.
            return null;
        }

        var hopSamples = Math.Max(1, frameSamples / 2);

        int? bestFrameStart = null;
        var bestEnergy = double.MaxValue;

        for (var frameStart = searchStart; frameStart + frameSamples <= searchEnd; frameStart += hopSamples)
        {
            var energy = ComputeRms(samples.Slice(frameStart, frameSamples));
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestFrameStart = frameStart;
            }
        }

        return bestFrameStart is { } start ? start + (frameSamples / 2) : null;
    }

    private static double ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        double sumSquares = 0;
        foreach (var s in samples)
        {
            sumSquares += (double)s * s;
        }

        return Math.Sqrt(sumSquares / samples.Length);
    }
}
