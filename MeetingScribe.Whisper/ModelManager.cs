using Whisper.net.Ggml;

namespace MeetingScribe.Whisper;

/// <summary>
/// Resolves ggml model files from a local cache directory, downloading them from
/// Hugging Face on demand when absent. Models are never bundled with the app.
/// </summary>
public static class ModelManager
{
    private const int CopyBufferBytes = 1 << 20; // 1 MiB

    /// <summary>
    /// Returns the full path to the requested model inside <paramref name="cacheDirectory"/>,
    /// downloading it first if it is not already present.
    /// </summary>
    /// <param name="size">Which model size to resolve.</param>
    /// <param name="cacheDirectory">Local directory used as the model cache. Created if missing.</param>
    /// <param name="downloadProgress">
    /// Optional progress reporter, 0.0-1.0. Only fires during an actual download; a cache hit
    /// reports nothing because there is nothing to wait for.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the download.</param>
    /// <returns>Absolute path to the ggml model file on disk.</returns>
    public static async Task<string> EnsureModelAsync(
        WhisperModelSize size,
        string cacheDirectory,
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        Directory.CreateDirectory(cacheDirectory);

        var fileName = ModelCatalog.ToFileName(size);
        var fullPath = Path.Combine(cacheDirectory, fileName);

        if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
        {
            return fullPath;
        }

        var ggmlType = ModelCatalog.ToGgmlType(size);

        // Download to a temp file first so a cancelled/failed download never leaves a
        // corrupt file behind under the real model name (which would look like a cache hit
        // on the next run).
        var tempPath = fullPath + ".download";

        try
        {
            using (var sourceStream = await WhisperGgmlDownloader.Default
                       .GetGgmlModelAsync(ggmlType, QuantizationType.NoQuantization, cancellationToken)
                       .ConfigureAwait(false))
            await using (var destinationStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferBytes,
                             useAsync: true))
            {
                await CopyWithProgressAsync(sourceStream, destinationStream, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        downloadProgress?.Report(1.0);
        return fullPath;
    }

    private static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        long? totalBytes = null;
        try
        {
            if (source.CanSeek)
            {
                totalBytes = source.Length;
            }
        }
        catch (NotSupportedException)
        {
            // Some HTTP-backed streams throw on Length even when CanSeek reports true.
            totalBytes = null;
        }

        var buffer = new byte[CopyBufferBytes];
        long totalRead = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;

            if (progress is not null && totalBytes is > 0)
            {
                progress.Report(Math.Min(1.0, (double)totalRead / totalBytes.Value));
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover .download file is harmless and will be
            // overwritten or ignored on the next attempt.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }
    }
}
