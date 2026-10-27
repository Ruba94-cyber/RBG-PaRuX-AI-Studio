using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Local music generation through PaRuX Core (ACE-Step 1.5 in the managed ComfyUI). Returns the saved FLAC and its artifact path.</summary>
    public sealed class MusicClient
    {
        public static MusicClient Current { get; } = new MusicClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(Timeout.InfiniteTimeSpan);
        private MusicClient() { }

        public async Task<MediaAsset> GenerateAsync(MusicGenerateRequest request, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/music/generate")
            {
                Content = JsonContent.Create(request)
            };
            message.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(message, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Music generation failed (" + (int)response.StatusCode + "): " + ExtractError(detail));
            }
            MediaAsset? asset = await response.Content.ReadFromJsonAsync<MediaAsset>(cancellationToken: ct).ConfigureAwait(false);
            return asset ?? throw new LocalAiException("PaRuX Core returned no music.");
        }

        /// <summary>Writes the generated audio into PaRuX's managed audio folder and returns the path.</summary>
        public static async Task<string> SaveAsync(MediaAsset asset, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(asset.Base64Data)) throw new InvalidDataException("PaRuX Core returned no audio bytes.");
            byte[] bytes = Convert.FromBase64String(asset.Base64Data);
            AppPaths.EnsureCreated();
            StorageQuotaService.EnsureCapacity(bytes.LongLength);
            string extension = asset.MediaType switch { "audio/mpeg" => ".mp3", "audio/ogg" => ".ogg", "audio/wav" => ".wav", _ => ".flac" };
            string path = Path.Combine(AppPaths.GeneratedAudioDir, Guid.NewGuid().ToString("N") + extension);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            return path;
        }

        private static string ExtractError(string body)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? body;
            }
            catch (System.Text.Json.JsonException) { }
            return body;
        }
    }
}
