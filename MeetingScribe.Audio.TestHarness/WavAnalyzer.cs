using System.Buffers.Binary;

namespace MeetingScribe.Audio.TestHarness;

/// <summary>
/// Minimal, dependency-free canonical-PCM-WAV reader used only for this harness's
/// read-back verification step. Deliberately not NAudio: this project must build and
/// run identically on Windows and macOS, and NAudio is Windows-only. Parses the RIFF/
/// fmt/data chunk layout that both <c>MeetingScribe.Audio.Windows</c>'s
/// <c>WaveFileWriter</c> and <c>MeetingScribe.Audio.Mac</c>'s <c>PcmWavWriter</c>
/// produce (16-bit PCM only - both writers only ever emit that).
/// </summary>
internal static class WavAnalyzer
{
    public readonly record struct Result(int SampleRate, int Channels, int BitsPerSample, double DurationSeconds, double Rms, double Peak);

    public static Result Analyze(string path)
    {
        var dataBytes = ReadDataChunk(path, out var sampleRate, out var channels, out var bitsPerSample);
        if (bitsPerSample != 16)
        {
            throw new InvalidDataException($"'{path}': only 16-bit PCM is supported by this analyzer (got {bitsPerSample}-bit).");
        }

        var sampleCount = dataBytes.Length / sizeof(short);
        double sumSquares = 0;
        var peak = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(i * sizeof(short), sizeof(short)));
            sumSquares += (double)sample * sample;
            var abs = Math.Abs((int)sample);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        var framesPerChannel = channels > 0 ? sampleCount / channels : 0;
        var durationSeconds = sampleRate > 0 ? framesPerChannel / (double)sampleRate : 0;
        var rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) / 32768.0 : 0;
        var peakNorm = peak / 32768.0;

        return new Result(sampleRate, channels, bitsPerSample, durationSeconds, rms, peakNorm);
    }

    /// <summary>Reads every 16-bit PCM sample from the data chunk, ignoring channel layout (our writers are always mono).</summary>
    public static short[] ReadRawSamples(string path)
    {
        var dataBytes = ReadDataChunk(path, out _, out _, out var bitsPerSample);
        if (bitsPerSample != 16)
        {
            throw new InvalidDataException($"'{path}': only 16-bit PCM is supported by this analyzer (got {bitsPerSample}-bit).");
        }

        var sampleCount = dataBytes.Length / sizeof(short);
        var samples = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(i * sizeof(short), sizeof(short)));
        }

        return samples;
    }

    /// <summary>Pearson correlation coefficient over the overlapping length of two sample arrays, in [-1, 1].</summary>
    public static double Correlate(short[] a, short[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0)
        {
            return 0;
        }

        double sumA = 0, sumB = 0;
        for (var i = 0; i < n; i++)
        {
            sumA += a[i];
            sumB += b[i];
        }

        var meanA = sumA / n;
        var meanB = sumB / n;

        double cov = 0, varA = 0, varB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        if (varA <= 0 || varB <= 0)
        {
            return 0;
        }

        return cov / Math.Sqrt(varA * varB);
    }

    /// <summary>Walks the RIFF chunk list and returns the raw bytes of the data chunk, plus the fmt chunk's fields.</summary>
    private static byte[] ReadDataChunk(string path, out int sampleRate, out int channels, out int bitsPerSample)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);

        var riffTag = reader.ReadBytes(4);
        if (!Matches(riffTag, "RIFF"u8))
        {
            throw new InvalidDataException($"'{path}' is not a RIFF file (bad magic).");
        }

        reader.ReadUInt32(); // RIFF chunk size - not needed, we read to actual EOF/data-size below.

        var waveTag = reader.ReadBytes(4);
        if (!Matches(waveTag, "WAVE"u8))
        {
            throw new InvalidDataException($"'{path}' is not a WAVE file.");
        }

        sampleRate = 0;
        channels = 0;
        bitsPerSample = 0;
        var haveFmt = false;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = reader.ReadBytes(4);
            var declaredChunkSize = reader.ReadUInt32();
            // Tolerate a truncated file (e.g. a crash mid-write left the data chunk
            // size header stale/larger than what's actually on disk) - requirement #5:
            // a truncated file must still be readable. Clamp to what's actually left.
            var bytesRemaining = stream.Length - stream.Position;
            var chunkSize = declaredChunkSize > bytesRemaining ? (uint)bytesRemaining : declaredChunkSize;

            if (Matches(chunkId, "fmt "u8))
            {
                var chunkStart = stream.Position;
                reader.ReadUInt16(); // AudioFormat
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32(); // ByteRate
                reader.ReadUInt16(); // BlockAlign
                bitsPerSample = reader.ReadUInt16();
                stream.Position = chunkStart + chunkSize;
                haveFmt = true;
            }
            else if (Matches(chunkId, "data"u8))
            {
                if (!haveFmt)
                {
                    throw new InvalidDataException($"'{path}': data chunk appeared before fmt chunk.");
                }

                return reader.ReadBytes((int)chunkSize);
            }
            else
            {
                stream.Position += chunkSize;
            }

            // WAV chunks are word-aligned; skip the pad byte if the chunk size was odd.
            if (chunkSize % 2 == 1 && stream.Position < stream.Length)
            {
                stream.Position += 1;
            }
        }

        throw new InvalidDataException($"'{path}': no data chunk found.");
    }

    private static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) => actual.SequenceEqual(expected);
}
