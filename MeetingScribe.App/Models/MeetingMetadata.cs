namespace MeetingScribe.App.Models;

/// <summary>Persisted as <c>meta.json</c> in every meeting output folder.</summary>
public sealed class MeetingMetadata
{
    public required string Title { get; init; }
    public required DateTime StartedUtc { get; init; }
    public DateTime? StoppedUtc { get; init; }
    public TimeSpan? Duration { get; init; }

    public required bool MicrophoneEnabled { get; init; }
    public string? MicrophoneDeviceName { get; init; }
    public required bool SystemAudioEnabled { get; init; }
    public string? SystemAudioDeviceName { get; init; }

    public required string LiveModel { get; init; }
    public required string FinalModel { get; init; }
    public string? LanguageOverride { get; init; }

    public string? FinalTranscriptionBackend { get; init; }
    public bool? FinalTranscriptionIsGpu { get; init; }

    public bool MinutesGenerated { get; init; }
    public string? MinutesError { get; init; }

    public IReadOnlyList<string> RecorderErrors { get; init; } = [];
}
