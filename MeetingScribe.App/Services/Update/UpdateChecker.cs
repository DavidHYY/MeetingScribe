using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingScribe.App.Services.Update;

public enum UpdateCheckStatus
{
    /// <summary>Running version is the same as, or newer than, the latest published release.</summary>
    UpToDate,

    /// <summary>Latest published release is newer than the running version.</summary>
    UpdateAvailable,

    /// <summary>Could not determine an answer - network/DNS failure, GitHub unreachable, rate-limited, no releases published, or an unparseable response. Never thrown as an exception; always a result.</summary>
    Error,
}

/// <param name="InstallerDownloadUrl">Direct download URL for the "MeetingScribe-Setup-*-win-x64.exe" asset, or null if the release does not have one.</param>
/// <param name="ChecksumDownloadUrl">Direct download URL for the "SHA256SUMS.txt" asset, or null if this release predates that sidecar.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersionRaw,
    Version? LatestVersion,
    string? InstallerDownloadUrl,
    string? ChecksumDownloadUrl,
    string? ReleaseHtmlUrl,
    string? Message);

/// <param name="SidecarMissing">True specifically when the release had no SHA256SUMS.txt (or no matching entry in it) - the caller uses this to offer "open the release page" instead of a generic failure.</param>
public sealed record UpdateApplyResult(bool Success, string Message, bool SidecarMissing);

/// <summary>
/// Checks GitHub Releases for a newer MeetingScribe build, and - if the release carries a
/// verifiable installer - downloads it, verifies its SHA-256 against the release's own
/// SHA256SUMS.txt sidecar, and launches it. Never replaces the running app's own files; the
/// installer (already verified end-to-end for in-place upgrade) does that.
/// </summary>
internal sealed class UpdateChecker
{
    private const string RepoOwner = "DavidHYY";
    private const string RepoName = "MeetingScribe";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    // GitHub REQUIRES a User-Agent header on API requests or it returns 403 - not optional.
    // One shared HttpClient for the process lifetime (standard .NET guidance against per-call
    // HttpClient instances); each call applies its own timeout via a linked
    // CancellationTokenSource rather than the client's own Timeout, so a short "check" call and a
    // longer "download the installer" call can each use an appropriate value.
    private static readonly HttpClient SharedHttpClient = BuildHttpClient();

    private static HttpClient BuildHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MeetingScribe-UpdateChecker", GetUserAgentVersion()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string GetUserAgentVersion()
    {
        var version = UpdateVersion.GetRunningVersion();
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Calls the GitHub Releases API once. Never throws - every failure mode (network, DNS,
    /// timeout, non-2xx status including 403 rate-limit, unparseable JSON, missing/malformed
    /// tag_name) becomes an <see cref="UpdateCheckStatus.Error"/> result with a human-readable
    /// <see cref="UpdateCheckResult.Message"/> instead.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await SharedHttpClient.GetAsync(LatestReleaseApiUrl, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error($"GitHub did not respond within {timeout.TotalSeconds:F0}s.");
        }
        catch (HttpRequestException ex)
        {
            return Error($"Could not reach GitHub: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Error("GitHub returned HTTP 403 (rate-limited or blocked). Unauthenticated requests are limited to 60/hour/IP.");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Error($"No published releases found for {RepoOwner}/{RepoName}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Error($"GitHub returned HTTP {(int)response.StatusCode}.");
            }

            GitHubReleaseDto? release;
            try
            {
                release = await response.Content.ReadFromJsonAsync<GitHubReleaseDto>(cancellationToken: linkedCts.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Error($"GitHub returned unparseable JSON: {ex.Message}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Error($"GitHub did not respond within {timeout.TotalSeconds:F0}s.");
            }

            if (release is null)
            {
                return Error("GitHub returned an empty response.");
            }

            if (!UpdateVersion.TryParse(release.TagName, out var latestVersion))
            {
                return Error($"Release tag '{release.TagName ?? "(none)"}' is not a recognizable version - cannot compare.");
            }

            var runningVersion = UpdateVersion.GetRunningVersion();
            var assets = release.Assets ?? [];
            var installerUrl = assets.FirstOrDefault(IsInstallerAsset)?.BrowserDownloadUrl;
            var checksumUrl = assets.FirstOrDefault(IsChecksumAsset)?.BrowserDownloadUrl;

            if (!UpdateVersion.IsNewer(latestVersion, runningVersion))
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, release.TagName, latestVersion, installerUrl, checksumUrl, release.HtmlUrl, null);
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release.TagName, latestVersion, installerUrl, checksumUrl, release.HtmlUrl, null);
        }
    }

    private static bool IsInstallerAsset(GitHubAssetDto asset) =>
        asset.Name is not null &&
        asset.Name.StartsWith("MeetingScribe-Setup-", StringComparison.OrdinalIgnoreCase) &&
        asset.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase) &&
        asset.BrowserDownloadUrl is not null;

    private static bool IsChecksumAsset(GitHubAssetDto asset) =>
        string.Equals(asset.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) &&
        asset.BrowserDownloadUrl is not null;

    private static UpdateCheckResult Error(string message) =>
        new(UpdateCheckStatus.Error, null, null, null, null, null, message);

    /// <summary>
    /// Downloads the installer and its SHA256SUMS.txt sidecar, verifies the hash, and - only if
    /// it matches - launches the installer and returns success. A hash mismatch is always
    /// reported as a failure (never a silent skip); a missing sidecar or installer asset is
    /// reported with <see cref="UpdateApplyResult.SidecarMissing"/> set, and nothing is ever run
    /// unverified.
    /// </summary>
    public async Task<UpdateApplyResult> DownloadVerifyAndLaunchAsync(
        UpdateCheckResult checkResult,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkResult);
        if (checkResult.Status != UpdateCheckStatus.UpdateAvailable)
        {
            throw new InvalidOperationException($"{nameof(DownloadVerifyAndLaunchAsync)} requires an {nameof(UpdateCheckStatus.UpdateAvailable)} result.");
        }

        if (checkResult.InstallerDownloadUrl is null)
        {
            return new UpdateApplyResult(false, $"Release {checkResult.LatestVersionRaw} has no installer asset (expected a name like 'MeetingScribe-Setup-{checkResult.LatestVersionRaw}-win-x64.exe').", SidecarMissing: true);
        }

        if (checkResult.ChecksumDownloadUrl is null)
        {
            return new UpdateApplyResult(false, $"Release {checkResult.LatestVersionRaw} has no SHA256SUMS.txt sidecar - refusing to download and run an unverified installer.", SidecarMissing: true);
        }

        string tempDir;
        string installerPath;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), "MeetingScribeUpdate");
            Directory.CreateDirectory(tempDir);
            var installerFileName = GetFileNameFromUrl(checkResult.InstallerDownloadUrl);
            installerPath = Path.Combine(tempDir, installerFileName);

            onProgress?.Invoke($"Downloading {installerFileName}...");
            await DownloadToFileAsync(checkResult.InstallerDownloadUrl, installerPath, cancellationToken).ConfigureAwait(false);

            onProgress?.Invoke("Downloading SHA256SUMS.txt...");
            var sumsContent = await SharedHttpClient.GetStringAsync(checkResult.ChecksumDownloadUrl, cancellationToken).ConfigureAwait(false);

            var expectedHash = Sha256SumsFile.FindHash(sumsContent, installerFileName);
            if (expectedHash is null)
            {
                TryDelete(installerPath);
                return new UpdateApplyResult(false, $"SHA256SUMS.txt does not list an entry for '{installerFileName}' - refusing to run an unverified installer.", SidecarMissing: true);
            }

            onProgress?.Invoke("Verifying download...");
            var verification = await InstallerVerifier.VerifyAsync(installerPath, expectedHash, cancellationToken).ConfigureAwait(false);
            if (!verification.Match)
            {
                TryDelete(installerPath);
                return new UpdateApplyResult(
                    false,
                    $"SHA-256 verification FAILED for {installerFileName}: expected {verification.ExpectedHash}, got {verification.ActualHash}. " +
                    "The download may be corrupt or tampered with - it will NOT be run.",
                    SidecarMissing: false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateApplyResult(false, "Update download timed out.", SidecarMissing: false);
        }
        catch (HttpRequestException ex)
        {
            return new UpdateApplyResult(false, $"Update download failed: {ex.Message}", SidecarMissing: false);
        }
        catch (IOException ex)
        {
            return new UpdateApplyResult(false, $"Could not write the downloaded installer to disk: {ex.Message}", SidecarMissing: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new UpdateApplyResult(false, $"Could not write the downloaded installer to disk: {ex.Message}", SidecarMissing: false);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new UpdateApplyResult(false, "Verified installer downloaded, but automatic launch is only supported on Windows - download it manually from the release page.", SidecarMissing: false);
        }

        try
        {
            onProgress?.Invoke("Launching installer...");
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new UpdateApplyResult(false, $"Verified installer downloaded, but could not be launched: {ex.Message}", SidecarMissing: false);
        }

        return new UpdateApplyResult(true, "Installer launched.", SidecarMissing: false);
    }

    private static async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = File.Create(destinationPath);
        await httpStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private static string GetFileNameFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : Path.GetFileName(url);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a rejected download; leaving a stray temp file is not worth failing the caller over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above.
        }
    }

    private sealed record GitHubReleaseDto(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] List<GitHubAssetDto>? Assets);

    private sealed record GitHubAssetDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
