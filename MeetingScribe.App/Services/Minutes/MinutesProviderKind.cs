namespace MeetingScribe.App.Services;

/// <summary>Which backend <see cref="MeetingSessionController"/> uses to turn a transcript into minutes.</summary>
public enum MinutesProviderKind
{
    /// <summary>Claude Code CLI (<c>claude -p</c>). Requires a Claude subscription/authenticated CLI.</summary>
    Claude,

    /// <summary>Codex CLI (<c>codex exec</c>). Requires an authenticated OpenAI/ChatGPT CLI.</summary>
    Codex,

    /// <summary>Local model via Ollama's HTTP API. No subscription; quality/speed depend on the local machine and model.</summary>
    Ollama,
}
