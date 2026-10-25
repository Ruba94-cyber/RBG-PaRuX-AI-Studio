using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// GitHub-first PaRuX application update client. Release discovery is informational unless the
    /// release contains BOTH the exact versioned MSI and detached signed update envelope, this
    /// build contains the corresponding public trust anchor, and Desktop/Update Helper are inside
    /// the protected official Program Files installation. Downloaded bytes are size/SHA-256
    /// verified against the signed manifest before Update Manager may invoke the elevated helper.
    ///
    /// A private GitHub repository cannot be distinguished from a missing repository by treating an
    /// unauthenticated 404 as "no release". PaRuX therefore fails that state closed. Maintainers or
    /// explicitly-authorized private users may opt in by providing a personal GitHub credential via
    /// PARUX_GITHUB_UPDATE_TOKEN. The credential is read from the process environment for each
    /// operation, never compiled into PaRuX, never persisted by this client, and never becomes a
    /// trust anchor: the detached release signature remains mandatory for installation.
    /// </summary>
    public sealed class AppUpdateClient
    {
        internal const string GitHubUpdateTokenEnvironmentVariable = "PARUX_GITHUB_UPDATE_TOKEN";
        private const string LatestReleaseApi =
            "https://api.github.com/repos/Ruba94-cyber/RBG-PaRuX-AI-Studio/releases/latest";

        public static AppUpdateClient Current { get; } = new AppUpdateClient();
        private static readonly HttpClient ApiHttp = CreateApiClient();
        private static readonly HttpClient DownloadHttp = CreateDownloadClient();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private AppUpdateClient() { }

        public string CurrentVersion => NormalizeInstalledVersion(
            typeof(AppUpdateClient).Assembly.GetName().Version?.ToString() ?? "0.0.0");

        public Task<AppReleaseStatus> CheckLatestAsync(CancellationToken ct)
            => CheckLatestAsync(ApiHttp, LatestReleaseApi, ReadGitHubUpdateToken(), CurrentVersion, ct);

        /// <summary>Test seam: same discovery logic against an injected client/endpoint.</summary>
        internal async Task<AppReleaseStatus> CheckLatestAsync(
            HttpClient apiHttp, string latestReleaseUrl, string? githubToken, string current, CancellationToken ct)
        {
            bool authenticated = githubToken != null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, latestReleaseUrl);
                ApplyGitHubAuthorization(request, githubToken);
                using HttpResponseMessage response = await apiHttp.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return DiscoveryHttpFailure(current, response.StatusCode, authenticated);

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                ReleaseResponse? release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(stream, JsonOptions, ct)
                    .ConfigureAwait(false);
                if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                    return new AppReleaseStatus
                    {
                        CurrentVersion = current,
                        CheckSucceeded = false,
                        DiscoveryState = "invalid-release-metadata",
                        DiscoveryAuthenticated = authenticated,
                        Message = "Release metadata was empty or invalid; PaRuX cannot prove whether an update exists.",
                        SecureInstallerMessage = "Secure installer metadata was unavailable."
                    };

                string latest = NormalizeVersion(release.TagName);
                bool updateAvailable = CompareVersions(latest, current) > 0;
                var status = new AppReleaseStatus
                {
                    CurrentVersion = current,
                    LatestVersion = latest,
                    ReleaseName = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                    ReleaseUrl = release.HtmlUrl,
                    PublishedUtc = release.PublishedAt,
                    CheckSucceeded = true,
                    DiscoveryState = "verified-release",
                    DiscoveryAuthenticated = authenticated,
                    UpdateAvailable = updateAvailable,
                    Message = updateAvailable
                        ? "A newer published PaRuX release is available."
                        : "The authenticated/public release metadata confirms this PaRuX build is not older than the latest published release."
                };

                if (!updateAvailable)
                {
                    status.SecureInstallerMessage = "No application upgrade is required.";
                    return status;
                }

                string expectedMsi = "PaRuX-" + latest + "-win-x64.msi";
                string expectedEnvelope = "PaRuX-" + latest + "-win-x64.update.json";
                IReadOnlyList<ReleaseAsset> assets = release.Assets ?? new List<ReleaseAsset>();
                ReleaseAsset? msi = assets.FirstOrDefault(a =>
                    string.Equals(a.Name, expectedMsi, StringComparison.Ordinal));
                ReleaseAsset? envelope = assets.FirstOrDefault(a =>
                    string.Equals(a.Name, expectedEnvelope, StringComparison.Ordinal));
                if (msi == null || envelope == null
                    || string.IsNullOrWhiteSpace(msi.BrowserDownloadUrl)
                    || string.IsNullOrWhiteSpace(envelope.BrowserDownloadUrl))
                {
                    status.SecureInstallerMessage =
                        "The GitHub Release does not contain the expected versioned MSI + signed update envelope.";
                    return status;
                }

                if (!ReleaseTrustAnchor.IsConfigured)
                {
                    status.SecureInstallerMessage =
                        "Secure in-app MSI installation is disabled because this build has no release public key configured.";
                    return status;
                }

                // Never elevate a helper from a portable/developer/user-writable application copy.
                // This check is repeated immediately before the MSI download/handoff so a stale
                // AppReleaseStatus cannot bypass the boundary.
                try { AppUpdateInstallBoundary.ResolveProtectedHelperPath(); }
                catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
                {
                    status.SecureInstallerMessage = ex.Message;
                    return status;
                }

                string envelopeJson = await DownloadSmallTextAsync(
                    envelope.BrowserDownloadUrl, envelope.ApiUrl,
                    SignedAppUpdateVerifier.MaximumEnvelopeBytes, githubToken, ct).ConfigureAwait(false);
                SignedAppUpdateVerification verification = SignedAppUpdateVerifier.Verify(
                    envelopeJson, ReleaseTrustAnchor.PublicKeyPem);
                if (!verification.Valid || verification.Manifest == null)
                {
                    status.SecureInstallerMessage = "Signed MSI manifest rejected: " + verification.Message;
                    return status;
                }

                SignedAppUpdateManifest manifest = verification.Manifest;
                if (!string.Equals(NormalizeVersion(manifest.Version), latest, StringComparison.Ordinal)
                    || !UrisEqual(manifest.PackageUrl, msi.BrowserDownloadUrl)
                    || (msi.Size > 0 && manifest.PackageSizeBytes != msi.Size))
                {
                    status.SecureInstallerMessage =
                        "Signed MSI manifest does not match the GitHub Release asset metadata.";
                    return status;
                }

                status.SecureInstallerReady = true;
                status.SecureInstallerMessage =
                    "Signed MSI manifest, exact source commit and package metadata verified. Download/install remains an explicit action and is re-verified after elevation.";
                status.SourceCommit = manifest.SourceCommit;
                status.SignedManifest = manifest;
                status.SignedEnvelopeJson = envelopeJson;
                status.InstallerAssetName = msi.Name;
                status.InstallerAssetApiUrl = msi.ApiUrl;
                return status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                return new AppReleaseStatus
                {
                    CurrentVersion = current,
                    CheckSucceeded = false,
                    DiscoveryState = "timeout",
                    DiscoveryAuthenticated = authenticated,
                    Message = "The release check timed out; update availability is unknown.",
                    SecureInstallerMessage = "Secure installer check was not performed because release discovery was not verified."
                };
            }
            catch (JsonException)
            {
                return new AppReleaseStatus
                {
                    CurrentVersion = current,
                    CheckSucceeded = false,
                    DiscoveryState = "invalid-release-metadata",
                    DiscoveryAuthenticated = authenticated,
                    Message = "Release metadata was empty or invalid; PaRuX cannot prove whether an update exists.",
                    SecureInstallerMessage = "Secure installer metadata was unavailable."
                };
            }
            catch (Exception ex)
            {
                return new AppReleaseStatus
                {
                    CurrentVersion = current,
                    CheckSucceeded = false,
                    DiscoveryState = "transport-or-validation-error",
                    DiscoveryAuthenticated = authenticated,
                    Message = "Release check unavailable or unverifiable: " + ex.Message,
                    SecureInstallerMessage = "Secure installer check failed before any installation action."
                };
            }
        }

        public async Task<string> DownloadVerifiedMsiAsync(
            AppReleaseStatus release,
            IProgress<AppUpdateDownloadProgress>? progress,
            CancellationToken ct)
        {
            if (release == null) throw new ArgumentNullException(nameof(release));
            if (!release.SecureInstallerReady || release.SignedManifest == null
                || string.IsNullOrWhiteSpace(release.SignedEnvelopeJson))
                throw new InvalidOperationException("This release has not passed signed MSI verification.");

            // Re-check at the action boundary, not only during release discovery. Once this passes,
            // the helper resides below Program Files and a standard user cannot replace it during
            // the remaining download/UAC handoff window.
            AppUpdateInstallBoundary.ResolveProtectedHelperPath();

            SignedAppUpdateManifest manifest = release.SignedManifest;
            Uri packageUri = new(manifest.PackageUrl, UriKind.Absolute);
            string fileName = Uri.UnescapeDataString(Path.GetFileName(packageUri.AbsolutePath));
            string updateRoot = Path.Combine(
                PaRuX.Contracts.PaRuXDataRoot.Path, "updates", "app");
            Directory.CreateDirectory(updateRoot);
            string finalPath = Path.Combine(updateRoot, fileName);
            string pendingPath = Path.Combine(updateRoot, ".pending-" + fileName);

            try
            {
                if (File.Exists(pendingPath)) File.Delete(pendingPath);

                string? githubToken = ReadGitHubUpdateToken();
                using HttpRequestMessage request = CreateAssetDownloadRequest(
                    manifest.PackageUrl, release.InstallerAssetApiUrl, githubToken);
                using HttpResponseMessage response = await DownloadHttp.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidDataException(
                        "MSI download returned HTTP " + (int)response.StatusCode
                        + (response.StatusCode == HttpStatusCode.NotFound
                            ? "; for a private release, provide an authorized " + GitHubUpdateTokenEnvironmentVariable + " credential"
                            : string.Empty) + ".");

                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value != manifest.PackageSizeBytes)
                    throw new InvalidDataException("GitHub MSI Content-Length does not match the signed manifest.");

                await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var target = new FileStream(
                    pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    long downloaded = 0;
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                        if (read == 0) break;
                        downloaded += read;
                        if (downloaded > manifest.PackageSizeBytes
                            || downloaded > SignedAppUpdateVerifier.MaximumPackageBytes)
                            throw new InvalidDataException("MSI download exceeded the signed size bound.");
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        progress?.Report(new AppUpdateDownloadProgress
                        {
                            BytesDownloaded = downloaded,
                            TotalBytes = manifest.PackageSizeBytes
                        });
                    }
                    await target.FlushAsync(ct).ConfigureAwait(false);
                }

                await MsiAppUpdatePackage.VerifyPackageAsync(pendingPath, manifest, ct).ConfigureAwait(false);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(pendingPath, finalPath);
                return finalPath;
            }
            catch
            {
                try { if (File.Exists(pendingPath)) File.Delete(pendingPath); } catch { }
                throw;
            }
        }

        internal static int CompareVersions(string left, string right)
        {
            if (Version.TryParse(NumericVersion(left), out Version? l)
                && Version.TryParse(NumericVersion(right), out Version? r))
                return l.CompareTo(r);
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        internal static AppReleaseStatus DiscoveryHttpFailure(
            string currentVersion, HttpStatusCode statusCode, bool authenticated)
        {
            string state;
            string message;
            switch (statusCode)
            {
                case HttpStatusCode.NotFound:
                    state = "unknown-not-found-or-inaccessible";
                    message = authenticated
                        ? "GitHub returned HTTP 404 even with the supplied update credential. The release/repository may be absent, or that credential may not have access; PaRuX cannot verify 'no update'."
                        : "GitHub returned HTTP 404. Private repositories intentionally look like missing repositories to unauthenticated callers, so PaRuX cannot verify 'no update'. Provide " + GitHubUpdateTokenEnvironmentVariable + " for authorized private-release access or publish the update channel publicly.";
                    break;
                case HttpStatusCode.Unauthorized:
                    state = "authentication-rejected";
                    message = "GitHub rejected the update credential (HTTP 401); update availability is unknown.";
                    break;
                case HttpStatusCode.Forbidden:
                    state = "forbidden-or-rate-limited";
                    message = "GitHub refused the release check (HTTP 403). Access, policy or rate limiting prevented update verification.";
                    break;
                default:
                    state = "http-error";
                    message = "Release check returned HTTP " + (int)statusCode + "; update availability is unknown.";
                    break;
            }

            return new AppReleaseStatus
            {
                CurrentVersion = currentVersion,
                CheckSucceeded = false,
                DiscoveryState = state,
                DiscoveryAuthenticated = authenticated,
                Message = message,
                SecureInstallerMessage = "Secure installer check was not performed because release discovery was not verified."
            };
        }

        private static async Task<string> DownloadSmallTextAsync(
            string browserUrl, string? apiUrl, int maxBytes, string? githubToken, CancellationToken ct)
        {
            using HttpRequestMessage request = CreateAssetDownloadRequest(browserUrl, apiUrl, githubToken);
            using HttpResponseMessage response = await DownloadHttp.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException("Signed manifest download returned HTTP " + (int)response.StatusCode + ".");
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                throw new InvalidDataException("Signed manifest is larger than the allowed bound.");

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > maxBytes)
                    throw new InvalidDataException("Signed manifest is larger than the allowed bound.");
                await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            return System.Text.Encoding.UTF8.GetString(memory.ToArray());
        }

        private static HttpRequestMessage CreateAssetDownloadRequest(
            string browserUrl, string? apiUrl, string? githubToken)
        {
            string url = githubToken != null && !string.IsNullOrWhiteSpace(apiUrl) ? apiUrl! : browserUrl;
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (githubToken != null && !string.IsNullOrWhiteSpace(apiUrl))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                ApplyGitHubAuthorization(request, githubToken);
            }
            return request;
        }

        private static string? ReadGitHubUpdateToken()
        {
            string? token = Environment.GetEnvironmentVariable(GitHubUpdateTokenEnvironmentVariable)?.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        private static void ApplyGitHubAuthorization(HttpRequestMessage request, string? githubToken)
        {
            if (!string.IsNullOrWhiteSpace(githubToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }

        private static bool UrisEqual(string left, string right)
        {
            if (!Uri.TryCreate(left, UriKind.Absolute, out Uri? l)
                || !Uri.TryCreate(right, UriKind.Absolute, out Uri? r)) return false;
            return string.Equals(l.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                r.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), StringComparison.Ordinal);
        }

        private static string NormalizeInstalledVersion(string value)
        {
            string numeric = NumericVersion(value);
            if (!Version.TryParse(numeric, out Version? version)) return NormalizeVersion(value);
            int build = version.Build < 0 ? 0 : version.Build;
            return version.Major + "." + version.Minor + "." + build;
        }

        private static string NormalizeVersion(string value) => (value ?? string.Empty).Trim().TrimStart('v', 'V');

        private static string NumericVersion(string value)
        {
            string normalized = NormalizeVersion(value);
            int suffix = normalized.IndexOfAny(new[] { '-', '+' });
            return suffix >= 0 ? normalized[..suffix] : normalized;
        }

        private static HttpClient CreateApiClient()
        {
            var client = CreateDownloadClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PaRuX", "1.0"));
            return client;
        }

        private sealed class ReleaseResponse
        {
            [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
            [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
            [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
            [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
            [JsonPropertyName("assets")] public List<ReleaseAsset>? Assets { get; set; } = new();
        }

        private sealed class ReleaseAsset
        {
            [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
            [JsonPropertyName("url")] public string? ApiUrl { get; set; }
            [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
            [JsonPropertyName("size")] public long Size { get; set; }
        }
    }

    public sealed class AppReleaseStatus
    {
        public string CurrentVersion { get; set; } = string.Empty;
        public string? LatestVersion { get; set; }
        public string? ReleaseName { get; set; }
        public string? ReleaseUrl { get; set; }
        public DateTime? PublishedUtc { get; set; }
        public bool CheckSucceeded { get; set; }
        /// <summary>Machine-readable discovery truth: verified-release, unknown-not-found-or-inaccessible,
        /// authentication-rejected, forbidden-or-rate-limited, invalid-release-metadata, timeout,
        /// transport-or-validation-error or http-error.</summary>
        public string DiscoveryState { get; set; } = "not-checked";
        public bool DiscoveryAuthenticated { get; set; }
        public bool UpdateAvailable { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool SecureInstallerReady { get; set; }
        public string SecureInstallerMessage { get; set; } = string.Empty;
        public string? InstallerAssetName { get; set; }
        public string? InstallerAssetApiUrl { get; set; }
        public string? SourceCommit { get; set; }
        public SignedAppUpdateManifest? SignedManifest { get; set; }
        /// <summary>The exact already-verified detached envelope. It is persisted only temporarily
        /// beside the downloaded MSI so the elevated helper can authenticate it independently.</summary>
        public string? SignedEnvelopeJson { get; set; }
    }

    public sealed class AppUpdateDownloadProgress
    {
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesDownloaded * 100L / TotalBytes, 0, 100);
    }
}