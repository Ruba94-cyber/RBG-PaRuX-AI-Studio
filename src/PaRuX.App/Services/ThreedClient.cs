using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class ThreedClient
    {
        public static ThreedClient Current { get; } = new ThreedClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(10));
        private ThreedClient() { }

        public async Task<ThreedEngineStatus> GetStatusAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/threed/engine");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new ThreedEngineStatus { Installed = false };
            return await response.Content.ReadFromJsonAsync<ThreedEngineStatus>(cancellationToken: ct).ConfigureAwait(false)
                ?? new ThreedEngineStatus { Installed = false };
        }

        public async Task<string> InstallEngineAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/threed/engine/install");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement error))
                        throw new LocalAiException(error.GetString() ?? "3D Studio install failed.");
                }
                catch (System.Text.Json.JsonException) { }
                throw new LocalAiException("3D Studio install failed (" + (int)response.StatusCode + ").");
            }
            return body;
        }

        public async Task<ThreedGenerateResponse> GenerateAsync(string sourceImagePath, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            byte[] bytes = await File.ReadAllBytesAsync(sourceImagePath, ct).ConfigureAwait(false);
            var payload = new ThreedGenerateRequest
            {
                SourceImageBase64 = Convert.ToBase64String(bytes),
                FileName = Path.GetFileName(sourceImagePath),
                Seed = -1
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/threed/generate");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(payload);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("3D generation failed (" + (int)response.StatusCode + "): " + detail);
            }
            ThreedGenerateResponse? result = await response.Content.ReadFromJsonAsync<ThreedGenerateResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null || string.IsNullOrEmpty(result.Base64Data)) throw new LocalAiException("PaRuX Core returned an empty 3D model.");

            // Preserve a real managed .glb behind the generated response so Artifact Workspace has
            // a durable file reference. The user-facing export flow remains independent.
            try
            {
                byte[] modelBytes = Convert.FromBase64String(result.Base64Data);
                AppPaths.EnsureCreated();
                StorageQuotaService.EnsureCapacity(modelBytes.LongLength);
                string managedPath = Path.Combine(AppPaths.GeneratedThreedDir, Guid.NewGuid().ToString("N") + ".glb");
                await File.WriteAllBytesAsync(managedPath, modelBytes, CancellationToken.None).ConfigureAwait(false);
                await ArtifactCaptureService.Current.CaptureFileAsync(
                    managedPath,
                    "3d",
                    string.IsNullOrWhiteSpace(result.FileName) ? "model.glb" : result.FileName,
                    "3d-generation",
                    new
                    {
                        sourceImage = Path.GetFileName(sourceImagePath),
                        mediaType = result.MediaType,
                        durationSeconds = result.DurationSeconds
                    },
                    markFinal: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("3D artifact capture skipped after successful generation: " + ex.Message);
            }

            return result;
        }
    }
}
