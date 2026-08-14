using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingScribe.App.Services;

/// <summary>
/// Thin wrapper around Ollama's local HTTP API (default <c>http://localhost:11434</c>): list
/// installed models (<c>GET /api/tags</c>), check reachability, look up a model's own maximum
/// context length (<c>POST /api/show</c>), and generate text (<c>POST /api/generate</c>,
/// non-streaming). The JSON field names below match Ollama's documented REST API (a stable
/// surface unchanged across releases: <c>models</c>/<c>name</c> for <c>/api/tags</c>,
/// <c>model</c>/<c>prompt</c>/<c>stream</c>/<c>options.num_ctx</c>/<c>response</c> for
/// <c>/api/generate</c>) and, for <c>/api/show</c>, <c>details.family</c> plus a
/// <c>model_info</c> map keyed by architecture-prefixed names (e.g.
/// <c>"qwen3.context_length"</c>) - confirmed live on 2026-08-11 against a running server with
/// <c>qwen3:8b</c> pulled (<c>curl -s http://localhost:11434/api/show -d
/// '{"model":"qwen3:8b"}'</c> returned <c>model_info["qwen3.context_length"] == 40960</c>,
/// matching <c>details.family == "qwen3"</c>); see the "Ollama" section of the project README.
/// UTF-8 is set explicitly on the request body's Content-Type (not left to a default) - a
/// transcript here can contain Japanese/Chinese/Polish/French, and this project has an explicit
/// UTF-8-everywhere rule after past cp950 corruption.
/// </summary>
internal sealed class OllamaClient(string baseUrl, HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _baseUrl = baseUrl;
    private readonly Uri _baseUri = new(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    public async Task<MinutesProviderAvailability> CheckAvailabilityAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await httpClient.GetAsync(new Uri(_baseUri, "api/tags"), linkedCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new MinutesProviderAvailability(true, $"Reachable at {_baseUrl}.")
                : new MinutesProviderAvailability(false, $"Ollama at {_baseUrl} returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MinutesProviderAvailability(false, $"Ollama at {_baseUrl} did not respond within {timeout.TotalSeconds:F0}s (is it running?).");
        }
        catch (HttpRequestException ex)
        {
            return new MinutesProviderAvailability(false, $"Ollama not reachable at {_baseUrl}: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(new Uri(_baseUri, "api/tags"), linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MinutesGenerationException($"Ollama at {_baseUrl} did not respond within {timeout.TotalSeconds:F0}s listing models.");
        }
        catch (HttpRequestException ex)
        {
            throw new MinutesGenerationException($"Could not reach Ollama at {_baseUrl} to list models: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MinutesGenerationException($"Ollama at {_baseUrl} returned HTTP {(int)response.StatusCode} listing models.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            TagsResponse? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<TagsResponse>(stream, JsonOptions, linkedCts.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new MinutesGenerationException($"Ollama at {_baseUrl} returned unparseable JSON for /api/tags: {ex.Message}", ex);
            }

            return payload?.Models?
                .Select(m => m.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList() ?? [];
        }
    }

    /// <summary>
    /// Non-streaming generate call. <paramref name="prompt"/> already contains the full
    /// instructions and transcript. <paramref name="numCtx"/> is sent explicitly as
    /// <c>options.num_ctx</c> - Ollama silently falls back to its own 4096-token default when
    /// this is omitted, regardless of what the model itself supports (e.g. <c>qwen3:8b</c>
    /// supports 40960), which truncates a real meeting transcript from the front with no error.
    /// Callers are expected to have already clamped <paramref name="numCtx"/> to the model's own
    /// maximum via <see cref="GetMaxContextLengthAsync"/> where possible.
    /// </summary>
    public async Task<string> GenerateAsync(string model, string prompt, int numCtx, TimeSpan timeout, IProgress<TimeSpan>? progress, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var progressReporter = ElapsedProgressReporter.Start(progress, TimeSpan.FromSeconds(2));

        var requestJson = JsonSerializer.Serialize(new GenerateRequest(model, prompt, false, new GenerateOptions(numCtx)), JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync(new Uri(_baseUri, "api/generate"), content, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new MinutesGenerationException($"Ollama ('{model}' at {_baseUrl}) timed out after {timeout.TotalSeconds:F0}s generating minutes.");
        }
        catch (HttpRequestException ex)
        {
            throw new MinutesGenerationException($"Could not reach Ollama at {_baseUrl}: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new MinutesGenerationException($"Ollama returned HTTP {(int)response.StatusCode}: {body.Trim()}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            GenerateResponse? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<GenerateResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new MinutesGenerationException($"Ollama at {_baseUrl} returned unparseable JSON for /api/generate: {ex.Message}", ex);
            }

            var text = payload?.Response?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                throw new MinutesGenerationException($"Ollama ('{model}') produced no output.");
            }

            return text;
        }
    }

    /// <summary>
    /// Best-effort lookup of the model's own maximum context length via <c>POST /api/show</c>,
    /// used to clamp a configured <c>num_ctx</c> down to what the model can actually honour
    /// instead of sending a value it silently ignores. Ollama nests this under
    /// <c>model_info</c> with an architecture-prefixed key rather than a fixed field name (e.g.
    /// <c>"qwen3.context_length"</c> for a model whose <c>details.family</c> is
    /// <c>"qwen3"</c>) - confirmed live 2026-08-11 against <c>qwen3:8b</c>, see the class doc
    /// comment. Returns <c>null</c> (never throws, except on real caller cancellation) on any
    /// failure - server unreachable, model not found, timeout, or a response shape that does not
    /// match this pattern (a future Ollama release could rename the field) - so a clamp failure
    /// never blocks minutes generation; the caller falls back to the configured <c>num_ctx</c>.
    /// </summary>
    public async Task<int?> GetMaxContextLengthAsync(string model, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            var requestJson = JsonSerializer.Serialize(new ShowRequest(model), JsonOptions);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            response = await httpClient.PostAsync(new Uri(_baseUri, "api/show"), content, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                var payload = await JsonSerializer.DeserializeAsync<ShowResponse>(stream, JsonOptions, linkedCts.Token).ConfigureAwait(false);

                var family = payload?.Details?.Family;
                if (string.IsNullOrWhiteSpace(family) || payload?.ModelInfo is null)
                {
                    return null;
                }

                if (payload.ModelInfo.TryGetValue($"{family}.context_length", out var element) &&
                    element.ValueKind == JsonValueKind.Number &&
                    element.TryGetInt32(out var contextLength) &&
                    contextLength > 0)
                {
                    return contextLength;
                }

                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private sealed record TagsResponse([property: JsonPropertyName("models")] List<OllamaTagEntry>? Models);

    private sealed record OllamaTagEntry([property: JsonPropertyName("name")] string Name);

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] GenerateOptions Options);

    private sealed record GenerateOptions([property: JsonPropertyName("num_ctx")] int NumCtx);

    private sealed record GenerateResponse([property: JsonPropertyName("response")] string? Response);

    private sealed record ShowRequest([property: JsonPropertyName("model")] string Model);

    private sealed record ShowResponse(
        [property: JsonPropertyName("details")] ShowDetails? Details,
        [property: JsonPropertyName("model_info")] Dictionary<string, JsonElement>? ModelInfo);

    private sealed record ShowDetails([property: JsonPropertyName("family")] string? Family);
}
