using System.Net;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server.Orchestration;

/// <summary>
/// Read-only compatibility probe for an optional, already-running OpenCode server.
/// This does not start OpenCode, create sessions, forward credentials or grant tool
/// authority. A local health response alone never proves process identity or grants
/// permission to execute code; those belong to a future supervised backend worker.
/// </summary>
public static class OpenCodeLocalProbe
{
    private const int MaxHealthResponseBytes = 4_096;
    private static readonly HttpClient ProbeClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public sealed record Result(bool Healthy, string Status, string? Version);

    /// <summary>Only explicit IPv4 loopback, root-path HTTP URLs are permitted.</summary>
    public static bool IsAllowedEndpoint(Uri? endpoint) =>
        endpoint != null
        && endpoint.IsAbsoluteUri
        && string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
        // Uri.Host canonicalization can turn shorthand or alternate-form IPv4
        // spellings into loopback; reject those rather than trusting normalization.
        && endpoint.OriginalString.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
        && string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal)
        && endpoint.Port is > 0 and <= 65535
        && string.IsNullOrEmpty(endpoint.UserInfo)
        && endpoint.AbsolutePath == "/"
        && string.IsNullOrEmpty(endpoint.Query)
        && string.IsNullOrEmpty(endpoint.Fragment);

    public static Task<Result> CheckAsync(Uri endpoint, CancellationToken cancellationToken) =>
        CheckAsync(endpoint, ProbeClient, cancellationToken);

    /// <summary>
    /// Injectable HTTP client for isolated contract tests. Production always uses a
    /// redirect-disabled, no-proxy client above to prevent accidental network egress.
    /// </summary>
    internal static async Task<Result> CheckAsync(
        Uri endpoint, HttpClient http, CancellationToken cancellationToken)
    {
        if (!IsAllowedEndpoint(endpoint))
            return new Result(false, "invalid_local_endpoint", null);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(endpoint, "global/health"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(false, "timeout", null);
        }
        catch (HttpRequestException)
        {
            return new Result(false, "unreachable", null);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new Result(false, "authentication_required", null);
            if (response.StatusCode != HttpStatusCode.OK)
                return new Result(false, "unexpected_http_status", null);
            if (response.Content.Headers.ContentLength > MaxHealthResponseBytes)
                return new Result(false, "response_too_large", null);

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                var buffer = new byte[MaxHealthResponseBytes + 1];
                int read = 0;
                while (read < buffer.Length)
                {
                    int count = await stream.ReadAsync(
                        buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                        .ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                }
                if (read > MaxHealthResponseBytes)
                    return new Result(false, "response_too_large", null);

                using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, read));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("healthy", out JsonElement healthy)
                    || healthy.ValueKind != JsonValueKind.True
                    || !root.TryGetProperty("version", out JsonElement version)
                    || version.ValueKind != JsonValueKind.String)
                    return new Result(false, "invalid_health_response", null);
                string? value = version.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
                    return new Result(false, "invalid_health_response", null);
                return new Result(true, "healthy_unverified_identity", value);
            }
            catch (JsonException)
            {
                return new Result(false, "invalid_health_response", null);
            }
        }
    }
}
