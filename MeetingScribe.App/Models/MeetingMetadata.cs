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

    /// <summary>True for a meeting produced by importing an existing recording (see
    /// <see cref="MeetingScribe.App.Services.MeetingSessionController.ImportAsync"/>) rather than
    /// live Start/Stop capture - no live (stage 1) pass ever ran for one of these.</summary>
    public bool Imported { get; init; }

    /// <summary>Original file path the user picked, only set when <see cref="Imported"/> is true.</summary>
    public string? ImportedSourceFile { get; init; }

    public required string LiveModel { get; init; }
    public required string FinalModel { get; init; }
    public string? LanguageOverride { get; init; }

    public string? FinalTranscriptionBackend { get; init; }
    public bool? FinalTranscriptionIsGpu { get; init; }

    public bool MinutesGenerated { get; init; }
    public string? MinutesError { get; init; }

    public IReadOnlyList<string> RecorderErrors { get; init; } = [];
}
