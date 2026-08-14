using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetingScribe.App.Models;
using MeetingScribe.Audio;
using MeetingScribe.Whisper;

// AudioPlatformProvider lives in the MeetingScribe.App root namespace (Bootstrap/); this
// is the seam that picks the Windows or Mac IAudioPlatform for the active TFM.
using MeetingScribe.App;

namespace MeetingScribe.App.Services;

/// <summary>Everything needed to start one meeting recording/transcription session.</summary>
public sealed record StartRequest(
    string Title,
    string OutputRoot,
    bool MicrophoneEnabled,
    string? MicrophoneDeviceId,
    string? MicrophoneDeviceName,
    bool SystemAudioEnabled,
    string? SystemAudioDeviceId,
    string? SystemAudioDeviceName,
    WhisperModelSize LiveModelSize,
    WhisperModelSize FinalModelSize,
    string ModelCacheDirectory,
    string? LanguageOverride,
    string? AllowedLanguages,
    bool LiveTranslationEnabled,
    string MinutesPromptTemplate,
    int MinutesTimeoutSeconds,
    MinutesProviderKind MinutesProvider,
    string OllamaBaseUrl,
    string OllamaModel,
    int OllamaNumCtx);

/// <summary>
/// Everything needed to transcribe a recording the user already has (a phone/voice-recorder/
/// meeting-tool export) instead of one captured live via <see cref="StartRequest"/>/
/// <see cref="MeetingSessionController.StopAsync"/>. Deliberately narrower than
/// <see cref="StartRequest"/>: there is no live (stage 1) pass for an imported file (nothing is
/// being recorded to run one against) and no Mic/System split (one file, one track), so this
/// carries only what <see cref="MeetingSessionController.ImportAsync"/> actually uses - the
/// accurate-pass model/language settings and the minutes settings.
/// </summary>
public sealed record ImportRequest(
    string Title,
    string OutputRoot,
    string SourceFilePath,
    WhisperModelSize FinalModelSize,
    string ModelCacheDirectory,
    string? LanguageOverride,
    string? AllowedLanguages,
    string MinutesPromptTemplate,
    int MinutesTimeoutSeconds,
    MinutesProviderKind MinutesProvider,
    string OllamaBaseUrl,
    string OllamaModel,
    int OllamaNumCtx);

/// <summary>Raised once the whole session (recording + stage 2 + stage 3) has finished, successfully or not.</summary>
public sealed class MeetingCompletedEventArgs(
    string meetingFolder,
    IReadOnlyList<TranscriptLine> finalTranscript,
    string? minutesMarkdown,
    string? minutesError) : EventArgs
{
    public string MeetingFolder { get; } = meetingFolder;
    public IReadOnlyList<TranscriptLine> FinalTranscript { get; } = finalTranscript;
    public string? MinutesMarkdown { get; } = minutesMarkdown;
    public string? MinutesError { get; } = minutesError;
    public bool MinutesSucceeded => MinutesError is null && MinutesMarkdown is not null;
}

/// <summary>
/// Elapsed-time heartbeat during minutes generation - not a percentage (unlike
/// <see cref="FinalProgress"/>), since none of the three backends report fractional progress.
/// Raised roughly every 2 seconds so a UI can show "still running" instead of a progress
/// indicator that looks stuck during a slow local model.
/// </summary>
public readonly record struct MinutesProgress(string ProviderDisplayName, TimeSpan Elapsed);

/// <summary>
/// Ties <see cref="IMeetingRecorder"/> and <see cref="WhisperTranscriber"/> together and
/// drives the three-stage pipeline end to end: live rough transcript while recording,
/// an accurate re-transcription per track after Stop, then minutes generated from that
/// accurate pass only. One controller instance is good for one meeting at a time - like
/// <see cref="IMeetingRecorder"/> itself, it is not safe for concurrent Start/Stop calls
/// and expects a single owner (the UI thread). Recorders are created through
/// <see cref="IAudioPlatform"/> - this class never references a concrete platform
/// implementation or NAudio directly.
/// </summary>
public sealed class MeetingSessionController : IAsyncDisposable
{
    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _stateLock = new();
    private readonly IAudioPlatform _audioPlatform;

    private IMeetingRecorder? _recorder;
    private WhisperTranscriber? _liveTranscriber;
    private volatile LiveTranscriptionEngine? _liveEngine;
    private Task? _liveInitTask;
    private StartRequest? _activeRequest;
    private DateTime _startedUtc;
    private bool _disposed;

    public bool IsBusy { get; private set; }
    public bool IsRecording { get; private set; }
    public string? CurrentMeetingFolder { get; private set; }

    public event EventHandler<(AudioSourceKind Source, AudioLevel Level)>? LevelChanged;
    public event EventHandler<string>? RecorderErrorOccurred;
    public event EventHandler<LiveSegmentEventArgs>? LiveSegmentReady;
    public event EventHandler<LiveTranslationEventArgs>? LiveTranslationReady;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<FinalProgress>? FinalProgressChanged;
    public event EventHandler<MinutesProgress>? MinutesProgressChanged;
    public event EventHandler<MeetingCompletedEventArgs>? Completed;
    public event EventHandler<string>? Failed;

    /// <summary>
    /// Creates a controller bound to the given audio platform. Defaults to
    /// <see cref="AudioPlatformProvider.Current"/> (the current build's Windows or Mac
    /// provider), overridable for tests.
    /// </summary>
    public MeetingSessionController(IAudioPlatform? audioPlatform = null)
    {
        _audioPlatform = audioPlatform ?? AudioPlatformProvider.Current;
    }

    /// <summary>
    /// Starts recording immediately and begins loading the live model in the background.
    /// Live transcription only starts flowing once that load finishes; any audio
    /// captured before then is still fully preserved in mic.wav/system.wav and will be
    /// covered by the stage-2 accurate pass regardless - only the rough live pane misses
    /// the first few seconds on a cold model cache.
    /// </summary>
    public async Task StartAsync(StartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_stateLock)
        {
            if (IsRecording || IsBusy)
            {
                throw new InvalidOperationException("A meeting session is already active.");
            }

            IsBusy = true;
        }

        try
        {
            if (!request.MicrophoneEnabled && !request.SystemAudioEnabled)
            {
                throw new ArgumentException("At least one of microphone or system audio must be enabled.");
            }

            var meetingFolder = MeetingPathPlanner.BuildMeetingFolder(request.OutputRoot, request.Title, DateTime.Now);
            Directory.CreateDirectory(meetingFolder);

            var recorder = _audioPlatform.CreateRecorder(new MeetingRecorderOptions
            {
                OutputDirectory = meetingFolder,
                MicrophoneEnabled = request.MicrophoneEnabled,
                MicrophoneDeviceId = request.MicrophoneDeviceId,
                SystemAudioEnabled = request.SystemAudioEnabled,
                SystemAudioDeviceId = request.SystemAudioDeviceId,
            });

            recorder.LevelUpdated += (_, e) => LevelChanged?.Invoke(this, (e.Source, e.Level));
            recorder.CaptureError += (_, e) =>
                RecorderErrorOccurred?.Invoke(this, $"{e.Source}: {e.Exception.Message}");
            recorder.SamplesAvailable += (_, e) =>
                _liveEngine?.Enqueue(e.Source, e.Samples as short[] ?? [.. e.Samples]);

            recorder.Start();

            _recorder = recorder;
            _activeRequest = request;
            _startedUtc = DateTime.UtcNow;
            CurrentMeetingFolder = meetingFolder;
            IsRecording = true;

            StatusChanged?.Invoke(this, "Recording. Loading live model...");

            // Intentionally not awaited here so Start() returns as soon as capture is
            // live - but the task IS retained (not fire-and-forget): StopAsync awaits it
            // before touching _liveEngine/_liveTranscriber, otherwise a Stop() that lands
            // while the model is still loading could race this initialization and leak
            // the live model (loaded after Stop already decided there was nothing to
            // dispose). Exceptions are caught inside and reported, never left unobserved.
            _liveInitTask = InitializeLiveEngineAsync(request, cancellationToken);
        }
        finally
        {
            lock (_stateLock)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Transcribes an existing recording exactly as a live meeting's Stop path does from the
    /// accurate pass onward: one <see cref="WhisperTranscriber.TranscribeFileAsync"/> call
    /// against the file's own audio (no live/stage-1 pass - there is nothing being recorded to
    /// run one against), then the same transcript.txt/vtt/json + minutes.md outputs in a freshly
    /// created meeting folder, so the Minutes tab and export work exactly as they do for a
    /// recorded meeting. Mutually exclusive with a live recording, same
    /// IsRecording/IsBusy guard as <see cref="StartAsync"/>.
    /// </summary>
    /// <remarks>
    /// WAV input goes straight to <see cref="WhisperTranscriber.TranscribeFileAsync"/> (it already
    /// reads WAV natively via <c>WavAudioLoader</c>). Anything else is decoded first via
    /// <see cref="_audioPlatform"/>'s <see cref="IAudioPlatform.DecodeAudioFileToWavAsync"/> -
    /// Windows Media Foundation on Windows, an honest <see cref="PlatformNotSupportedException"/>
    /// on macOS (see <c>MacAudioPlatform</c>).
    /// </remarks>
    public async Task ImportAsync(ImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_stateLock)
        {
            if (IsRecording || IsBusy)
            {
                throw new InvalidOperationException("A meeting session is already active.");
            }

            IsBusy = true;
        }

        var startedUtc = DateTime.UtcNow;

        try
        {
            if (!File.Exists(request.SourceFilePath))
            {
                throw new FileNotFoundException($"Recording file not found: {request.SourceFilePath}", request.SourceFilePath);
            }

            var sourceInfo = new FileInfo(request.SourceFilePath);
            if (sourceInfo.Length == 0)
            {
                throw new InvalidDataException($"'{sourceInfo.Name}' is empty (0 bytes) - nothing to transcribe.");
            }

            var meetingFolder = MeetingPathPlanner.BuildMeetingFolder(request.OutputRoot, request.Title, DateTime.Now);
            Directory.CreateDirectory(meetingFolder);
            CurrentMeetingFolder = meetingFolder;

            var isWav = string.Equals(Path.GetExtension(sourceInfo.Name), ".wav", StringComparison.OrdinalIgnoreCase);

            StatusChanged?.Invoke(this, isWav ? $"Opening {sourceInfo.Name}..." : $"Decoding {sourceInfo.Name}...");

            IReadOnlyList<TranscriptLine> finalTranscript = [];
            BackendInfo? finalBackend = null;

            var audioStream = isWav
                ? new FileStream(request.SourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true)
                : await _audioPlatform.DecodeAudioFileToWavAsync(request.SourceFilePath, cancellationToken).ConfigureAwait(false);

            await using (audioStream.ConfigureAwait(false))
            {
                StatusChanged?.Invoke(this, "Loading accurate model...");

                var finalOptions = new WhisperTranscriberOptions
                {
                    ModelSize = request.FinalModelSize,
                    ModelCacheDirectory = request.ModelCacheDirectory,
                    LanguageOverride = request.LanguageOverride,
                    AllowedLanguages = ParseAllowedLanguages(request.AllowedLanguages),
                };

                await using var finalTranscriber = await WhisperTranscriber.CreateAsync(finalOptions, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                finalBackend = finalTranscriber.Backend;

                // TranscribeFileAsync reports 0-100% per chunk (~15-25s of audio each - see its
                // own remarks) regardless of total file length, so even a multi-hour recording
                // keeps moving visibly instead of appearing to hang; it is not pre-scanned for
                // duration up front, since the only way to learn that is the same WAV parse
                // TranscribeFileAsync already does internally, and duplicating it here would just
                // load the whole file twice for a number that would be stale within seconds
                // anyway.
                var progress = new Progress<int>(p =>
                {
                    var status = $"Transcribing {sourceInfo.Name}... {p}%";
                    StatusChanged?.Invoke(this, status);
                    FinalProgressChanged?.Invoke(this, new FinalProgress(status, p));
                });

                var result = await finalTranscriber.TranscribeFileAsync(audioStream, progress, cancellationToken)
                    .ConfigureAwait(false);

                finalTranscript = result.Segments
                    .Where(s => s.Text.Trim().Length > 0)
                    .Select(s => new TranscriptLine(
                        AudioSourceKind.Microphone, // placeholder, never shown - see SourceLabelOverride below
                        s.Start,
                        s.End,
                        s.Text.Trim(),
                        s.Language,
                        s.Probability)
                    {
                        SourceLabelOverride = "Recording",
                    })
                    .ToList();

                if (finalTranscript.Count > 0)
                {
                    TranscriptWriter.WriteTxt(Path.Combine(meetingFolder, "transcript.txt"), finalTranscript);
                    TranscriptWriter.WriteVtt(Path.Combine(meetingFolder, "transcript.vtt"), finalTranscript);
                    TranscriptWriter.WriteJson(Path.Combine(meetingFolder, "transcript.json"), finalTranscript);
                }
                else
                {
                    StatusChanged?.Invoke(this, "No speech detected in the recording.");
                }
            }

            string? minutesMarkdown = null;
            string? minutesError = "No transcript to generate minutes from.";

            if (finalTranscript.Count > 0)
            {
                (minutesMarkdown, minutesError) = await GenerateMinutesAsync(
                    finalTranscript,
                    request.MinutesProvider,
                    request.OllamaBaseUrl,
                    request.OllamaModel,
                    request.OllamaNumCtx,
                    request.MinutesPromptTemplate,
                    request.MinutesTimeoutSeconds,
                    request.Title,
                    startedUtc,
                    meetingFolder,
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteImportMetaAsync(
                meetingFolder, request, sourceInfo.FullName, startedUtc, DateTime.UtcNow, finalBackend,
                minutesMarkdown is not null, minutesError, cancellationToken).ConfigureAwait(false);

            Completed?.Invoke(this, new MeetingCompletedEventArgs(meetingFolder, finalTranscript, minutesMarkdown, minutesError));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                IsBusy = false;
            }
        }
    }

    private async Task InitializeLiveEngineAsync(StartRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var options = new WhisperTranscriberOptions
            {
                ModelSize = request.LiveModelSize,
                ModelCacheDirectory = request.ModelCacheDirectory,
                LanguageOverride = request.LanguageOverride,
                AllowedLanguages = ParseAllowedLanguages(request.AllowedLanguages),
            };

            var transcriber = await WhisperTranscriber.CreateAsync(options, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var enabledSources = new List<AudioSourceKind>();
            if (request.MicrophoneEnabled)
            {
                enabledSources.Add(AudioSourceKind.Microphone);
            }

            if (request.SystemAudioEnabled)
            {
                enabledSources.Add(AudioSourceKind.SystemAudio);
            }

            var engine = new LiveTranscriptionEngine(transcriber, enabledSources, request.LiveTranslationEnabled);
            engine.SegmentReady += (_, e) => LiveSegmentReady?.Invoke(this, e);
            engine.TranslationReady += (_, e) => LiveTranslationReady?.Invoke(this, e);
            engine.EngineError += (_, ex) => RecorderErrorOccurred?.Invoke(this, $"Live transcription: {ex.Message}");

            _liveTranscriber = transcriber;
            _liveEngine = engine;

            if (IsRecording)
            {
                StatusChanged?.Invoke(this, "Recording. Live transcript active.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecorderErrorOccurred?.Invoke(this, $"Live model failed to load: {ex.Message}");
            StatusChanged?.Invoke(this, "Recording. Live transcript unavailable (model load failed).");
        }
    }

    /// <summary>
    /// Stops recording, then runs stage 2 (accurate re-transcription) and stage 3
    /// (minutes) in the background before raising <see cref="Completed"/>. A stage-3
    /// failure (e.g. the claude CLI is missing) does not discard the stage-2 transcript
    /// already written to disk - <see cref="Completed"/> still fires with the transcript
    /// and a populated <c>MinutesError</c>.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IMeetingRecorder recorder;
        StartRequest request;
        string meetingFolder;
        DateTime startedUtc;

        lock (_stateLock)
        {
            if (!IsRecording || _recorder is null || _activeRequest is null || CurrentMeetingFolder is null)
            {
                return;
            }

            IsBusy = true;
            recorder = _recorder;
            request = _activeRequest;
            meetingFolder = CurrentMeetingFolder;
            startedUtc = _startedUtc;
        }

        var stoppedUtc = DateTime.UtcNow;
        var recorderErrors = Array.Empty<string>();

        try
        {
            StatusChanged?.Invoke(this, "Stopping recording...");
            await recorder.StopAsync().ConfigureAwait(false);
            recorderErrors = [.. recorder.Errors];
            IsRecording = false;

            // Wait for InitializeLiveEngineAsync to finish first (it may still be
            // mid-flight if Stop() lands right after Start()) - otherwise it can assign
            // _liveEngine/_liveTranscriber after the null-checks below already ran,
            // leaking the just-loaded live model instead of disposing it.
            if (_liveInitTask is not null)
            {
                try
                {
                    await _liveInitTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Already reported (if applicable) inside InitializeLiveEngineAsync's
                    // own catch; nothing more to do here.
                }

                _liveInitTask = null;
            }

            var liveEngine = _liveEngine;
            _liveEngine = null;
            if (liveEngine is not null)
            {
                StatusChanged?.Invoke(this, "Finishing live transcript...");
                // FlushAllAsync also drains already-queued live translations (bounded, best-effort
                // - see LiveTranscriptionEngine.FlushAllAsync's remarks) before returning, so the
                // snapshot below reflects them when they finished in time.
                await liveEngine.FlushAllAsync(cancellationToken).ConfigureAwait(false);

                var liveSnapshot = liveEngine.GetSnapshot();
                if (liveSnapshot.Count > 0)
                {
                    // The live (stage 1) transcript - and any English translations attached to it
                    // - was never persisted before; this is its only durable record, independent
                    // of the stage-2 accurate pass below (which never carries translations).
                    TranscriptWriter.WriteTxt(Path.Combine(meetingFolder, "live_transcript.txt"), liveSnapshot);
                    TranscriptWriter.WriteJson(Path.Combine(meetingFolder, "live_transcript.json"), liveSnapshot);
                }

                await liveEngine.DisposeAsync().ConfigureAwait(false);
            }

            if (_liveTranscriber is not null)
            {
                await _liveTranscriber.DisposeAsync().ConfigureAwait(false);
                _liveTranscriber = null;
            }

            await recorder.DisposeAsync().ConfigureAwait(false);
            _recorder = null;

            var tracks = new List<(AudioSourceKind Source, string WavPath)>();
            if (request.MicrophoneEnabled && File.Exists(recorder.MicWavPath))
            {
                tracks.Add((AudioSourceKind.Microphone, recorder.MicWavPath));
            }

            if (request.SystemAudioEnabled && File.Exists(recorder.SystemWavPath))
            {
                tracks.Add((AudioSourceKind.SystemAudio, recorder.SystemWavPath));
            }

            IReadOnlyList<TranscriptLine> finalTranscript = [];
            BackendInfo? finalBackend = null;

            if (tracks.Count > 0)
            {
                StatusChanged?.Invoke(this, "Loading accurate model for final pass...");

                var finalOptions = new WhisperTranscriberOptions
                {
                    ModelSize = request.FinalModelSize,
                    ModelCacheDirectory = request.ModelCacheDirectory,
                    LanguageOverride = request.LanguageOverride,
                    AllowedLanguages = ParseAllowedLanguages(request.AllowedLanguages),
                };

                await using var finalTranscriber = await WhisperTranscriber.CreateAsync(finalOptions, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                finalBackend = finalTranscriber.Backend;

                var progress = new Progress<FinalProgress>(p =>
                {
                    StatusChanged?.Invoke(this, p.Status);
                    FinalProgressChanged?.Invoke(this, p);
                });

                var trackResults = await new FinalTranscriptionEngine()
                    .TranscribeTracksAsync(finalTranscriber, tracks, progress, cancellationToken)
                    .ConfigureAwait(false);

                finalTranscript = TranscriptMerger.Merge(trackResults);

                TranscriptWriter.WriteTxt(Path.Combine(meetingFolder, "transcript.txt"), finalTranscript);
                TranscriptWriter.WriteVtt(Path.Combine(meetingFolder, "transcript.vtt"), finalTranscript);
                TranscriptWriter.WriteJson(Path.Combine(meetingFolder, "transcript.json"), finalTranscript);
            }
            else
            {
                StatusChanged?.Invoke(this, "No audio captured; skipping final transcription.");
            }

            string? minutesMarkdown = null;
            string? minutesError = null;

            if (finalTranscript.Count > 0)
            {
                (minutesMarkdown, minutesError) = await GenerateMinutesAsync(
                    finalTranscript,
                    request.MinutesProvider,
                    request.OllamaBaseUrl,
                    request.OllamaModel,
                    request.OllamaNumCtx,
                    request.MinutesPromptTemplate,
                    request.MinutesTimeoutSeconds,
                    request.Title,
                    startedUtc,
                    meetingFolder,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                minutesError = "No transcript to generate minutes from.";
            }

            await WriteMetaAsync(
                meetingFolder, request, startedUtc, stoppedUtc, recorderErrors, finalBackend, minutesMarkdown is not null, minutesError,
                cancellationToken).ConfigureAwait(false);

            Completed?.Invoke(this, new MeetingCompletedEventArgs(meetingFolder, finalTranscript, minutesMarkdown, minutesError));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsRecording = false;
            Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                IsBusy = false;
                _activeRequest = null;
            }
        }
    }

    private static async Task WriteMetaAsync(
        string meetingFolder,
        StartRequest request,
        DateTime startedUtc,
        DateTime stoppedUtc,
        IReadOnlyList<string> recorderErrors,
        BackendInfo? finalBackend,
        bool minutesGenerated,
        string? minutesError,
        CancellationToken cancellationToken)
    {
        var meta = new MeetingMetadata
        {
            Title = request.Title,
            StartedUtc = startedUtc,
            StoppedUtc = stoppedUtc,
            Duration = stoppedUtc - startedUtc,
            MicrophoneEnabled = request.MicrophoneEnabled,
            MicrophoneDeviceName = request.MicrophoneDeviceName,
            SystemAudioEnabled = request.SystemAudioEnabled,
            SystemAudioDeviceName = request.SystemAudioDeviceName,
            LiveModel = request.LiveModelSize.ToString(),
            FinalModel = request.FinalModelSize.ToString(),
            LanguageOverride = request.LanguageOverride,
            FinalTranscriptionBackend = finalBackend?.LoadedLibrary,
            FinalTranscriptionIsGpu = finalBackend?.IsGpuBackend,
            MinutesGenerated = minutesGenerated,
            MinutesError = minutesError,
            RecorderErrors = recorderErrors,
        };

        var json = JsonSerializer.Serialize(meta, MetaJsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(meetingFolder, "meta.json"),
            json,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary><see cref="WriteMetaAsync"/>'s counterpart for <see cref="ImportAsync"/> - same
    /// meta.json shape, populated from an <see cref="ImportRequest"/> instead of a
    /// <see cref="StartRequest"/> (no mic/system devices, no live model - see
    /// <see cref="MeetingMetadata.Imported"/>).</summary>
    private static async Task WriteImportMetaAsync(
        string meetingFolder,
        ImportRequest request,
        string sourceFilePath,
        DateTime startedUtc,
        DateTime stoppedUtc,
        BackendInfo? finalBackend,
        bool minutesGenerated,
        string? minutesError,
        CancellationToken cancellationToken)
    {
        var meta = new MeetingMetadata
        {
            Title = request.Title,
            StartedUtc = startedUtc,
            StoppedUtc = stoppedUtc,
            Duration = stoppedUtc - startedUtc,
            MicrophoneEnabled = false,
            SystemAudioEnabled = false,
            Imported = true,
            ImportedSourceFile = sourceFilePath,
            LiveModel = "N/A (imported recording - no live pass)",
            FinalModel = request.FinalModelSize.ToString(),
            LanguageOverride = request.LanguageOverride,
            FinalTranscriptionBackend = finalBackend?.LoadedLibrary,
            FinalTranscriptionIsGpu = finalBackend?.IsGpuBackend,
            MinutesGenerated = minutesGenerated,
            MinutesError = minutesError,
        };

        var json = JsonSerializer.Serialize(meta, MetaJsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(meetingFolder, "meta.json"),
            json,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage 3 (minutes) shared between <see cref="StopAsync"/> (a live-recorded meeting) and
    /// <see cref="ImportAsync"/> (an imported recording) - identical prompt/timeout/progress
    /// handling either way, since minutes generation itself does not know or care where the
    /// transcript it is fed came from.
    /// </summary>
    private async Task<(string? Markdown, string? Error)> GenerateMinutesAsync(
        IReadOnlyList<TranscriptLine> finalTranscript,
        MinutesProviderKind minutesProvider,
        string ollamaBaseUrl,
        string ollamaModel,
        int ollamaNumCtx,
        string minutesPromptTemplate,
        int minutesTimeoutSeconds,
        string title,
        DateTime startedUtc,
        string meetingFolder,
        CancellationToken cancellationToken)
    {
        var provider = CreateMinutesProvider(minutesProvider, ollamaBaseUrl, ollamaModel, ollamaNumCtx);
        StatusChanged?.Invoke(this, $"Generating minutes via {provider.DisplayName}...");
        try
        {
            var transcriptText = TranscriptWriter.ToPlainText(finalTranscript);
            var minutesProgress = new Progress<TimeSpan>(elapsed =>
                MinutesProgressChanged?.Invoke(this, new MinutesProgress(provider.DisplayName, elapsed)));

            var minutesMarkdown = await provider
                .GenerateAsync(
                    transcriptText,
                    minutesPromptTemplate,
                    title,
                    startedUtc.ToLocalTime().ToString("yyyy-MM-dd"),
                    TimeSpan.FromSeconds(minutesTimeoutSeconds),
                    minutesProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(meetingFolder, "minutes.md"),
                minutesMarkdown,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            StatusChanged?.Invoke(this, "Minutes ready.");
            return (minutesMarkdown, null);
        }
        catch (MinutesGenerationException ex)
        {
            StatusChanged?.Invoke(this, $"Minutes generation failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsRecording && _recorder is not null)
        {
            try
            {
                await _recorder.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort shutdown on app close; the recording is being abandoned anyway, so
                // a failure here must not throw out of Dispose -- but it must not vanish
                // silently either. Trace.TraceWarning (not Debug.WriteLine) so this survives in
                // Release builds, where [Conditional("DEBUG")] would otherwise strip it.
                Trace.TraceWarning(
                    $"MeetingSessionController.DisposeAsync: recorder StopAsync failed during shutdown: {ex}");
            }
        }

        if (_liveInitTask is not null)
        {
            try
            {
                await _liveInitTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Same rationale as the StopAsync() catch above: nothing left to salvage,
                // just make sure _liveEngine/_liveTranscriber are settled before disposal.
            }
        }

        if (_liveEngine is not null)
        {
            await _liveEngine.DisposeAsync().ConfigureAwait(false);
        }

        if (_liveTranscriber is not null)
        {
            await _liveTranscriber.DisposeAsync().ConfigureAwait(false);
        }

        if (_recorder is not null)
        {
            await _recorder.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Picks the <see cref="IMinutesProvider"/> for this run from the settings snapshot captured
    /// at Start()/ImportAsync() time - same pattern as every other per-meeting setting on
    /// <see cref="StartRequest"/>/<see cref="ImportRequest"/> (model sizes, prompt template,
    /// etc.): changing the Settings tab mid-run never affects a meeting already in progress.
    /// Takes the four minutes-related fields directly (not a whole request) since
    /// <see cref="StartRequest"/> and <see cref="ImportRequest"/> share no common base type.
    /// </summary>
    private static IMinutesProvider CreateMinutesProvider(
        MinutesProviderKind minutesProvider, string ollamaBaseUrl, string ollamaModel, int ollamaNumCtx) => minutesProvider switch
    {
        MinutesProviderKind.Claude => new ClaudeMinutesProvider(),
        MinutesProviderKind.Codex => new CodexMinutesProvider(),
        MinutesProviderKind.Ollama => new OllamaMinutesProvider(ollamaBaseUrl, ollamaModel, ollamaNumCtx),
        _ => throw new NotSupportedException($"Unknown minutes provider '{minutesProvider}'."),
    };

    /// <summary>
    /// Parses the Settings-tab comma-separated allowed-language string (see
    /// <see cref="AppSettings.AllowedLanguages"/>) into the collection
    /// <see cref="WhisperTranscriberOptions.AllowedLanguages"/> expects. Null or
    /// whitespace-only input means "no restriction" - null here lets
    /// <see cref="WhisperTranscriberOptions.AllowedLanguages"/>'s own default apply only when the
    /// property is left unset; since this always sets it explicitly, an empty/blank setting is
    /// how a user opts out of the restriction entirely from the Settings tab.
    /// </summary>
    private static IReadOnlyCollection<string>? ParseAllowedLanguages(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var codes = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return codes.Count > 0 ? codes : null;
    }
}
