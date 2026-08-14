using MeetingScribe.App.Services;

namespace MeetingScribe.App.ViewModels;

/// <summary>
/// One selectable entry in the Settings "Minutes Generation" provider ComboBox. Availability is
/// refreshed asynchronously (see <c>MainViewModel</c>'s constructor and
/// <c>RecheckAvailabilityCommand</c>) so a provider that is not actually usable (CLI missing,
/// Ollama unreachable/model not pulled) shows up greyed out with a reason, instead of being
/// offered and only failing when clicked.
/// </summary>
public sealed class MinutesProviderOption(MinutesProviderKind kind, string displayName, string installCommand) : ObservableObject
{
    public MinutesProviderKind Kind { get; } = kind;

    public string DisplayName { get; } = displayName;

    /// <summary>
    /// The literal, copy-pasteable command that installs this backend's CLI - shown next to the
    /// "not found" reason in Settings so a user who skipped the installer's optional backends
    /// step can still get here without hunting for a README. Kept in sync with the same commands
    /// the installer offers (installer\backends.iss); not re-derived at runtime because there is
    /// no reliable way to ask "what would install this" from inside the app itself.
    /// </summary>
    public string InstallCommand { get; } = installCommand;

    private bool _isAvailable;

    /// <summary>Bound to a ComboBoxItem style's IsEnabled in MainWindow.axaml - false greys the item out.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(ShowInstallCommand));
            }
        }
    }

    /// <summary>True once a real availability check has come back negative - not on the initial "Checking availability..." placeholder, so the install command doesn't flash on screen before the first check completes.</summary>
    public bool ShowInstallCommand => !IsAvailable && _reason != "Checking availability...";

    private string _reason = "Checking availability...";

    /// <summary>Human-readable why (found version / not on PATH / endpoint unreachable / model not pulled, etc.).</summary>
    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(ShowInstallCommand));
            }
        }
    }

    /// <summary>"DisplayName — Reason", shown in the always-visible availability list under the picker.</summary>
    public string Summary => $"{DisplayName} — {Reason}";

    public override string ToString() => DisplayName;
}
