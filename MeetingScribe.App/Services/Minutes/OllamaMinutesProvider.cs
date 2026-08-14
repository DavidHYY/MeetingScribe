using System.Net.Http;
using MeetingScribe.App.Models;

namespace MeetingScribe.App.Services;

/// <summary>
/// Local-model backend via Ollama's HTTP API - the no-subscription default. Unlike the CLI
/// providers (which get the transcript "for free" as CLI stdin the tool itself merges with the
/// prompt argument), Ollama's HTTP API has no such split, so the rendered prompt and the raw
/// transcript are combined into one request body here, with the transcript wrapped in an
/// explicit <c>&lt;transcript&gt;</c> block - matching the <c>&lt;stdin&gt;</c> wrapping codex uses, so
/// the model sees the same visible instructions/data separation either way.
///
/// Honest caveat (see README): on a CPU-only machine with no CUDA GPU, a small local model
/// writing minutes from a mixed Japanese/Mandarin/English/Polish/French transcript will be
/// noticeably worse than Claude or Codex. This class does not hide that trade-off; Settings
/// surfaces it next to the provider picker.
///
/// <b>Context-window truncation guard.</b> Ollama silently defaults to a 4096-token context
/// window unless <c>options.num_ctx</c> is set explicitly on the request (a confirmed real bug -
/// see README). This class always sets it, from <see cref="AppSettings.OllamaNumCtx"/>, clamped
/// down further to the model's own reported maximum when <c>/api/show</c> exposes one (see
/// <see cref="OllamaClient.GetMaxContextLengthAsync"/>). Even with that, any fixed window can
/// still be overflowed by a long enough meeting, so before every call the prompt is checked
/// against the effective window using a character-based token estimate (adequate for this
/// project's CJK-heavy transcripts - see <see cref="GenerateAsync"/>); if it is estimated to
/// overflow, a plain-language warning is prepended to the returned minutes rather than silently
/// producing minutes grounded in only a fraction of the meeting.
/// </summary>
public sealed class OllamaMinutesProvider : IMinutesProvider
{
    // One shared HttpClient for the process lifetime (the standard .NET guidance: creating a new
    // HttpClient per call risks socket exhaustion under load). Its own Timeout is left infinite;
    // every call manages its own timeout via a linked CancellationTokenSource instead, so the
    // failure message is consistent with the CLI providers' timeout wording.
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly int _numCtx;
    private readonly OllamaClient _client;

    /// <param name="numCtx">
    /// Configured context window in tokens (<see cref="AppSettings.OllamaNumCtx"/>). Defaults to
    /// <see cref="AppSettings.DefaultOllamaNumCtx"/> for call sites (availability/model-list
    /// checks) that never reach <see cref="GenerateAsync"/> and so don't need a real value.
    /// Non-positive values fall back to the same default rather than being sent to Ollama as-is.
    /// </param>
    public OllamaMinutesProvider(string baseUrl, string model, int numCtx = AppSettings.DefaultOllamaNumCtx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl;
        _model = model ?? string.Empty;
        _numCtx = numCtx > 0 ? numCtx : AppSettings.DefaultOllamaNumCtx;
        _client = new OllamaClient(baseUrl, SharedHttpClient);
    }

    public MinutesProviderKind Kind => MinutesProviderKind.Ollama;

    public string DisplayName => string.IsNullOrWhiteSpace(_model) ? "Ollama (no model selected)" : $"Ollama ({_model})";

    /// <summary>Reachability only - does not require a model to be selected. Used by Settings to check the server independent of the model picker.</summary>
    public static Task<MinutesProviderAvailability> CheckServerAvailabilityAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        new OllamaClient(baseUrl, SharedHttpClient).CheckAvailabilityAsync(TimeSpan.FromSeconds(5), cancellationToken);

    /// <summary>Lists models the server actually has pulled, for the Settings model picker - never a blind-typed name.</summary>
    public static Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        new OllamaClient(baseUrl, SharedHttpClient).ListModelsAsync(TimeSpan.FromSeconds(10), cancellationToken);

    /// <summary>Checks the server is reachable AND that the configured model is actually pulled - both are required for GenerateAsync to succeed.</summary>
    public async Task<MinutesProviderAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var reachability = await _client.CheckAvailabilityAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (!reachability.IsAvailable)
        {
            return reachability;
        }

        if (string.IsNullOrWhiteSpace(_model))
        {
            return new MinutesProviderAvailability(false, $"{reachability.Reason} No model selected.");
        }

        try
        {
            var models = await _client.ListModelsAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
            {
                return new MinutesProviderAvailability(false, $"{reachability.Reason} No models pulled yet (run 'ollama pull <model>').");
            }

            if (!models.Contains(_model, StringComparer.OrdinalIgnoreCase))
            {
                return new MinutesProviderAvailability(
                    false, $"{reachability.Reason} Model '{_model}' is not pulled. Available: {string.Join(", ", models)}.");
            }

            return new MinutesProviderAvailability(true, $"{reachability.Reason} Model '{_model}' is pulled.");
        }
        catch (MinutesGenerationException ex)
        {
            return new MinutesProviderAvailability(false, ex.Message);
        }
    }

    public async Task<string> GenerateAsync(
        string transcriptText,
        string promptTemplate,
        string meetingTitle,
        string meetingDate,
        TimeSpan timeout,
        IProgress<TimeSpan>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcriptText);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptTemplate);
        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new MinutesGenerationException("No Ollama model selected. Open Settings, click 'Refresh Models', and pick one.");
        }

        var instructions = MinutesTextUtils.RenderPrompt(promptTemplate, meetingTitle, meetingDate);
        var prompt = $"{instructions}\n\n<transcript>\n{transcriptText}\n</transcript>\n";

        // Prefer the model's own reported maximum (best-effort; null if /api/show is
        // unreachable or doesn't match the expected shape) over the configured value, so we
        // never ask Ollama to honour a window the model can't actually provide.
        var maxContext = await _client.GetMaxContextLengthAsync(_model, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var effectiveNumCtx = maxContext is > 0 ? Math.Min(_numCtx, maxContext.Value) : _numCtx;

        var overflowWarning = BuildOverflowWarningOrNull(prompt, effectiveNumCtx, wasClampedToModelMax: effectiveNumCtx < _numCtx ? maxContext : null);

        var text = await _client.GenerateAsync(_model, prompt, effectiveNumCtx, timeout, progress, cancellationToken).ConfigureAwait(false);
        var minutes = MinutesTextUtils.StripCodeFence(text);

        return overflowWarning is null ? minutes : $"{overflowWarning}\n\n{minutes}";
    }

    /// <summary>
    /// Character-based token estimate: this project's real transcripts are Japanese/Mandarin-
    /// heavy, and CJK text runs close to 1 token per character, so counting characters is an
    /// adequate proxy without needing a real tokenizer. For the English/Latin-script portions
    /// this over-counts (they typically run several characters per token), which makes the
    /// estimate conservative - it errs toward warning rather than missing a real overflow, which
    /// matches this fix's purpose (guard against silent truncation, not measure it precisely).
    /// Returns <c>null</c> if the estimate does not exceed <paramref name="numCtx"/>.
    /// </summary>
    /// <param name="numCtx">The effective (possibly already clamped) window actually sent to Ollama.</param>
    /// <param name="wasClampedToModelMax">
    /// Non-null only when <paramref name="numCtx"/> was reduced from the configured value because
    /// the model's own reported maximum was smaller - i.e. the window in the warning came from
    /// the model, not the user's Settings value. Null when no clamping happened (whether because
    /// the model's max was unknown, or was >= the configured value), so the message never
    /// falsely claims a clamp that didn't occur.
    /// </param>
    private static string? BuildOverflowWarningOrNull(string prompt, int numCtx, int? wasClampedToModelMax)
    {
        var estimatedTokens = prompt.Length;
        if (estimatedTokens <= numCtx)
        {
            return null;
        }

        var clampNote = wasClampedToModelMax is > 0 ? $" (clamped down from your configured value to the model's own maximum of {wasClampedToModelMax:N0})" : string.Empty;
        return "> **WARNING: this transcript is estimated to exceed Ollama's context window and these minutes are likely INCOMPLETE.** " +
               $"Estimated prompt size ~{estimatedTokens:N0} tokens vs. a configured window of {numCtx:N0} tokens{clampNote}. " +
               "Ollama silently drops the start of the transcript once the window fills, so early topics, decisions, and dates may be " +
               "missing from what follows. Increase 'Ollama context window' in Settings (if the model supports it) or switch providers " +
               "for this meeting.";
    }
}
