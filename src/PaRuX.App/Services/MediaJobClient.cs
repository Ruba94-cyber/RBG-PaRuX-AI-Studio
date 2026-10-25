using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class MediaJobClient
    {
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(Timeout.InfiniteTimeSpan);
        public static MediaJobClient Current { get; } = new MediaJobClient();
        private MediaJobClient() { }

        public Task<MediaAsset> RunAsync(MediaJobRequest request, IProgress<MediaJobStatus> progress, CancellationToken ct)
            => RunAsync(request, progress, ct, null);

        /// <summary>Re-attaches to a job that already exists on the server (for example after an app restart) and polls it to its end.</summary>
        public Task<MediaAsset> AttachAsync(string jobId, IProgress<MediaJobStatus> progress, CancellationToken ct)
            => RunAsync(null, progress, ct, null, jobId);

        /// <summary>Asks the server about one job. Job is null when the server answered 404; Reachable is false when it could not be asked.</summary>
        public async Task<(MediaJobStatus? Job, bool Reachable)> TryGetStatusAsync(string jobId, CancellationToken ct)
        {
            try
            {
                ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
                using var poll = new HttpRequestMessage(HttpMethod.Get, server.Endpoint.TrimEnd('/') + "/api/v1/jobs/" + Uri.EscapeDataString(jobId));
                poll.Headers.Add("X-PaRuX-Token", server.Token);
                using HttpResponseMessage response = await Http.SendAsync(poll, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, true);
                if (!response.IsSuccessStatusCode) return (null, false);
                MediaJobStatus? status = await response.Content.ReadFromJsonAsync<MediaJobStatus>(cancellationToken: ct).ConfigureAwait(false);
                return status == null ? (null, false) : (status, true);
            }
            catch (OperationCanceledException) { throw; }
            catch { return (null, false); }
        }

        public async Task<MediaAsset> RunAsync(MediaJobRequest? request, IProgress<MediaJobStatus> progress, CancellationToken ct,
            Action<string>? onJobCreated, string? existingJobId = null)
        {
            ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
            string root = server.Endpoint.TrimEnd('/');
            string? jobId = existingJobId;
            try
            {
                if (jobId == null)
                {
                using var create = new HttpRequestMessage(HttpMethod.Post, root + "/api/v1/jobs");
                create.Headers.Add("X-PaRuX-Token", server.Token);
                create.Content = JsonContent.Create(request);
                using HttpResponseMessage accepted = await Http.SendAsync(create, ct).ConfigureAwait(false);
                MediaJobStatus? created = await accepted.Content.ReadFromJsonAsync<MediaJobStatus>(cancellationToken: ct).ConfigureAwait(false);
                if (!accepted.IsSuccessStatusCode || created == null) throw new LocalAiException("The media job could not be created.");
                jobId = created.Id;
                }
                onJobCreated?.Invoke(jobId);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    using var poll = new HttpRequestMessage(HttpMethod.Get, root + "/api/v1/jobs/" + jobId);
                    poll.Headers.Add("X-PaRuX-Token", server.Token);
                    using HttpResponseMessage response = await Http.SendAsync(poll, ct).ConfigureAwait(false);
                    MediaJobStatus? status = await response.Content.ReadFromJsonAsync<MediaJobStatus>(cancellationToken: ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || status == null) throw new LocalAiException("The media job disappeared.");
                    progress?.Report(status);
                    if (status.Status == "completed") return status.Asset ?? throw new LocalAiException("The media job returned no file.");
                    if (status.Status == "failed")
                    {
                        if (status.ErrorDetail != null) throw new MediaDependencyJobException(status.ErrorDetail);
                        throw new LocalAiException(status.Error ?? "Media generation failed.");
                    }
                    if (status.Status == "cancelled") throw new OperationCanceledException(ct);
                    // 400ms rather than 1s: a fast generation can finish its whole sampling pass in
                    // just a few seconds, and a 1s poll only samples that a handful of times -
                    // real, moving server-side progress (see ComfyWorkflowRunner's websocket
                    // listener) still looked like it jumped in big steps just from being polled too
                    // coarsely. Still cheap: this is a local HTTP round trip against 127.0.0.1.
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrWhiteSpace(jobId))
                {
                    try
                    {
                        using var cancel = new HttpRequestMessage(HttpMethod.Post, root + "/api/v1/jobs/" + jobId + "/cancel");
                        cancel.Headers.Add("X-PaRuX-Token", server.Token);
                        using HttpResponseMessage ignored = await Http.SendAsync(cancel, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                }
                throw;
            }
        }

        public async Task<MediaCompanionPlan> GetCompanionPlanAsync(string type, CancellationToken ct)
        {
            ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
            string root = server.Endpoint.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Get,
                root + "/api/v1/media/companions?type=" + Uri.EscapeDataString(type));
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            MediaCompanionPlan? plan = await response.Content.ReadFromJsonAsync<MediaCompanionPlan>(cancellationToken: ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || plan == null)
                throw new LocalAiException("The media companion plan could not be loaded (HTTP " + (int)response.StatusCode + ").");
            return plan;
        }

        public async Task<MediaCompanionFetchResult> FetchCompanionsAsync(MediaCompanionPlan plan, CancellationToken ct)
        {
            ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
            string root = server.Endpoint.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, root + "/api/v1/media/companions/fetch");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(MediaCompanionRemediationText.CreateFetchRequest(plan));
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            MediaCompanionFetchResult? result = await response.Content.ReadFromJsonAsync<MediaCompanionFetchResult>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null || (!response.IsSuccessStatusCode && result.Ok))
                throw new LocalAiException("The media companion fetch returned an unreadable or inconsistent response.");
            return result;
        }
    }
}
