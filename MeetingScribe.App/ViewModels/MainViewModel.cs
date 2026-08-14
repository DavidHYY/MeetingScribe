using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MeetingScribe.App.Models;
using MeetingScribe.App.Services;
using MeetingScribe.Audio;
using MeetingScribe.Whisper;

// AudioPlatformProvider lives in the MeetingScribe.App root namespace (Bootstrap/).
using MeetingScribe.App;

namespace MeetingScribe.App.ViewModels;

/// <summary>
/// Single view model backing <c>MainWindow</c>. Owns the <see cref="MeetingSessionController"/>
/// and marshals every one of its events (which fire on background/capture threads) onto
/// the UI dispatcher before touching a bound property or collection.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TopLevel _topLevel;
    private readonly Dispatcher _dispatcher;
    private readonly MeetingSessionController _controller = new();
    private readonly DispatcherTimer _elapsedTimer;
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly RelayCommand _cancelCommand;

    private DateTime _recordingStartedLocal;
    private string? _currentMeetingFolder;
    private CancellationTokenSource? _stopCts;

    /// <summary>
    /// <paramref name="topLevel"/> is the owning window, used only for Avalonia's
    /// cross-platform <see cref="IStorageProvider"/> (folder/file pickers) - the direct
    /// replacement for WPF's <c>Microsoft.Win32.OpenFolderDialog</c>/<c>SaveFileDialog</c>,
    /// which have no Avalonia equivalent.
    /// </summary>
    public MainViewModel(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        _topLevel = topLevel;
        _dispatcher = Dispatcher.UIThread;

        var settings = SettingsStore.Load();
        _outputRoot = settings.OutputRoot;
        _languageOverrideText = settings.LanguageOverride ?? string.Empty;
        _allowedLanguagesText = settings.AllowedLanguages ?? string.Empty;
        _minutesPromptTemplate = settings.MinutesPromptTemplate;
        _micEnabled = settings.MicrophoneEnabled;
        _systemEnabled = settings.SystemAudioEnabled;
        _liveTranslationEnabled = settings.LiveTranslationEnabled;
        _minutesTimeoutSecondsText = settings.MinutesTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _modelCacheDirectory = settings.ModelCacheDirectory;
        _selectedLiveModel = ModelOption.All.First(m => m.Size == settings.LiveModelSize);
        _selectedFinalModel = ModelOption.All.First(m => m.Size == settings.FinalModelSize);
        _meetingTitle = string.IsNullOrWhiteSpace(settings.LastMeetingTitle)
            ? DefaultTitle()
            : settings.LastMeetingTitle;
        _lastMicDeviceId = settings.MicrophoneDeviceId;
        _lastSystemDeviceId = settings.SystemAudioDeviceId;

        // Install commands mirror installer\backends.iss exactly - same three commands offered by
        // the installer's optional "Minutes backends" page, so a user who skipped that step (or
        // installed MeetingScribe some other way) sees the identical, literal command here.
        MinutesProviderOptions.Add(new MinutesProviderOption(
            MinutesProviderKind.Claude, "Claude (claude -p)", "irm https://claude.ai/install.ps1 | iex"));
        MinutesProviderOptions.Add(new MinutesProviderOption(
            MinutesProviderKind.Codex, "Codex (codex exec)", "npm install -g @openai/codex"));
        MinutesProviderOptions.Add(new MinutesProviderOption(
            MinutesProviderKind.Ollama, "Ollama (local, no subscription)",
            "winget install --id Ollama.Ollama --scope user --accept-source-agreements --accept-package-agreements"));
        _selectedMinutesProviderOption = MinutesProviderOptions.FirstOrDefault(o => o.Kind == settings.MinutesProvider)
                                          ?? MinutesProviderOptions[0];

        _ollamaBaseUrl = settings.OllamaBaseUrl;
        _ollamaNumCtxText = settings.OllamaNumCtx.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(settings.OllamaModel))
        {
            OllamaModels.Add(settings.OllamaModel);
        }

        _selectedOllamaModel = OllamaModels.FirstOrDefault();

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _elapsedTimer.Tick += (_, _) => ElapsedDisplay = FormatElapsed(DateTime.Now - _recordingStartedLocal);

        _controller.LevelChanged += OnLevelChanged;
        _controller.RecorderErrorOccurred += OnRecorderErrorOccurred;
        _controller.LiveSegmentReady += OnLiveSegmentReady;
        _controller.LiveTranslationReady += OnLiveTranslationReady;
        _controller.StatusChanged += OnStatusChanged;
        _controller.FinalProgressChanged += OnFinalProgressChanged;
        _controller.MinutesProgressChanged += OnMinutesProgressChanged;
        _controller.Completed += OnCompleted;
        _controller.Failed += OnFailed;

        _startCommand = new AsyncRelayCommand(StartAsync, CanStart);
        _stopCommand = new AsyncRelayCommand(StopAsync, () => IsRecording && !IsBusy);
        _cancelCommand = new RelayCommand(() => _stopCts?.Cancel(), () => IsBusy && _stopCts is not null);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        BrowseOutputFolderCommand = new AsyncRelayCommand(BrowseOutputFolderAsync);
        ExportMinutesCommand = new AsyncRelayCommand(ExportMinutesAsync, () => !string.IsNullOrWhiteSpace(MinutesMarkdown));
        RecheckAvailabilityCommand = new AsyncRelayCommand(RefreshProviderAvailabilityAsync);
        RefreshOllamaModelsCommand = new AsyncRelayCommand(RefreshOllamaModelsAsync);

        RefreshDevices();
        _ = RefreshProviderAvailabilityAsync();
    }

    // ----- Collections -----------------------------------------------------------

    public ObservableCollection<DeviceItem> MicrophoneDevices { get; } = [];
    public ObservableCollection<DeviceItem> SystemAudioDevices { get; } = [];
    public ObservableCollection<TranscriptLineViewModel> LiveTranscriptLines { get; } = [];
    public IReadOnlyList<ModelOption> ModelOptions => ModelOption.All;
    public ObservableCollection<MinutesProviderOption> MinutesProviderOptions { get; } = [];
    public ObservableCollection<string> OllamaModels { get; } = [];

    // ----- Commands ----------------------------------------------------------------

    public ICommand StartCommand => _startCommand;
    public ICommand StopCommand => _stopCommand;
    public ICommand CancelCommand => _cancelCommand;
    public ICommand RefreshDevicesCommand { get; }
    public ICommand BrowseOutputFolderCommand { get; }
    public ICommand ExportMinutesCommand { get; }
    public ICommand RecheckAvailabilityCommand { get; }
    public ICommand RefreshOllamaModelsCommand { get; }

    // ----- Meeting setup -------------------------------------------------------------

    private string _meetingTitle;
    public string MeetingTitle
    {
        get => _meetingTitle;
        set { if (SetProperty(ref _meetingTitle, value)) RequeryCommands(); }
    }

    private DeviceItem? _selectedMicDevice;
    public DeviceItem? SelectedMicDevice
    {
        get => _selectedMicDevice;
        set => SetProperty(ref _selectedMicDevice, value);
    }

    private DeviceItem? _selectedSystemDevice;
    public DeviceItem? SelectedSystemDevice
    {
        get => _selectedSystemDevice;
        set => SetProperty(ref _selectedSystemDevice, value);
    }

    private bool _micEnabled;
    public bool MicrophoneEnabled
    {
        get => _micEnabled;
        set { if (SetProperty(ref _micEnabled, value)) RequeryCommands(); }
    }

    private bool _systemEnabled;
    public bool SystemAudioEnabled
    {
        get => _systemEnabled;
        set { if (SetProperty(ref _systemEnabled, value)) RequeryCommands(); }
    }

    private readonly string? _lastMicDeviceId;
    private readonly string? _lastSystemDeviceId;

    private bool _liveTranslationEnabled;

    /// <summary>
    /// Off by default. When on, the live (Stage 1) transcript also gets a live English
    /// translation per line - see <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>.
    /// English-only by design: whisper.cpp's <c>translate</c> task cannot target any other
    /// language, so this is a single on/off switch, not a language picker.
    /// </summary>
    public bool LiveTranslationEnabled
    {
        get => _liveTranslationEnabled;
        set => SetProperty(ref _liveTranslationEnabled, value);
    }

    // ----- Live meters / status --------------------------------------------------

    private double _micPeak;
    public double MicPeak
    {
        get => _micPeak;
        set => SetProperty(ref _micPeak, value);
    }

    private double _systemPeak;
    public double SystemPeak
    {
        get => _systemPeak;
        set => SetProperty(ref _systemPeak, value);
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set { if (SetProperty(ref _isRecording, value)) RequeryCommands(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RequeryCommands(); }
    }

    private string _elapsedDisplay = "00:00:00";
    public string ElapsedDisplay
    {
        get => _elapsedDisplay;
        private set => SetProperty(ref _elapsedDisplay, value);
    }

    private string _statusText = "Ready.";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    // ----- Minutes -----------------------------------------------------------------

    private string _minutesMarkdown = string.Empty;
    public string MinutesMarkdown
    {
        get => _minutesMarkdown;
        private set { if (SetProperty(ref _minutesMarkdown, value)) RequeryCommands(); }
    }

    // ----- Settings tab --------------------------------------------------------------

    private ModelOption _selectedLiveModel;
    public ModelOption SelectedLiveModel
    {
        get => _selectedLiveModel;
        set => SetProperty(ref _selectedLiveModel, value);
    }

    private ModelOption _selectedFinalModel;
    public ModelOption SelectedFinalModel
    {
        get => _selectedFinalModel;
        set => SetProperty(ref _selectedFinalModel, value);
    }

    private string _languageOverrideText;
    public string LanguageOverrideText
    {
        get => _languageOverrideText;
        set => SetProperty(ref _languageOverrideText, value);
    }

    private string _allowedLanguagesText;

    /// <summary>
    /// Comma-separated allowed-language codes for auto-detect (e.g. "ja,en,zh,pl,fr"). Blank =
    /// no restriction. Only meaningful when <see cref="LanguageOverrideText"/> is blank (auto-detect).
    /// </summary>
    public string AllowedLanguagesText
    {
        get => _allowedLanguagesText;
        set => SetProperty(ref _allowedLanguagesText, value);
    }

    private string _outputRoot;
    public string OutputRoot
    {
        get => _outputRoot;
        set { if (SetProperty(ref _outputRoot, value)) RequeryCommands(); }
    }

    private string _minutesPromptTemplate;
    public string MinutesPromptTemplate
    {
        get => _minutesPromptTemplate;
        set => SetProperty(ref _minutesPromptTemplate, value);
    }

    private string _minutesTimeoutSecondsText;

    /// <summary>
    /// How long a minutes generation call is allowed to run before it is treated as failed.
    /// Editable (not fixed) because a local CPU model (Ollama) can legitimately need much
    /// longer than the CLI backends' typical under-a-minute run. Free text, parsed defensively -
    /// see <see cref="ParseMinutesTimeoutSeconds"/>.
    /// </summary>
    public string MinutesTimeoutSecondsText
    {
        get => _minutesTimeoutSecondsText;
        set => SetProperty(ref _minutesTimeoutSecondsText, value);
    }

    private MinutesProviderOption? _selectedMinutesProviderOption;
    public MinutesProviderOption? SelectedMinutesProviderOption
    {
        get => _selectedMinutesProviderOption;
        set
        {
            if (SetProperty(ref _selectedMinutesProviderOption, value))
            {
                OnPropertyChanged(nameof(IsOllamaProviderSelected));
            }
        }
    }

    /// <summary>Drives the Ollama-only sub-panel's visibility in the Settings tab.</summary>
    public bool IsOllamaProviderSelected => SelectedMinutesProviderOption?.Kind == MinutesProviderKind.Ollama;

    private string _ollamaBaseUrl;

    /// <summary>Configurable (not hardcoded localhost) because Ollama can run on another host on the LAN.</summary>
    public string OllamaBaseUrl
    {
        get => _ollamaBaseUrl;
        set => SetProperty(ref _ollamaBaseUrl, value);
    }

    private string _ollamaNumCtxText;

    /// <summary>
    /// Ollama's context window (<c>num_ctx</c>, in tokens) - free text on the Settings tab, same
    /// pattern as <see cref="MinutesTimeoutSecondsText"/>, parsed defensively by
    /// <see cref="ParseOllamaNumCtx"/>. A transcript estimated to exceed this (after it is
    /// clamped to the model's own reported maximum) triggers a visible warning instead of being
    /// silently truncated - see <see cref="OllamaMinutesProvider"/>.
    /// </summary>
    public string OllamaNumCtxText
    {
        get => _ollamaNumCtxText;
        set => SetProperty(ref _ollamaNumCtxText, value);
    }

    private string? _selectedOllamaModel;

    /// <summary>Picked from <see cref="OllamaModels"/> (populated by <see cref="RefreshOllamaModelsCommand"/>) - never typed blind.</summary>
    public string? SelectedOllamaModel
    {
        get => _selectedOllamaModel;
        set
        {
            if (SetProperty(ref _selectedOllamaModel, value))
            {
                _ = RefreshOllamaAvailabilityAsync();
            }
        }
    }

    private readonly string _modelCacheDirectory;

    // ----- Device enumeration --------------------------------------------------------

    private void RefreshDevices()
    {
        try
        {
            var platform = AudioPlatformProvider.Current;

            MicrophoneDevices.Clear();
            MicrophoneDevices.Add(DeviceItem.SystemDefault);
            foreach (var device in platform.ListCaptureDevices())
            {
                MicrophoneDevices.Add(DeviceItem.FromInfo(device));
            }

            SystemAudioDevices.Clear();
            SystemAudioDevices.Add(DeviceItem.SystemDefault);
            foreach (var device in platform.ListRenderDevices())
            {
                SystemAudioDevices.Add(DeviceItem.FromInfo(device));
            }

            SelectedMicDevice = MicrophoneDevices.FirstOrDefault(d => d.Id == _lastMicDeviceId)
                                 ?? MicrophoneDevices.FirstOrDefault();
            SelectedSystemDevice = SystemAudioDevices.FirstOrDefault(d => d.Id == _lastSystemDeviceId)
                                    ?? SystemAudioDevices.FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            ErrorMessage = $"Failed to enumerate audio devices: {ex.Message}";
        }
    }

    // ----- Start / Stop --------------------------------------------------------------

    private bool CanStart() =>
        !IsRecording && !IsBusy && (MicrophoneEnabled || SystemAudioEnabled) &&
        !string.IsNullOrWhiteSpace(OutputRoot);

    private async Task StartAsync()
    {
        ErrorMessage = null;

        var title = string.IsNullOrWhiteSpace(MeetingTitle) ? DefaultTitle() : MeetingTitle.Trim();
        SaveSettings(title);

        var request = new StartRequest(
            Title: title,
            OutputRoot: OutputRoot,
            MicrophoneEnabled: MicrophoneEnabled,
            MicrophoneDeviceId: SelectedMicDevice?.Id,
            MicrophoneDeviceName: SelectedMicDevice?.DisplayName,
            SystemAudioEnabled: SystemAudioEnabled,
            SystemAudioDeviceId: SelectedSystemDevice?.Id,
            SystemAudioDeviceName: SelectedSystemDevice?.DisplayName,
            LiveModelSize: SelectedLiveModel.Size,
            FinalModelSize: SelectedFinalModel.Size,
            ModelCacheDirectory: _modelCacheDirectory,
            LanguageOverride: string.IsNullOrWhiteSpace(LanguageOverrideText) ? null : LanguageOverrideText.Trim(),
            AllowedLanguages: string.IsNullOrWhiteSpace(AllowedLanguagesText) ? null : AllowedLanguagesText.Trim(),
            LiveTranslationEnabled: LiveTranslationEnabled,
            MinutesPromptTemplate: MinutesPromptTemplate,
            MinutesTimeoutSeconds: ParseMinutesTimeoutSeconds(),
            MinutesProvider: SelectedMinutesProviderOption?.Kind ?? MinutesProviderKind.Claude,
            OllamaBaseUrl: OllamaBaseUrl,
            OllamaModel: SelectedOllamaModel ?? string.Empty,
            OllamaNumCtx: ParseOllamaNumCtx());

        try
        {
            LiveTranscriptLines.Clear();
            MinutesMarkdown = string.Empty;
            MicPeak = 0;
            SystemPeak = 0;

            await _controller.StartAsync(request).ConfigureAwait(true);

            _recordingStartedLocal = DateTime.Now;
            IsRecording = true;
            ElapsedDisplay = "00:00:00";
            _elapsedTimer.Start();
        }
        catch (Exception ex) when (ex is AudioDeviceNotFoundException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            ErrorMessage = ex.Message;
            StatusText = $"Failed to start: {ex.Message}";
        }
    }

    private async Task StopAsync()
    {
        _elapsedTimer.Stop();
        IsRecording = false;

        // Owns the cancellation for the whole Stop pipeline (final transcription pass, then
        // minutes generation) - both stages already thread this token through
        // MeetingSessionController.StopAsync; what was missing was a caller that actually
        // created one and offered a way to trigger it, so a slow stage (a CPU-bound local model
        // in particular) had no way to be interrupted short of killing the app. This is created
        // before IsBusy flips true so the RequeryCommands() the IsBusy setter triggers already
        // sees a non-null _stopCts and enables CancelCommand.
        _stopCts?.Dispose();
        _stopCts = new CancellationTokenSource();

        IsBusy = true;
        IsProgressIndeterminate = true;
        StatusText = "Stopping recording...";

        try
        {
            await _controller.StopAsync(_stopCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled by user.";
            StatusText = "Cancelled - the raw recording is intact; transcript/minutes for this run were not produced.";
        }
        catch (Exception ex)
        {
            // MeetingSessionController.StopAsync() already catches its own internal
            // failures and raises Failed/Completed instead of throwing; this is a last
            // resort for anything unexpected slipping past that.
            ErrorMessage = ex.Message;
            StatusText = $"Failed while stopping: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _stopCts?.Dispose();
            _stopCts = null;
        }
    }

    /// <summary>
    /// Called from <c>MainWindow</c>'s closing handler before it stops the recorder, to
    /// ask "stop and discard the in-progress recording?" the same way the WPF build's
    /// <c>MessageBox.Show</c> did. Returns true if the user confirmed (or nothing was
    /// recording, so there was nothing to confirm).
    /// </summary>
    public async Task<bool> ConfirmDiscardIfRecordingAsync(Window owner)
    {
        if (!IsRecording)
        {
            return true;
        }

        return await ErrorDialog.ShowConfirmAsync(
            owner,
            "MeetingScribe",
            "A meeting is still recording. Stop and discard the in-progress recording before closing?")
            .ConfigureAwait(true);
    }

    // ----- Controller event handlers (background threads -> dispatcher) --------------

    private void OnLevelChanged(object? sender, (AudioSourceKind Source, AudioLevel Level) e)
    {
        _dispatcher.Post(() =>
        {
            if (e.Source == AudioSourceKind.Microphone)
            {
                MicPeak = e.Level.Peak;
            }
            else
            {
                SystemPeak = e.Level.Peak;
            }
        });
    }

    private void OnRecorderErrorOccurred(object? sender, string message)
    {
        _dispatcher.Post(() => ErrorMessage = message);
    }

    private void OnLiveSegmentReady(object? sender, LiveSegmentEventArgs e)
    {
        _dispatcher.Post(() => LiveTranscriptLines.Add(new TranscriptLineViewModel(e.Line)));
    }

    /// <summary>
    /// Routes an asynchronously-arriving translation back to the line it belongs to. Always fires
    /// strictly after that line's own <see cref="OnLiveSegmentReady"/> (see
    /// <see cref="LiveTranslationEventArgs"/>'s remarks), but the corresponding view model is
    /// looked up defensively (not assumed present) since it could in principle already have
    /// scrolled out of a bounded collection in a future change - today <see cref="LiveTranscriptLines"/>
    /// is unbounded so this always finds it, but a missing line is just silently ignored rather
    /// than throwing.
    /// </summary>
    private void OnLiveTranslationReady(object? sender, LiveTranslationEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            var line = LiveTranscriptLines.FirstOrDefault(l => l.Id == e.LineId);
            line?.ApplyTranslation(e.Translation, e.Status, e.Error);
        });
    }

    private void OnStatusChanged(object? sender, string status)
    {
        _dispatcher.Post(() => StatusText = status);
    }

    private void OnFinalProgressChanged(object? sender, FinalProgress progress)
    {
        _dispatcher.Post(() =>
        {
            IsProgressIndeterminate = false;
            ProgressPercent = progress.PercentComplete;
        });
    }

    /// <summary>
    /// None of the three minutes backends report fractional progress, so this switches the
    /// status bar back to indeterminate (it would otherwise sit static at ~100% from the final
    /// transcription pass, which looks finished/stuck rather than "still generating") and shows
    /// elapsed time - this is the "not cancellable, not shown as running" gap called out for a
    /// slow local model.
    /// </summary>
    private void OnMinutesProgressChanged(object? sender, MinutesProgress progress)
    {
        _dispatcher.Post(() =>
        {
            IsProgressIndeterminate = true;
            var slowHint = progress.Elapsed.TotalMinutes >= 2 ? " (a local CPU model can take several minutes)" : string.Empty;
            StatusText = $"Generating minutes via {progress.ProviderDisplayName}... {FormatElapsed(progress.Elapsed)} elapsed{slowHint}";
        });
    }

    private void OnCompleted(object? sender, MeetingCompletedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            _currentMeetingFolder = e.MeetingFolder;
            if (e.MinutesSucceeded)
            {
                MinutesMarkdown = e.MinutesMarkdown!;
                StatusText = $"Meeting saved to {e.MeetingFolder}";
            }
            else
            {
                ErrorMessage = e.MinutesError;
                StatusText = $"Meeting saved to {e.MeetingFolder} (minutes generation failed - transcript is intact).";
            }

            ProgressPercent = 100;
        });
    }

    private void OnFailed(object? sender, string message)
    {
        _dispatcher.Post(() =>
        {
            ErrorMessage = message;
            StatusText = $"Failed: {message}";
            IsBusy = false;
        });
    }

    // ----- Settings persistence --------------------------------------------------------

    private void SaveSettings(string? lastTitle = null)
    {
        try
        {
            var settings = new AppSettings
            {
                OutputRoot = OutputRoot,
                ModelCacheDirectory = _modelCacheDirectory,
                LiveModelSize = SelectedLiveModel.Size,
                FinalModelSize = SelectedFinalModel.Size,
                LanguageOverride = string.IsNullOrWhiteSpace(LanguageOverrideText) ? null : LanguageOverrideText.Trim(),
                AllowedLanguages = AllowedLanguagesText.Trim(),
                LiveTranslationEnabled = LiveTranslationEnabled,
                MinutesPromptTemplate = MinutesPromptTemplate,
                MinutesTimeoutSeconds = ParseMinutesTimeoutSeconds(),
                MinutesProvider = SelectedMinutesProviderOption?.Kind ?? MinutesProviderKind.Claude,
                OllamaBaseUrl = string.IsNullOrWhiteSpace(OllamaBaseUrl) ? AppSettings.DefaultOllamaBaseUrl : OllamaBaseUrl.Trim(),
                OllamaModel = SelectedOllamaModel ?? string.Empty,
                OllamaNumCtx = ParseOllamaNumCtx(),
                MicrophoneEnabled = MicrophoneEnabled,
                MicrophoneDeviceId = SelectedMicDevice?.Id,
                SystemAudioEnabled = SystemAudioEnabled,
                SystemAudioDeviceId = SelectedSystemDevice?.Id,
                LastMeetingTitle = lastTitle ?? MeetingTitle,
            };

            SettingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    private async Task BrowseOutputFolderAsync()
    {
        var startLocation = Directory.Exists(OutputRoot)
            ? await _topLevel.StorageProvider.TryGetFolderFromPathAsync(OutputRoot).ConfigureAwait(true)
            : null;

        var results = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the meeting output folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        }).ConfigureAwait(true);

        var folder = results.Count > 0 ? results[0] : null;
        var path = folder?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            OutputRoot = path;
            SaveSettings();
        }
    }

    private async Task ExportMinutesAsync()
    {
        var defaultName = SanitizeFileName(MeetingTitle) + "_minutes.md";
        var startDirectory = _currentMeetingFolder is not null && Directory.Exists(_currentMeetingFolder)
            ? _currentMeetingFolder
            : OutputRoot;
        var startLocation = Directory.Exists(startDirectory)
            ? await _topLevel.StorageProvider.TryGetFolderFromPathAsync(startDirectory).ConfigureAwait(true)
            : null;

        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export minutes",
            SuggestedFileName = defaultName,
            DefaultExtension = "md",
            SuggestedStartLocation = startLocation,
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] },
            ],
        }).ConfigureAwait(true);

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, MinutesMarkdown, new System.Text.UTF8Encoding(false)).ConfigureAwait(true);
            StatusText = $"Minutes exported to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Failed to export minutes: {ex.Message}";
        }
    }

    // ----- Minutes provider availability / model discovery --------------------------

    /// <summary>
    /// Probes all three backends in parallel and updates each <see cref="MinutesProviderOption"/>
    /// in place (its bindings pick up the change live). Called once from the constructor and
    /// again from <see cref="RecheckAvailabilityCommand"/> - never throws itself, since every
    /// individual check is already exception-guarded.
    /// </summary>
    private async Task RefreshProviderAvailabilityAsync()
    {
        var claudeCheck = SetOptionAvailabilityAsync(FindOption(MinutesProviderKind.Claude), new ClaudeMinutesProvider());
        var codexCheck = SetOptionAvailabilityAsync(FindOption(MinutesProviderKind.Codex), new CodexMinutesProvider());
        var ollamaCheck = RefreshOllamaAvailabilityAsync();

        await Task.WhenAll(claudeCheck, codexCheck, ollamaCheck).ConfigureAwait(true);
    }

    private static async Task SetOptionAvailabilityAsync(MinutesProviderOption option, IMinutesProvider provider)
    {
        try
        {
            var availability = await provider.CheckAvailabilityAsync().ConfigureAwait(true);
            option.IsAvailable = availability.IsAvailable;
            option.Reason = availability.Reason;
        }
        catch (Exception ex) when (ex is MinutesGenerationException or InvalidOperationException)
        {
            option.IsAvailable = false;
            option.Reason = $"Availability check failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Ollama's availability additionally requires a model to be selected (the other two
    /// backends have no equivalent - claude/codex use whatever model their own CLI config
    /// resolves to), so this does not go through the generic <see cref="SetOptionAvailabilityAsync"/>.
    /// </summary>
    private async Task RefreshOllamaAvailabilityAsync()
    {
        var option = FindOption(MinutesProviderKind.Ollama);

        if (string.IsNullOrWhiteSpace(SelectedOllamaModel))
        {
            var reachability = await OllamaMinutesProvider.CheckServerAvailabilityAsync(OllamaBaseUrl).ConfigureAwait(true);
            option.IsAvailable = false;
            option.Reason = reachability.IsAvailable
                ? $"{reachability.Reason} No model selected - click Refresh Models."
                : reachability.Reason;
            return;
        }

        await SetOptionAvailabilityAsync(option, new OllamaMinutesProvider(OllamaBaseUrl, SelectedOllamaModel)).ConfigureAwait(true);
    }

    private async Task RefreshOllamaModelsAsync()
    {
        try
        {
            var models = await OllamaMinutesProvider.ListModelsAsync(OllamaBaseUrl).ConfigureAwait(true);
            var previouslySelected = SelectedOllamaModel;

            OllamaModels.Clear();
            foreach (var model in models)
            {
                OllamaModels.Add(model);
            }

            SelectedOllamaModel = OllamaModels.FirstOrDefault(m => m == previouslySelected) ?? OllamaModels.FirstOrDefault();

            StatusText = models.Count == 0
                ? $"Ollama at {OllamaBaseUrl} is reachable but has no models pulled yet."
                : $"Found {models.Count} Ollama model(s).";
        }
        catch (MinutesGenerationException ex)
        {
            ErrorMessage = $"Failed to list Ollama models: {ex.Message}";
        }

        // SelectedOllamaModel's setter already triggers a re-check when it changes, but if the
        // list came back empty (or unchanged), nothing has necessarily changed - always refresh
        // explicitly so "no models pulled" / connection failures show up even then.
        await RefreshOllamaAvailabilityAsync().ConfigureAwait(true);
    }

    private MinutesProviderOption FindOption(MinutesProviderKind kind) =>
        MinutesProviderOptions.First(o => o.Kind == kind);

    /// <summary>
    /// Parses <see cref="MinutesTimeoutSecondsText"/> defensively - it is free text on the
    /// Settings tab (not a numeric control), so a blank/non-numeric/non-positive value falls
    /// back to <see cref="AppSettings.MinutesTimeoutSeconds"/>'s own default rather than
    /// producing a zero or negative timeout.
    /// </summary>
    private int ParseMinutesTimeoutSeconds() =>
        int.TryParse(MinutesTimeoutSecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : DefaultMinutesTimeoutSeconds;

    private const int DefaultMinutesTimeoutSeconds = 600;

    /// <summary>
    /// Parses <see cref="OllamaNumCtxText"/> defensively, same rationale as
    /// <see cref="ParseMinutesTimeoutSeconds"/>: free text on the Settings tab, not a numeric
    /// control, so it can be blank/non-numeric/non-positive.
    /// </summary>
    private int ParseOllamaNumCtx() =>
        int.TryParse(OllamaNumCtxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numCtx) && numCtx > 0
            ? numCtx
            : AppSettings.DefaultOllamaNumCtx;

    private void RequeryCommands()
    {
        _startCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
        if (ExportMinutesCommand is AsyncRelayCommand export)
        {
            export.RaiseCanExecuteChanged();
        }
    }

    private static string DefaultTitle() => DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "meeting" : result;
    }

    public async ValueTask DisposeAsync()
    {
        _elapsedTimer.Stop();
        _stopCts?.Dispose();
        await _controller.DisposeAsync().ConfigureAwait(false);
    }
}
