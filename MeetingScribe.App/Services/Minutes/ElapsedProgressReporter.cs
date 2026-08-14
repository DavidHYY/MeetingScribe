using System.Diagnostics;

namespace MeetingScribe.App.Services;

/// <summary>
/// Ticks <c>progress</c> with elapsed wall-clock time on a fixed interval for as long as this is
/// not disposed - used by every <see cref="IMinutesProvider"/> so a caller can render "still
/// running, Ns elapsed" instead of a progress indicator that looks stuck during a slow
/// generation (a local CPU model can legitimately run for several minutes). No-op if the given
/// progress sink is null.
/// </summary>
internal sealed class ElapsedProgressReporter : IDisposable
{
    private readonly Stopwatch _stopwatch;
    private readonly Timer? _timer;

    private ElapsedProgressReporter(IProgress<TimeSpan>? progress, TimeSpan interval)
    {
        _stopwatch = Stopwatch.StartNew();
        _timer = progress is null
            ? null
            : new Timer(_ => progress.Report(_stopwatch.Elapsed), null, interval, interval);
    }

    public static ElapsedProgressReporter Start(IProgress<TimeSpan>? progress, TimeSpan interval) =>
        new(progress, interval);

    public void Dispose() => _timer?.Dispose();
}
