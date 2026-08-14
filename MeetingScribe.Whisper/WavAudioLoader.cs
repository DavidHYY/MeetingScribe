using System.Text;

namespace MeetingScribe.Whisper;

/// <summary>
/// Minimal, dependency-free RIFF/WAV reader that decodes to 16kHz mono float32 samples in
/// [-1, 1] — the exact format whisper.cpp requires.
/// </summary>
/// <remarks>
/// Whisper.net's own <see cref="Whisper.net.Wave.WaveParser"/> throws
/// <see cref="Whisper.net.Wave.NotSupportedWaveException"/> on anything that isn't already
/// exactly 16kHz. That is not a safe assumption for real capture pipelines — mics commonly
/// default to 44.1kHz/48kHz — so this loader accepts standard PCM/IEEE-float WAV at any
/// sample rate, channel count and bit depth, and resamples/downmixes to what whisper.cpp
/// needs. Resampling uses linear interpolation: adequate for feeding a speech model (whisper
/// itself is not sensitive to the small high-frequency artifacts linear interpolation
/// introduces) without pulling in a DSP dependency for this.
/// </remarks>
public static class WavAudioLoader
{
    public const int TargetSampleRateHz = 16_000;

    /// <summary>
    /// Reads a WAV stream fully and returns 16kHz mono float32 samples plus the resulting
    /// audio duration. Works with non-seekable streams (chunk skipping falls back to a
    /// read-and-discard loop when <see cref="Stream.CanSeek"/> is false).
    /// </summary>
    public static async Task<(float[] Samples, TimeSpan Duration)> LoadMono16kAsync(
        Stream wavStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavStream);

        var (raw, sampleRate, channels) = await ReadRawAsync(wavStream, cancellationToken).ConfigureAwait(false);

        var mono = channels == 1 ? raw : DownmixToMono(raw, channels);
        var resampled = sampleRate == TargetSampleRateHz ? mono : Resample(mono, sampleRate, TargetSampleRateHz);

        var duration = TimeSpan.FromSeconds(resampled.Length / (double)TargetSampleRateHz);
        return (resampled, duration);
    }

    private static async Task<(float[] Samples, int SampleRate, int Channels)> ReadRawAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        await ReadExactOrThrowAsync(stream, header, "the RIFF header", cancellationToken).ConfigureAwait(false);

        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
        {
            throw new InvalidDataException("Not a RIFF file (missing 'RIFF' magic).");
        }

        if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E')
        {
            throw new InvalidDataException("Not a WAVE file (missing 'WAVE' magic).");
        }

        short audioFormat = 0;
        short numChannels = 0;
        short bitsPerSample = 0;
        var sampleRate = 0;
        byte[]? dataBytes = null;

        var chunkHeader = new byte[8];
        while (true)
        {
            try
            {
                await stream.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break; // Clean end of the chunk list.
            }

            var chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var chunkSize = BitConverter.ToInt32(chunkHeader, 4);

            if (chunkSize < 0)
            {
                throw new InvalidDataException($"WAV chunk '{chunkId}' reports a negative size ({chunkSize}).");
            }

            switch (chunkId)
            {
                case "fmt ":
                    var fmt = new byte[chunkSize];
                    await ReadExactOrThrowAsync(stream, fmt, "the 'fmt ' chunk", cancellationToken).ConfigureAwait(false);

                    if (fmt.Length < 16)
                    {
                        throw new InvalidDataException($"WAV 'fmt ' chunk is too small ({fmt.Length} bytes, need >= 16).");
                    }

                    audioFormat = BitConverter.ToInt16(fmt, 0);
                    numChannels = BitConverter.ToInt16(fmt, 2);
                    sampleRate = BitConverter.ToInt32(fmt, 4);
                    bitsPerSample = BitConverter.ToInt16(fmt, 14);

                    if ((chunkSize & 1) == 1)
                    {
                        await SkipAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case "data":
                    dataBytes = new byte[chunkSize];
                    await ReadExactOrThrowAsync(stream, dataBytes, "the 'data' chunk", cancellationToken).ConfigureAwait(false);

                    if ((chunkSize & 1) == 1)
                    {
                        await SkipAsync(stream, 1, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                default:
                    // Word-aligned per the RIFF spec: an odd-sized chunk has one padding byte.
                    await SkipAsync(stream, chunkSize + (chunkSize & 1), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (audioFormat == 0)
        {
            throw new InvalidDataException("WAV file has no 'fmt ' chunk.");
        }

        if (dataBytes is null)
        {
            throw new InvalidDataException("WAV file has no 'data' chunk.");
        }

        if (numChannels <= 0)
        {
            throw new InvalidDataException($"WAV 'fmt ' chunk reports an invalid channel count: {numChannels}.");
        }

        if (sampleRate <= 0)
        {
            throw new InvalidDataException($"WAV 'fmt ' chunk reports an invalid sample rate: {sampleRate}.");
        }

        var samples = Decode(dataBytes, audioFormat, bitsPerSample);
        return (samples, sampleRate, numChannels);
    }

    private const short WaveFormatPcm = 1;
    private const short WaveFormatIeeeFloat = 3;

    private static float[] Decode(byte[] data, short audioFormat, short bitsPerSample) => audioFormat switch
    {
        WaveFormatPcm => bitsPerSample switch
        {
            8 => DecodePcm8(data),
            16 => DecodePcm16(data),
            24 => DecodePcm24(data),
            32 => DecodePcm32(data),
            _ => throw new NotSupportedException($"Unsupported PCM bit depth: {bitsPerSample}."),
        },
        WaveFormatIeeeFloat => bitsPerSample switch
        {
            32 => DecodeFloat32(data),
            64 => DecodeFloat64(data),
            _ => throw new NotSupportedException($"Unsupported IEEE float bit depth: {bitsPerSample}."),
        },
        _ => throw new NotSupportedException(
            $"Unsupported WAV audio format code: {audioFormat}. Only PCM (1) and IEEE float (3) are supported."),
    };

    private static float[] DecodePcm8(byte[] data)
    {
        var samples = new float[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            // 8-bit PCM is the one unsigned case in the WAV spec; 128 is silence.
            samples[i] = (data[i] - 128) / 128f;
        }

        return samples;
    }

    private static float[] DecodePcm16(byte[] data)
    {
        var count = data.Length / 2;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        }

        return samples;
    }

    private static float[] DecodePcm24(byte[] data)
    {
        var count = data.Length / 3;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            var offset = i * 3;
            var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((value & 0x800000) != 0)
            {
                value = unchecked((int)(value | 0xFF000000));
            }

            samples[i] = value / 8388608f;
        }

        return samples;
    }

    private static float[] DecodePcm32(byte[] data)
    {
        var count = data.Length / 4;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BitConverter.ToInt32(data, i * 4) / 2147483648f;
        }

        return samples;
    }

    private static float[] DecodeFloat32(byte[] data)
    {
        var count = data.Length / 4;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BitConverter.ToSingle(data, i * 4);
        }

        return samples;
    }

    private static float[] DecodeFloat64(byte[] data)
    {
        var count = data.Length / 8;
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (float)BitConverter.ToDouble(data, i * 8);
        }

        return samples;
    }

    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        var frames = interleaved.Length / channels;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var baseIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += interleaved[baseIndex + channel];
            }

            mono[frame] = (float)(sum / channels);
        }

        return mono;
    }

    private static float[] Resample(float[] source, int sourceRate, int targetRate)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var ratio = (double)targetRate / sourceRate;
        var targetLength = (int)Math.Round(source.Length * ratio);
        var result = new float[targetLength];

        for (var i = 0; i < targetLength; i++)
        {
            var sourcePosition = i / ratio;
            var index0 = Math.Min((int)Math.Floor(sourcePosition), source.Length - 1);
            var index1 = Math.Min(index0 + 1, source.Length - 1);
            var fraction = sourcePosition - index0;

            result[i] = (float)(source[index0] + ((source[index1] - source[index0]) * fraction));
        }

        return result;
    }

    private static async Task ReadExactOrThrowAsync(
        Stream stream,
        byte[] buffer,
        string context,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                $"WAV file is truncated: expected {buffer.Length} bytes while reading {context}.", ex);
        }
    }

    private static async Task SkipAsync(Stream stream, int byteCount, CancellationToken cancellationToken)
    {
        if (byteCount <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(byteCount, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[Math.Min(byteCount, 81_920)];
        var remaining = byteCount;
        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, buffer.Length);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            remaining -= toRead;
        }
    }
}
