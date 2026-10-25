using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Counts coding request tokens through the existing desktop-authenticated
    /// PaRuX Core. No prompt is sent directly to the model provider or a website.
    /// Returns null when exact managed llama tokenization is unsupported; never
    /// silently calls an approximate value "exact".
    /// </summary>
    internal static class CodingRenderedTokenProbeClient
    {
        private static readonly HttpClient Client =
            PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(75));

        public static async Task<int?> TryCountAsync(
            string systemPrompt, string userPrompt,
            string schemaName, string jsonSchema, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(systemPrompt)
                || string.IsNullOrWhiteSpace(userPrompt)
                || systemPrompt.Length + (long)userPrompt.Length > 250_000
                || jsonSchema.Length > 15_000)
                return null;
            try
            {
                ServerBootstrap bootstrap = PaRuXServerController.Current.GetCurrentBootstrap();
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    bootstrap.Endpoint.TrimEnd('/') + "/api/v1/coding/prompt-token-count")
                {
                    Content = JsonContent.Create(new {
                        systemPrompt, userPrompt, schemaName, jsonSchema
                    })
                };
                request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                using HttpResponseMessage response = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 512)
                    return null;
                await using var stream = await response.Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                byte[] bytes = new byte[513];
                int used = 0;
                while (used < bytes.Length)
                {
                    int count = await stream.ReadAsync(
                        bytes.AsMemory(used, bytes.Length - used), ct).ConfigureAwait(false);
                    if (count == 0) break;
                    used += count;
                }
                if (used > 512) return null;
                using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, used));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("exact", out JsonElement exact)
                    || exact.ValueKind != JsonValueKind.True
                    || !root.TryGetProperty("inputTokens", out JsonElement value)
                    || !value.TryGetInt32(out int tokens) || tokens <= 0)
                    return null;
                return tokens;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or System.IO.IOException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }
    }

}
