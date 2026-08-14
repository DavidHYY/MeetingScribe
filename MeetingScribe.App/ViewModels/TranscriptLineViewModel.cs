using System.Globalization;
using Avalonia.Media;
using MeetingScribe.App.Models;
using MeetingScribe.Audio;

namespace MeetingScribe.App.ViewModels;

/// <summary>
/// UI-bindable projection of a <see cref="TranscriptLine"/> for the live transcript pane.
/// Original-text fields are set once at construction and never change. The translation fields
/// are the exception: translation lands later than the original (see
/// <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>), so <see cref="ApplyTranslation"/>
/// updates them in place - via <see cref="ObservableObject"/> - once a result arrives, rather than
/// this view model being replaced wholesale in the bound collection.
/// </summary>
public sealed class TranscriptLineViewModel : ObservableObject
{
    /// <summary>Matches the originating <see cref="TranscriptLine.Id"/> - used to route a later <see cref="ApplyTranslation"/> call to the right instance.</summary>
    public long Id { get; }

    public string Timestamp { get; }
    public string SourceLabel { get; }
    public string Text { get; }
    public IBrush SourceBrush { get; }

    public TranscriptLineViewModel(TranscriptLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Id = line.Id;
        Timestamp = line.Start.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        SourceLabel = line.SourceLabel;
        Text = line.Text;
        SourceBrush = line.Source == AudioSourceKind.Microphone
            ? Brushes.SteelBlue
            : Brushes.DarkOrange;

        _translationStatus = line.TranslationStatus;
        _translationDisplay = BuildTranslationDisplay(line.TranslationStatus, line.Translation, line.TranslationError);
    }

    private TranslationStatus _translationStatus;
    public TranslationStatus TranslationStatus
    {
        get => _translationStatus;
        private set => SetProperty(ref _translationStatus, value);
    }

    private string? _translationDisplay;

    /// <summary>
    /// What to show under the original text: the English translation once it lands, a short
    /// non-fatal marker while pending/skipped/failed (never a silent blank), or null (nothing
    /// rendered at all) when translation is off or this line's chunk was already English.
    /// </summary>
    public string? TranslationDisplay
    {
        get => _translationDisplay;
        private set
        {
            if (SetProperty(ref _translationDisplay, value))
            {
                OnPropertyChanged(nameof(HasTranslationDisplay));
            }
        }
    }

    /// <summary>Bound to the translation row's IsVisible so nothing renders when there is nothing to show.</summary>
    public bool HasTranslationDisplay => !string.IsNullOrEmpty(TranslationDisplay);

    /// <summary>Called from <c>MainViewModel</c> when a queued translation for this line settles.</summary>
    public void ApplyTranslation(string? translation, TranslationStatus status, string? error)
    {
        TranslationStatus = status;
        TranslationDisplay = BuildTranslationDisplay(status, translation, error);
    }

    private static string? BuildTranslationDisplay(TranslationStatus status, string? translation, string? error) => status switch
    {
        TranslationStatus.Completed when !string.IsNullOrEmpty(translation) => translation,
        TranslationStatus.Pending => "(translating…)",
        TranslationStatus.Skipped => "(translation skipped - falling behind)",
        TranslationStatus.Failed => string.IsNullOrEmpty(error) ? "(translation failed)" : $"(translation failed: {error})",
        _ => null, // NotApplicable, or Completed with nothing to show.
    };
}
