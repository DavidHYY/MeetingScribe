using System.Security.Cryptography;

namespace MeetingScribe.App.Services.Update;

/// <summary>Result of comparing a downloaded file's actual SHA-256 against the expected value from its SHA256SUMS.txt sidecar.</summary>
internal sealed record InstallerVerificationResult(bool Match, string ExpectedHash, string ActualHash);

/// <summary>
/// Computes and compares a file's SHA-256 against an expected hash. Split out from
/// <see cref="UpdateChecker"/> so hash verification can be exercised directly against a local
/// file in tests/manual verification, independent of any network call - builds of this project
/// are not reproducible (the PE header embeds a compile timestamp), so this can never assume a
/// hash checked into source; it only ever compares against whatever the matching release's own
/// SHA256SUMS.txt says.
/// </summary>
internal static class InstallerVerifier
{
    /// <summary>Hashes <paramref name="filePath"/> and compares it (case-insensitively) against <paramref name="expectedHash"/>. Never throws for a hash mismatch - that is a normal, reportable outcome, not an exceptional one.</summary>
    public static async Task<InstallerVerificationResult> VerifyAsync(string filePath, string expectedHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);

        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var expected = expectedHash.Trim().ToLowerInvariant();

        return new InstallerVerificationResult(
            Match: string.Equals(actualHash, expected, StringComparison.Ordinal),
            ExpectedHash: expected,
            ActualHash: actualHash);
    }
}
