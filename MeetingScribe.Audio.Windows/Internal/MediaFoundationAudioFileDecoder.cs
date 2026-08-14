using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MeetingScribe.Audio.Internal;

/// <summary>
/// Decodes an arbitrary compressed audio file (MP3, M4A/AAC, MP4 audio, etc.) to a 16kHz mono
/// 16-bit PCM WAV, via Windows Media Foundation - the same OS component
/// <see cref="MediaFoundationBootstrap"/> already starts for live loopback/mic resampling
/// (<see cref="SourcePipeline"/>). Backs <see cref="WindowsAudioPlatform.DecodeAudioFileToWavAsync"/>.
/// </summary>
/// <remarks>
/// Deliberately writes to a temp file rather than an in-memory <see cref="MemoryStream"/>: a
/// multi-hour recording decoded to 16kHz mono PCM16 is still tens to low-hundreds of MB (a
/// 3-hour file is roughly 345 MB), and <see cref="MeetingScribe.Whisper.WavAudioLoader"/>
/// downstream already allocates its own full-file byte/float buffers when it reads whatever
/// stream this hands back - doubling that up in a second in-memory copy here has no benefit and
/// a real cost on a long recording. The returned <see cref="FileStream"/> uses
/// <see cref="FileOptions.DeleteOnClose"/> so the temp file cannot leak past the caller
/// disposing the stream, success or failure, without the caller needing to know a temp file was
/// ever involved.
/// </remarks>
internal static class MediaFoundationAudioFileDecoder
{
    /// <summary>
    /// Target format for the decoded WAV: matches <c>SourcePipeline.TargetFormat</c> (the format
    /// live capture already writes) and <c>MeetingScribe.Whisper.WavAudioLoader.TargetSampleRateHz</c>
    /// - decoding straight to this format means WavAudioLoader's own resample/downmix step is a
    /// no-op for imported files, exactly as it already is for live-recorded mic/system WAVs.
    /// </summary>
    private static readonly WaveFormat TargetFormat = new(16_000, 16, 1);

    public static Task<Stream> DecodeToWavAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

        // MediaFoundationReader/MediaFoundationResampler are blocking, COM-backed calls (the
        // same kind SourcePipeline already makes on its own dedicated pull thread) - never run
        // them inline on a caller's async-await continuation (that would be the UI thread here).
        return Task.Run(() => DecodeToWavCore(filePath, cancellationToken), cancellationToken);
    }

    private static Stream DecodeToWavCore(string filePath, CancellationToken cancellationToken)
    {
        MediaFoundationBootstrap.EnsureStarted();

        var fileName = Path.GetFileName(filePath);
        using var reader = OpenReader(filePath, fileName);

        if (reader.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"'{fileName}' decoded to zero audio duration - nothing to transcribe.");
        }

        using var resampler = new MediaFoundationResampler(reader, TargetFormat) { ResamplerQuality = 60 };

        var tempPath = Path.Combine(Path.GetTempPath(), $"meetingscribe_import_{Guid.NewGuid():N}.wav");
        try
        {
            using (var writer = new WaveFileWriter(tempPath, TargetFormat))
            {
                var buffer = new byte[TargetFormat.AverageBytesPerSecond]; // ~1s per read
                int bytesRead;
                while ((bytesRead = ReadChecked(resampler, buffer, fileName)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Write(buffer, 0, bytesRead);
                }
            }

            // FileOptions.DeleteOnClose: the temp file is removed the moment the caller disposes
            // this stream (normal completion, an exception during transcription, or cancellation
            // all go through Dispose eventually) - no separate cleanup path for the caller to
            // forget.
            return new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1 << 20,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch
        {
            // Decode failed before the DeleteOnClose stream could take ownership of the temp
            // file - clean it up here instead of leaking it.
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup only; the original decode failure is what the caller needs
                // to see, not a secondary failure to delete a partial temp file.
            }

            throw;
        }
    }

    /// <summary>
    /// Opens the source file via Media Foundation, translating the COM-level failure a corrupt
    /// file or an unsupported codec produces into an <see cref="InvalidDataException"/> that
    /// names the actual file, rather than a bare <see cref="COMException"/> with an HRESULT the
    /// UI has no way to explain to a user.
    /// </summary>
    private static MediaFoundationReader OpenReader(string filePath, string fileName)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Recording file not found: {filePath}", filePath);
        }

        MediaFoundationReader reader;
        try
        {
            reader = new MediaFoundationReader(filePath);
        }
        catch (COMException ex)
        {
            throw new InvalidDataException(
                $"'{fileName}' could not be decoded - the file may be corrupt or in a format " +
                $"Windows Media Foundation does not support. ({ex.Message})", ex);
        }

        // Belt-and-suspenders: some inputs (e.g. a video-only MP4, or a container MF opens but
        // finds no usable audio stream in) construct successfully but report a degenerate
        // format instead of throwing. Caught here, not left to surface as a confusing failure
        // deep inside WavAudioLoader/whisper.cpp later.
        if (reader.WaveFormat.Channels <= 0 || reader.WaveFormat.SampleRate <= 0)
        {
            reader.Dispose();
            throw new InvalidDataException($"'{fileName}' has no usable audio stream.");
        }

        return reader;
    }

    /// <summary>
    /// Wraps <see cref="MediaFoundationResampler.Read"/> so a mid-stream COM failure (a
    /// truncated/corrupt file that opened fine but breaks partway through) also becomes an
    /// <see cref="InvalidDataException"/> naming the file, instead of a bare <see cref="COMException"/>.
    /// </summary>
    private static int ReadChecked(MediaFoundationResampler resampler, byte[] buffer, string fileName)
    {
        try
        {
            return resampler.Read(buffer, 0, buffer.Length);
        }
        catch (COMException ex)
        {
            throw new InvalidDataException(
                $"'{fileName}' stopped decoding partway through - the file is likely truncated or corrupt. ({ex.Message})",
                ex);
        }
    }
}
