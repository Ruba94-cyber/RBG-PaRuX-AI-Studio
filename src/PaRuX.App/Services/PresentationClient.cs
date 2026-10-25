using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class PresentationClient
    {
        public static PresentationClient Current { get; } = new PresentationClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(6));
        private PresentationClient() { }

        public async Task<PresentationResponse> GenerateAsync(string topic, int slideCount, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + "/api/v1/presentations");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(new PresentationRequest { Topic = topic, SlideCount = slideCount });
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Presentation request failed (" + (int)response.StatusCode + "): " + detail);
            }
            PresentationResponse? result = await response.Content.ReadFromJsonAsync<PresentationResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null || string.IsNullOrEmpty(result.Base64Data)) throw new LocalAiException("PaRuX Core returned an empty presentation.");

            // Keep a real, PaRuX-managed file behind every generated presentation so Artifact
            // Workspace references a durable path rather than transient base64. The Settings
            // page's Save button remains an export/copy to the user's chosen destination.
            // This bookkeeping is best-effort and must never turn a valid generated PPTX response
            // into a failed generation if local catalog storage happens to be unavailable.
            try
            {
                byte[] bytes = Convert.FromBase64String(result.Base64Data);
                AppPaths.EnsureCreated();
                StorageQuotaService.EnsureCapacity(bytes.LongLength);
                string managedPath = Path.Combine(
                    AppPaths.GeneratedPresentationsDir,
                    Guid.NewGuid().ToString("N") + ".pptx");
                await File.WriteAllBytesAsync(managedPath, bytes, CancellationToken.None).ConfigureAwait(false);

                await ArtifactCaptureService.Current.CaptureFileAsync(
                    managedPath,
                    "presentation",
                    string.IsNullOrWhiteSpace(result.FileName) ? "presentation.pptx" : result.FileName,
                    "presentation-generation",
                    new { topic, slideCount, mediaType = "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
                    markFinal: true,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Write("Presentation artifact capture skipped after successful generation: " + ex.Message);
            }

            return result;
        }
    }
}
