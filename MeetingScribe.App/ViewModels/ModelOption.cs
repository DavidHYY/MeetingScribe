using MeetingScribe.App.Models;
using MeetingScribe.Whisper;

namespace MeetingScribe.App.ViewModels;

/// <summary>One entry in a model-size ComboBox, labeled with the verified benchmark note when one exists.</summary>
public sealed class ModelOption
{
    public WhisperModelSize Size { get; }
    public string Display { get; }

    public ModelOption(WhisperModelSize size)
    {
        Size = size;
        var note = ModelBenchmarks.Describe(size);
        Display = note is null ? size.ToString() : $"{size} — {note}";
    }

    public override string ToString() => Display;

    public static IReadOnlyList<ModelOption> All { get; } =
    [
        new(WhisperModelSize.Tiny),
        new(WhisperModelSize.Base),
        new(WhisperModelSize.Small),
        new(WhisperModelSize.Medium),
        new(WhisperModelSize.LargeV3),
    ];
}
