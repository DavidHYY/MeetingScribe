namespace MeetingScribe.App.Services;

/// <summary>
/// Raised by any <see cref="IMinutesProvider"/> when it cannot produce minutes: the backing CLI
/// is missing from PATH, an HTTP endpoint (Ollama) is unreachable, the call times out, or the
/// backend exits/responds with an error.
/// </summary>
public sealed class MinutesGenerationException : Exception
{
    public MinutesGenerationException(string message) : base(message)
    {
    }

    public MinutesGenerationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
