using System.IO;
using MeetingScribe.App.Services;
using MeetingScribe.Whisper;

namespace MeetingScribe.App.Models;

/// <summary>Persisted application settings (JSON, UTF-8, under %LocalAppData%\MeetingScribeCS).</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Portable default: <c>Documents\MeetingScribeCS\Meetings</c> under the current user's
    /// profile. Computed at runtime rather than hardcoded, since the original fixed path was
    /// specific to one developer's machine.
    /// </summary>
    public static string DefaultOutputRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "MeetingScribeCS",
        "Meetings");

    public static string DefaultModelCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetingScribeCS",
        "models");

    /// <summary>
    /// Default minutes prompt. Editable in Settings. Transcript lines are labeled
    /// "[Mic]" (our side) / "[System]" (the remote side) - the prompt calls this out
    /// explicitly so action-item ownership can actually be attributed, which is the
    /// documented gap in the Python prototype's minutes.
    /// </summary>
    public const string DefaultMinutesPromptTemplate = """
        You are producing formal meeting minutes from a timestamped, track-labeled speech-to-text transcript of a real meeting (it may mix Japanese, Mandarin Chinese, and English). The transcript is provided on stdin below, as lines like "[hh:mm:ss] [Mic] text" and "[hh:mm:ss] [System] text". [Mic] is our side (the meeting host's own microphone); [System] is the remote/other side, captured from system audio (e.g. other participants on a call or in the room's speakers). Use these track labels to attribute action items to the correct side - that is the main reason each line carries one. Do not invent content that is not supported by the transcript.

        Meeting title: {title}
        Meeting date: {date}

        Produce a Markdown document with these sections, in this order:

        ## Attendees / Speakers
        List speakers as far as they can be inferred from the transcript (names if stated, otherwise "Our side" / "Other side" or "Speaker 1", "Speaker 2" in order of first appearance).

        ## Agenda / Topics Discussed
        Bullet list of the topics actually discussed, in the order they came up.

        ## Decisions Made
        Bullet list of concrete decisions. If none, write "None recorded."

        ## Action Items
        Table with columns: Owner | Action | Due Date. Use the [Mic]/[System] track to help identify Owner (e.g. "Our side", "Other side", or a named person if stated). Use "unspecified" for owner or due date when the transcript does not state them. If none, write "None recorded."

        ## Open Questions
        Bullet list of unresolved questions or follow-ups raised in the meeting. If none, write "None recorded."

        Keep the language of each quoted point close to what was actually said; translate only enough to make the minutes readable in English, and note the original language in parentheses for non-English decisions/action items.
        """;

    public string OutputRoot { get; set; } = DefaultOutputRoot;

    public string ModelCacheDirectory { get; set; } = DefaultModelCacheDirectory;

    /// <summary>Stage 1 (during the meeting): fast, rough. Default base per the design spec.</summary>
    public WhisperModelSize LiveModelSize { get; set; } = WhisperModelSize.Base;

    /// <summary>Stage 2 (after Stop): accurate, background. Default medium per the design spec.</summary>
    public WhisperModelSize FinalModelSize { get; set; } = WhisperModelSize.Medium;

    /// <summary>Null/empty/"auto" = auto-detect (default). Otherwise a 2-letter code such as "en", "ja", "zh".</summary>
    public string? LanguageOverride { get; set; }

    /// <summary>
    /// Comma-separated two-letter language codes <see cref="WhisperTranscriber.TranscribeFileAsync"/>
    /// is allowed to switch into during auto-detect (see <see cref="WhisperTranscriberOptions.AllowedLanguages"/>).
    /// Default matches the languages this project's real meetings mix: Japanese, English, Mandarin,
    /// Polish, French. Empty string = no restriction (any language whisper.cpp detects is eligible).
    /// </summary>
    public string AllowedLanguages { get; set; } = string.Join(',', WhisperTranscriberOptions.DefaultAllowedLanguages);

    public string MinutesPromptTemplate { get; set; } = DefaultMinutesPromptTemplate;

    public int MinutesTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Which backend generates minutes: Claude Code CLI (default; unchanged pre-existing
    /// behaviour, requires a subscription/authenticated CLI), Codex CLI (requires an
    /// authenticated OpenAI/ChatGPT CLI), or a local Ollama model (no subscription - see
    /// <see cref="OllamaBaseUrl"/>/<see cref="OllamaModel"/>).
    /// </summary>
    public MinutesProviderKind MinutesProvider { get; set; } = MinutesProviderKind.Claude;

    public const string DefaultOllamaBaseUrl = "http://localhost:11434";

    /// <summary>
    /// Ollama server base URL. Configurable, not hardcoded to localhost, because Ollama can run
    /// on another host on the LAN.
    /// </summary>
    public string OllamaBaseUrl { get; set; } = DefaultOllamaBaseUrl;

    /// <summary>
    /// Ollama model tag used for minutes generation (e.g. "llama3.1:8b"). Populated in Settings
    /// from the server's actual <c>/api/tags</c> list via "Refresh Models" - never typed blind.
    /// Empty until the user picks one.
    /// </summary>
    public string OllamaModel { get; set; } = string.Empty;

    public const int DefaultOllamaNumCtx = 16384;

    /// <summary>
    /// Ollama context window (<c>options.num_ctx</c>, in tokens) sent explicitly on every
    /// <c>/api/generate</c> call. Ollama silently defaults to 4096 tokens when this is never
    /// set on the request - unrelated to what the model itself supports (e.g. <c>qwen3:8b</c>
    /// supports 40960) - and a transcript longer than that gets silently truncated from the
    /// front, producing minutes grounded only in the tail of the meeting; this was a confirmed
    /// real bug (see README's "Ollama" section). 16384 is the smallest value measured, against
    /// the real 2026-08-07 transcript (~11,200 CJK-heavy characters), to keep the whole
    /// transcript in-window with headroom left for the model's response. <see
    /// cref="OllamaMinutesProvider"/> clamps this down further at call time if the model's own
    /// maximum (from <c>/api/show</c>) is smaller, and warns - in the UI and in the generated
    /// file - rather than silently truncating if the estimated prompt size still exceeds the
    /// (possibly clamped) window.
    /// </summary>
    public int OllamaNumCtx { get; set; } = DefaultOllamaNumCtx;

    /// <summary>
    /// Off by default. When on, the live (stage 1) transcript also gets a live English
    /// translation per line, via a second whisper.cpp decode of the same audio
    /// (<c>translate</c> task - English only, there is no other target; see
    /// <see cref="MeetingScribe.App.Services.LiveTranscriptionEngine"/>). Never affects the
    /// stage-2 accurate pass.
    /// </summary>
    public bool LiveTranslationEnabled { get; set; }

    public bool MicrophoneEnabled { get; set; } = true;

    /// <summary>Null = OS default recording device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public bool SystemAudioEnabled { get; set; } = true;

    /// <summary>Null = OS default playback device.</summary>
    public string? SystemAudioDeviceId { get; set; }

    public string LastMeetingTitle { get; set; } = string.Empty;
}
