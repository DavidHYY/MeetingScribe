using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Minimal, dependency-free canonical-PCM-WAV writer (no NAudio on macOS). Writes a
/// 44-byte RIFF/WAVE/fmt/data header up front with zero sizes, appends sample data,
/// and rewrites the RIFF and data chunk sizes in place on every <see cref="Flush"/> -
/// so a process crash mid-recording leaves a file with a header that undercounts the
/// last unflushed block at worst, never one that overcounts and points a reader past
/// the end of the file. Every write goes straight to the underlying <see cref="FileStream"/>
/// with no internal buffering beyond the OS's own, matching requirement #5 (continuous
/// flush, lose at most ~1s on crash).
/// </summary>
internal sealed class PcmWavWriter : IDisposable
{
    private const int HeaderSize = 44;

    private readonly FileStream _stream;
    private readonly object _lock = new();
    private long _dataBytesWritten;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    public PcmWavWriter(string path, int sampleRate, int channels, int bitsPerSample)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (bitsPerSample != 16)
        {
            // The only format this writer (and the native resampler feeding it) ever
            // produces; asserting this here catches a wiring mistake immediately
            // rather than writing a corrupt header silently.
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample, "Only 16-bit PCM is supported.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader(0);
        _stream.Flush(flushToDisk: true);
    }

    /// <summary>Appends interleaved PCM16 samples. Does not flush - call <see cref="Flush"/> after.</summary>
    public void WriteSamples(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var bytes = MemoryMarshal.AsBytes(samples);
            _stream.Write(bytes);
            _dataBytesWritten += bytes.Length;
        }
    }

    /// <summary>Patches the RIFF/data chunk sizes in place and flushes to disk.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            WriteHeader(_dataBytesWritten);
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Writes the 44-byte header for the given data length, then restores the stream position.</summary>
    private void WriteHeader(long dataBytes)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16); // PCM fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); // AudioFormat = PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)SampleRate);

        var byteRate = (uint)(SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], byteRate);

        var blockAlign = (ushort)(Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)BitsPerSample);

        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);

        var resumePosition = _stream.Position;
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Position = resumePosition == 0 ? HeaderSize : resumePosition;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WriteHeader(_dataBytesWritten);
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
        }
    }
}
