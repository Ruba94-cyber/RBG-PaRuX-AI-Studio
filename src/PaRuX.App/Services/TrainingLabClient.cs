using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// App-owned draft for the advanced Training Wizard controls. LifecycleLabView's historical
    /// StartTrainingAsync creates the common request shape; keeping the method-specific values here
    /// lets the partial wizard enrich that exact request at the single HTTP boundary instead of
    /// creating a second training submission path. The draft is process-local UI state only - Core
    /// remains authoritative and validates every field again.
    /// </summary>
    public sealed class TrainingInteractiveOptions
    {
        public string Method { get; init; } = TrainingMethod.Sft;
        public double LearningRate { get; init; } = 0.0002;
        public double DpoBeta { get; init; } = 0.1;
        public int MaxSequenceLength { get; init; } = 2048;
        public string GrpoRewardMode { get; init; } = PaRuX.Contracts.GrpoRewardMode.ExactReference;
        public int GrpoGenerations { get; init; } = 4;
        public string TextField { get; init; } = "text";
        public string ImageField { get; init; } = "image";
        public string AudioField { get; init; } = "audio";
    }

    public sealed class TrainingLabClient
    {
        public static TrainingLabClient Current { get; } = new TrainingLabClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromMinutes(30));
        private readonly object _interactiveGate = new();
        private TrainingInteractiveOptions? _interactiveOptions;
        private TrainingLabClient() { }

        public Task<TrainingEngineStatus> GetEngineStatusAsync(CancellationToken ct) =>
            SendAsync<TrainingEngineStatus>(HttpMethod.Get, "/api/v2/training/engine", null, ct);

        /// <summary>Installs PaRuX's pinned, backend-aware managed Training Lab runtime. The endpoint
        /// verifies the complete resolved environment before reporting readiness; the UI refreshes
        /// status afterwards instead of trusting installation exit code alone.</summary>
        public Task InstallEngineAsync(CancellationToken ct) =>
            SendNoContentOrJsonAsync(HttpMethod.Post, "/api/v2/training/engine/install", null, ct);

        /// <summary>Removes the managed training runtime and returns how much disk it gave back.
        /// Installing it costs roughly 5 GB, most of it torch; being able to undo that is the
        /// difference between an optional capability and a permanent one.</summary>
        public Task<TrainingEngineRemovalResult> UninstallEngineAsync(CancellationToken ct) =>
            SendAsync<TrainingEngineRemovalResult>(HttpMethod.Delete, "/api/v2/training/engine", null, ct);

        public Task<List<TrainingJobStatus>> ListJobsAsync(CancellationToken ct) =>
            SendAsync<List<TrainingJobStatus>>(HttpMethod.Get, "/api/v1/training/jobs", null, ct);

        public Task<TrainingJobStatus> GetJobAsync(string id, CancellationToken ct) =>
            SendAsync<TrainingJobStatus>(HttpMethod.Get, "/api/v1/training/jobs/" + Uri.EscapeDataString(id), null, ct);

        /// <summary>Checks a run before committing to it: whether the dataset is the shape the
        /// chosen method needs, whether the base model fits, and what settings suit this dataset.
        /// The same dataset/model planner is still authoritative when the v2 admission boundary
        /// accepts the actual job.</summary>
        public Task<TrainingPlanResult> PlanAsync(TrainingPlanRequest request, CancellationToken ct) =>
            SendAsync<TrainingPlanResult>(HttpMethod.Post, "/api/v1/training/plan", request, ct);

        /// <summary>Updates the currently visible wizard draft. This never starts a job and never
        /// persists credentials or paths; it only keeps method-specific controls attached to the
        /// common request object that the established Lifecycle Lab start button creates.</summary>
        public void SetInteractiveOptions(TrainingInteractiveOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            string method = TrainingMethod.Normalize(options.Method);
            var sanitized = new TrainingInteractiveOptions
            {
                Method = method,
                LearningRate = options.LearningRate,
                DpoBeta = options.DpoBeta,
                MaxSequenceLength = options.MaxSequenceLength,
                GrpoRewardMode = PaRuX.Contracts.GrpoRewardMode.Normalize(options.GrpoRewardMode),
                GrpoGenerations = options.GrpoGenerations,
                TextField = NormalizeField(options.TextField, "text"),
                ImageField = NormalizeField(options.ImageField, "image"),
                AudioField = NormalizeField(options.AudioField, "audio")
            };
            lock (_interactiveGate) _interactiveOptions = sanitized;
        }

        public Task<TrainingJobStatus> CreateJobAsync(TrainingJobRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            TrainingInteractiveOptions? options;
            lock (_interactiveGate) options = _interactiveOptions;
            // Only enrich the same method whose controls the user is looking at. A stale UI draft
            // can therefore never bleed into a job created after the method was changed elsewhere.
            if (options != null && string.Equals(
                    TrainingMethod.Normalize(request.Method), options.Method, StringComparison.Ordinal))
            {
                request.LearningRate = options.LearningRate;
                request.DpoBeta = options.DpoBeta;
                request.MaxSequenceLength = options.MaxSequenceLength;
                request.GrpoRewardMode = options.GrpoRewardMode;
                request.GrpoGenerations = options.GrpoGenerations;
                request.TextField = options.TextField;
                request.ImageField = options.ImageField;
                request.AudioField = options.AudioField;
            }
            return SendAsync<TrainingJobStatus>(HttpMethod.Post, "/api/v2/training/jobs", request, ct);
        }

        public Task CancelJobAsync(string id, CancellationToken ct) =>
            SendNoContentOrJsonAsync(HttpMethod.Post,
                "/api/v1/training/jobs/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        private static string NormalizeField(string? value, string fallback)
        {
            string field = (value ?? string.Empty).Trim();
            return field.Length == 0 ? fallback : field;
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Training Lab request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Training Lab response.");
            return value;
        }

        private static async Task SendNoContentOrJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Training Lab request failed (" + (int)response.StatusCode + "): " + detail);
            }
        }
    }
}
