namespace MeetingScribe.Audio;

/// <summary>
/// A live RMS/peak reading for one capture source, normalized to 0.0-1.0 against the
/// full 16-bit PCM range (1.0 == digital full scale, ±32768). Intended for driving a
/// UI level meter, not for precise loudness measurement.
/// </summary>
/// <param name="Rms">Root-mean-square level of the most recently written block.</param>
/// <param name="Peak">Peak absolute sample value of the most recently written block.</param>
/// <param name="TimestampUtc">
/// When this reading was produced. <see cref="DateTime.MinValue"/> (the default) means
/// no audio block has been captured yet for this source.
/// </param>
public readonly record struct AudioLevel(double Rms, double Peak, DateTime TimestampUtc)
{
    /// <summary>True once at least one real audio block has been measured.</summary>
    public bool HasData => TimestampUtc != DateTime.MinValue;
}
