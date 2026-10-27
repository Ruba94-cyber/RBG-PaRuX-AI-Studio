using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Local background removal through PaRuX Core (BiRefNet-lite ONNX in a managed Python runtime). Returns a transparent PNG.</summary>
    public sealed class CutoutClient
    {
        public static CutoutClient Current { get; } = new CutoutClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(Timeout.InfiniteTimeSpan);
        private CutoutClient() { }

        public async Task<CutoutEngineStatus> GetStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/cutout/engine");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new CutoutEngineStatus { Installed = false };
            return await response.Content.ReadFromJsonAsync<CutoutEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new CutoutEngineStatus { Installed = false };
        }

        /// <summary>Removes the background of the image at <paramref name="sourcePath"/> and returns the transparent PNG as a MediaAsset.</summary>
        public async Task<MediaAsset> RemoveAsync(string sourcePath, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            byte[] bytes = await File.ReadAllBytesAsync(sourcePath, ct).ConfigureAwait(false);
            using var message = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/cutout/remove")
            {
                Content = JsonContent.Create(new CutoutRemoveRequest
                {
                    ImageBase64 = Convert.ToBase64String(bytes),
                    FileName = Path.GetFileName(sourcePath)
                })
            };
            message.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(message, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Background removal failed (" + (int)response.StatusCode + "): " + ExtractError(detail));
            }
            MediaAsset? asset = await response.Content.ReadFromJsonAsync<MediaAsset>(cancellationToken: ct).ConfigureAwait(false);
            if (asset == null || string.IsNullOrEmpty(asset.Base64Data)) throw new LocalAiException("PaRuX Core returned no cutout image.");
            return asset;
        }

        /// <summary>Writes the cutout into PaRuX's managed images folder (or <paramref name="folder"/>) as a PNG and returns the path.</summary>
        public static async Task<string> SaveAsync(MediaAsset asset, string? folder, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(asset.Base64Data)) throw new InvalidDataException("PaRuX Core returned no image bytes.");
            byte[] bytes = Convert.FromBase64String(asset.Base64Data);
            AppPaths.EnsureCreated();
            string directory = string.IsNullOrWhiteSpace(folder) ? AppPaths.GeneratedImagesDir : folder;
            if (StorageQuotaService.IsManagedPath(directory)) StorageQuotaService.EnsureCapacity(bytes.LongLength);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
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
