using Whisper.net.Ggml;

namespace MeetingScribe.Whisper;

/// <summary>
/// Maps the public <see cref="WhisperModelSize"/> enum to the underlying Whisper.net
/// <see cref="GgmlType"/> and to the on-disk file name used for local caching.
/// File names follow the standard ggml naming convention (e.g. "ggml-medium.bin") so a
/// cache directory populated by other tools (whisper.cpp, other ggml downloaders) is
/// reused without re-downloading.
/// </summary>
public static class ModelCatalog
{
    private static readonly IReadOnlyDictionary<WhisperModelSize, (GgmlType GgmlType, string FileName)> Map =
        new Dictionary<WhisperModelSize, (GgmlType, string)>
        {
            [WhisperModelSize.Tiny] = (GgmlType.Tiny, "ggml-tiny.bin"),
            [WhisperModelSize.Base] = (GgmlType.Base, "ggml-base.bin"),
            [WhisperModelSize.Small] = (GgmlType.Small, "ggml-small.bin"),
            [WhisperModelSize.Medium] = (GgmlType.Medium, "ggml-medium.bin"),
            [WhisperModelSize.LargeV3] = (GgmlType.LargeV3, "ggml-large-v3.bin"),
        };

    public static GgmlType ToGgmlType(WhisperModelSize size) => Map[size].GgmlType;

    public static string ToFileName(WhisperModelSize size) => Map[size].FileName;
}
