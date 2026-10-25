using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// A typed coding-agent search tool, mediated by authenticated PaRuX Core.
    /// It never forwards the Core bearer token to a website or runs shell commands.
    /// Its evidence is bounded and is explicitly labelled as untrusted source text.
    /// </summary>
    internal static class CodingWebSearchClient
    {
        private const int MaxResponseBytes = 24_000;
        private static readonly HttpClient Http =
            PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));

        internal sealed record Outcome(bool Success, string Evidence);

        internal static bool IsValidQuery(string? query) =>
            !string.IsNullOrWhiteSpace(query)
            && query.Length <= 512
            && !System.Linq.Enumerable.Any(query, char.IsControl);

        public static async Task<Outcome> SearchAsync(string query, CancellationToken ct)
        {
            if (!IsValidQuery(query))
                return new Outcome(false, "Web search rejected: invalid query (maximum 512 characters).");

            try
            {
                ServerBootstrap server = await PaRuXServerController.Current
                    .EnsureRunningAsync(null, ct).ConfigureAwait(false);
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/coding/web-search")
                {
                    Content = JsonContent.Create(new { query = query.Trim(), maxResults = 5 })
                };
                request.Headers.Add("X-PaRuX-Token", server.Token);
                using HttpResponseMessage response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string reason = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Conflict =>
                            "Web search unavailable: Internet is disabled or the task was interrupted.",
                        System.Net.HttpStatusCode.Forbidden =>
                            "Web search refused: desktop owner authorization is required.",
                        _ => "Web search provider unavailable (HTTP " + (int)response.StatusCode + ")."
                    };
                    return new Outcome(false, reason);
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    return new Outcome(false, "Web search response exceeded the evidence limit.");

                await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                byte[] bytes = new byte[MaxResponseBytes + 1];
                int used = 0;
                while (used < bytes.Length)
                {
                    int count = await stream.ReadAsync(
                        bytes.AsMemory(used, bytes.Length - used), ct).ConfigureAwait(false);
                    if (count == 0) break;
                    used += count;
                }
                if (used > MaxResponseBytes)
                    return new Outcome(false, "Web search response exceeded the evidence limit.");
                using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, used));
                return ParseResults(document.RootElement);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                or TimeoutException or JsonException or OperationCanceledException)
            {
                return new Outcome(false, "Web search unavailable; no source results were verified.");
            }
        }

        internal static Outcome ParseResults(JsonElement root)
        {
            var projection = CodingWebSearchEvidenceFormatter.Render(root);
            return new Outcome(projection.Success, projection.Evidence);
        }

    }
}
