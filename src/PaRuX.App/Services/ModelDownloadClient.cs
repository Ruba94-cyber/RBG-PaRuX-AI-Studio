using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record ManagedModelDeleteOutcome(
        bool Deleted, string Message, IReadOnlyList<string> TextRolesToReset, bool OtherBlockers);

    /// <summary>
    /// The first-class Model Hub client. Text GGUF downloads and compatible media checkpoints use
    /// the same PaRuX Core download queue/budget/integrity boundary; activation remains explicit
    /// because text models and ComfyUI diffusion checkpoints have different runtime destinations.
    /// Stable download-source metadata is journaled before a transfer starts so preserved .partial
    /// bytes remain resumable even if PaRuX Core itself restarts.
    /// </summary>
    public sealed class ModelDownloadClient
    {
        public static ModelDownloadClient Current { get; } = new ModelDownloadClient();
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(30));
        private ModelDownloadClient() { }

        /// <summary>Replaces only the mutable resolve-ref segment of a Hugging Face file URL with
        /// the exact immutable commit inspected by the terms/repository-details flow.</summary>
        public static void PinRequestToRevision(ModelDownloadRequest request, string revision)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string? normalizedRevision = ModelRevisionIdentity.Normalize(revision);
            if (normalizedRevision == null)
                throw new InvalidDataException("The repository did not provide a valid immutable commit SHA.");
            if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase)
                || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("The selected file is not a Hugging Face HTTPS resolve URL.");

            string[] pathSegments = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length < 5
                || !string.Equals(Uri.UnescapeDataString(pathSegments[0]) + "/" + Uri.UnescapeDataString(pathSegments[1]),
                    request.RepoId, StringComparison.Ordinal)
                || !string.Equals(pathSegments[2], "resolve", StringComparison.Ordinal)
                || !string.Equals(Uri.UnescapeDataString(pathSegments[^1]), request.FileName, StringComparison.Ordinal))
                throw new InvalidDataException("The selected file URL does not match its repository and file name.");

            pathSegments[3] = normalizedRevision;
            string pinnedUrl = uri.GetLeftPart(UriPartial.Authority) + "/" + string.Join('/', pathSegments) + uri.Query;
            request.Revision = normalizedRevision;
            request.DownloadUrl = pinnedUrl;
        }

        public Task<List<ModelSearchResult>> SearchAsync(string query, int limit, CancellationToken ct) =>
            SendAsync<List<ModelSearchResult>>(HttpMethod.Get,
                "/api/v1/models/search?query=" + Uri.EscapeDataString(query) + "&limit=" + limit, null, ct);

        public Task<AiStorageBudgetStatus> GetBudgetAsync(CancellationToken ct) =>
            SendAsync<AiStorageBudgetStatus>(HttpMethod.Get, "/api/v1/models/budget", null, ct);

        public Task<ModelRepoDetails> GetDetailsAsync(string repoId, CancellationToken ct) =>
            SendAsync<ModelRepoDetails>(HttpMethod.Get,
                "/api/v1/models/details?repoId=" + Uri.EscapeDataString(repoId), null, ct);

        public Task<ModelDownloadPlan> GetDownloadPlanAsync(
            string repoId, string? quantization, string setName, string? directory, int partCount, CancellationToken ct) =>
            SendAsync<ModelDownloadPlan>(HttpMethod.Get,
                "/api/v1/models/download-plan?repoId=" + Uri.EscapeDataString(repoId)
                + (string.IsNullOrWhiteSpace(quantization) ? string.Empty : "&quantization=" + Uri.EscapeDataString(quantization))
                + "&setName=" + Uri.EscapeDataString(setName)
                + (directory == null ? string.Empty : "&directory=" + Uri.EscapeDataString(directory))
                + "&partCount=" + partCount, null, ct);

        public Task<List<ModelDownloadStatus>> StartDownloadPlanAsync(ModelDownloadPlanRequest request, CancellationToken ct) =>
            SendAsync<List<ModelDownloadStatus>>(HttpMethod.Post, "/api/v1/models/download-plan/start", request, ct);

        /// <summary>Curated "which text model should I get" picks.</summary>
        public Task<List<RecommendedModel>> GetRecommendedAsync(CancellationToken ct) =>
            SendAsync<List<RecommendedModel>>(HttpMethod.Get, "/api/v1/models/recommended", null, ct);

        public Task<ModelDiscoveryCatalog> GetDiscoveryCatalogAsync(CancellationToken ct) =>
            SendAsync<ModelDiscoveryCatalog>(HttpMethod.Get, "/api/v1/models/discovery", null, ct);

        /// <summary>Refreshes public model metadata only after an explicit user action.</summary>
        public Task<ModelDiscoveryRefreshResult> RefreshDiscoveryAsync(CancellationToken ct) =>
            SendAsync<ModelDiscoveryRefreshResult>(HttpMethod.Post, "/api/v1/models/discovery/refresh", new { }, ct);

        /// <summary>Checks only the named public repositories. A cooldown or fetch failure is a
        /// typed outcome from Core, so the view can preserve its existing catalog.</summary>
        public async Task<ModelDiscoveryRefreshResult> RefreshExplicitDiscoveryAsync(
            ModelDiscoveryExplicitRefreshRequest input, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct)
                .ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                server.Endpoint.TrimEnd('/') + "/api/v1/models/discovery/refresh-explicit");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(input);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK
                && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests
                && response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                throw new LocalAiException("Specific repository check was rejected (" +
                    (int)response.StatusCode + ").");
            ModelDiscoveryRefreshResult? result = await response.Content
                .ReadFromJsonAsync<ModelDiscoveryRefreshResult>(cancellationToken: ct).ConfigureAwait(false);
            if (result == null || result.Outcome is not ("refreshed" or "cooldown" or "failed"))
                throw new LocalAiException("Core returned an invalid repository-check outcome.");
            return result;
        }

        /// <summary>Persist the user's explicit decision to hide one exact Candidate identity.</summary>
        public Task IgnoreCandidateAsync(string candidateKey, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/models/discovery/" + Uri.EscapeDataString(candidateKey) + "/ignore", null, ct);

        /// <summary>Defer the grouped discovery notice for one exact Candidate revision.</summary>
        public Task DeferCandidateNoticeAsync(string candidateKey, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/models/discovery/" + Uri.EscapeDataString(candidateKey) + "/notice/later", null, ct);

        /// <summary>Dismiss a notice only after its requested local evaluation was accepted.</summary>
        public Task MarkCandidateNoticeHandledAsync(string candidateKey, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/models/discovery/" + Uri.EscapeDataString(candidateKey) + "/notice/handled", null, ct);

        public async Task<CandidateEvaluationJobAccepted> StartCandidateEvaluationJobAsync(
            string candidateKey, string managedFileName, CancellationToken ct,
            string? qualityProfileId = null)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                server.Endpoint.TrimEnd('/') + "/api/v1/models/discovery/" + Uri.EscapeDataString(candidateKey) + "/evaluation-jobs");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            request.Content = JsonContent.Create(new StartCandidateEvaluationJobRequest
            {
                ManagedFileName = managedFileName,
                QualityProfileId = qualityProfileId
            });
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict
                    && TryGetCandidateEvaluationRejectionCode(detail) is { } errorCode)
                    throw new CandidateEvaluationStartRejectedException(errorCode);
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }

            CandidateEvaluationJobAccepted? accepted = await response.Content
                .ReadFromJsonAsync<CandidateEvaluationJobAccepted>(cancellationToken: ct).ConfigureAwait(false);
            return accepted ?? throw new LocalAiException("PaRuX Core returned an empty Candidate evaluation response.");
        }

        private static string? TryGetCandidateEvaluationRejectionCode(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("error", out JsonElement error)
                    || error.ValueKind != JsonValueKind.String) return null;
                string? value = error.GetString();
                return value is "candidate_license_evidence_missing" or "candidate_license_metadata_changed"
                    or "candidate_license_review_required" or "candidate_provenance_unavailable"
                    ? value : null;
            }
            catch (JsonException) { return null; }
        }

        public async Task<List<CandidateEvaluationJobStatus>> ListCandidateEvaluationJobsAsync(CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                server.Endpoint.TrimEnd('/') + "/api/v1/models/evaluation-jobs");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
            List<CandidateEvaluationJobStatus>? value = await response.Content
                .ReadFromJsonAsync<List<CandidateEvaluationJobStatus>>(cancellationToken: ct).ConfigureAwait(false);
            return value ?? throw new LocalAiException("PaRuX Core returned an empty evaluation-job history response.");
        }

        /// <summary>Reads explicit Candidate utility policy readiness; no policy is synthesized.</summary>
        public Task<CandidateUtilityPolicyStatus> GetCandidateUtilityPolicyAsync(CancellationToken ct) =>
            SendAsync<CandidateUtilityPolicyStatus>(HttpMethod.Get,
                "/api/v1/models/candidate-utility/policy", null, ct);

        /// <summary>Reads the exact Core-owned scope for explicit Candidate comparison criteria.</summary>
        public Task<CandidateUtilityPolicyCalibrationContext> GetCandidateUtilityPolicyContextAsync(
            CancellationToken ct, string? qualityProfileId = null) =>
            SendAsync<CandidateUtilityPolicyCalibrationContext>(HttpMethod.Get,
                "/api/v1/models/candidate-utility/policy/context"
                    + (qualityProfileId is null ? string.Empty : "?qualityProfileId=" + Uri.EscapeDataString(qualityProfileId)), null, ct);

        /// <summary>Saves only explicit criteria against the exact context most recently shown to the user.</summary>
        public Task<CandidateUtilityPolicyStatus> ConfigureCandidateUtilityPolicyAsync(
            CandidateUtilityPolicyConfigurationRequest request, CancellationToken ct) =>
            SendAsync<CandidateUtilityPolicyStatus>(HttpMethod.Put,
                "/api/v1/models/candidate-utility/policy/configure", request, ct);

        /// <summary>Explicitly removes the persisted comparison criteria; it creates no replacement defaults.</summary>
        public Task<CandidateUtilityPolicyStatus> ResetCandidateUtilityPolicyAsync(CancellationToken ct) =>
            SendAsync<CandidateUtilityPolicyStatus>(HttpMethod.Delete,
                "/api/v1/models/candidate-utility/policy", null, ct);

        /// <summary>Requests an explicit, read-only comparison of stored evidence. Core remains the
        /// authority for exact provenance, evidence scope, policy and active-reference checks.</summary>
        public Task<CandidateUtilityComparisonResponse> CompareCandidateUtilityAsync(
            CandidateUtilityComparisonRequest request, CancellationToken ct) =>
            SendAsync<CandidateUtilityComparisonResponse>(HttpMethod.Post,
                "/api/v1/models/candidate-utility/compare", request, ct);

        /// <summary>Read-only status of the scoped recommendation; reading it changes nothing.</summary>
        public Task<CandidateRecommendationStatusResponse> GetCandidateRecommendationStatusAsync(CancellationToken ct) =>
            SendAsync<CandidateRecommendationStatusResponse>(HttpMethod.Get,
                "/api/v1/models/candidate-recommendation/status", null, ct);

        /// <summary>Explicit, confirmed promotion. Core's typed outcome is returned for 200/400/409/503 alike so the
        /// UI can show its plain-language reason instead of a transport error.</summary>
        public Task<CandidateRecommendationTransitionResponse> PromoteCandidateRecommendationAsync(
            PromoteCandidateRecommendationRequest request, CancellationToken ct) =>
            SendRecommendationTransitionAsync("/api/v1/models/candidate-recommendation/promote", request, ct);

        public Task<CandidateRecommendationTransitionResponse> RollbackCandidateRecommendationAsync(
            RollbackCandidateRecommendationRequest request, CancellationToken ct) =>
            SendRecommendationTransitionAsync("/api/v1/models/candidate-recommendation/rollback", request, ct);

        private static async Task<CandidateRecommendationTransitionResponse> SendRecommendationTransitionAsync(
            string path, object body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.TrimEnd('/') + path)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            CandidateRecommendationTransitionResponse? typed = null;
            try { typed = await response.Content.ReadFromJsonAsync<CandidateRecommendationTransitionResponse>(cancellationToken: ct).ConfigureAwait(false); }
            catch (System.Text.Json.JsonException) { }
            if (typed is null || string.IsNullOrEmpty(typed.ReasonCode))
                throw new LocalAiException("PaRuX Core returned an unexpected recommendation response (" + (int)response.StatusCode + ").");
            return typed;
        }

        public async Task<CandidateEvaluationJobStatus?> GetCandidateEvaluationJobAsync(
            string jobId, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                server.Endpoint.TrimEnd('/') + "/api/v1/models/evaluation-jobs/" + Uri.EscapeDataString(jobId));
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
            CandidateEvaluationJobStatus? value = await response.Content
                .ReadFromJsonAsync<CandidateEvaluationJobStatus>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty evaluation-job response.");
            return value;
        }

        public async Task<CandidateEvaluationJobStatus?> CancelCandidateEvaluationJobAsync(
            string jobId, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                server.Endpoint.TrimEnd('/') + "/api/v1/models/evaluation-jobs/" + Uri.EscapeDataString(jobId) + "/cancel");
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
            CandidateEvaluationJobStatus? value = await response.Content
                .ReadFromJsonAsync<CandidateEvaluationJobStatus>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty cancellation response.");
            return value;
        }

        /// <summary>The same picks, ranked against this machine's GPU and memory, with a sentence
        /// per model explaining the verdict. This is what first-run shows instead of a search box.</summary>
        public Task<FirstModelAdvice> GetAdviceForThisPcAsync(CancellationToken ct) =>
            SendAsync<FirstModelAdvice>(HttpMethod.Get, "/api/v1/models/recommended/for-this-pc", null, ct);

        /// <summary>Automatically assigns all system roles (standard, coding, reasoning, fast, etc.)
        /// using locally available models matching machine capabilities and memory.</summary>
        public Task<RoleAutoAssignResponse> AutoAssignRolesAsync(CancellationToken ct = default) =>
            SendAsync<RoleAutoAssignResponse>(HttpMethod.Post, "/api/v1/models/roles/auto-assign", null, ct);

        /// <summary>Current Image/Video/Edit diffusion checkpoint assignments.</summary>
        public Task<List<MediaCheckpointStatus>> ListMediaCheckpointsAsync(CancellationToken ct) =>
            SendAsync<List<MediaCheckpointStatus>>(HttpMethod.Get, "/api/v1/media/checkpoints", null, ct);

        /// <summary>Only same-family media checkpoints verified compatible with PaRuX's existing
        /// ComfyUI workflow text-encoder/VAE pairing are exposed here.</summary>
        public Task<List<RecommendedCheckpoint>> GetRecommendedMediaCheckpointsAsync(CancellationToken ct) =>
            SendAsync<List<RecommendedCheckpoint>>(HttpMethod.Get, "/api/v1/media/checkpoints/recommended", null, ct);

        /// <summary>Moves one already-downloaded compatible checkpoint into ComfyUI's managed
        /// diffusion_models folder and assigns it to Image/Video/Edit. Core performs all path and
        /// type validation; the client never sends a filesystem path.</summary>
        public Task<MediaCheckpointStatus> ActivateMediaCheckpointAsync(
            RecommendedCheckpoint checkpoint, CancellationToken ct) =>
            SendAsync<MediaCheckpointStatus>(HttpMethod.Post, "/api/v1/media/checkpoints/activate",
                new ActivateCheckpointRequest { Type = checkpoint.Type, FileName = checkpoint.FileName }, ct);

        public async Task<List<ModelDownloadStatus>> ListDownloadsAsync(CancellationToken ct)
        {
            List<ModelDownloadStatus> jobs = await SendAsync<List<ModelDownloadStatus>>(
                HttpMethod.Get, "/api/v1/models/downloads", null, ct).ConfigureAwait(false);
            jobs.AddRange(ModelDownloadResumeCatalog.Current.ListResumable(jobs));
            return jobs.OrderByDescending(j => j.CreatedUtc).ToList();
        }

        public Task<ModelDownloadTransferSettings> GetTransferSettingsAsync(CancellationToken ct) =>
            SendAsync<ModelDownloadTransferSettings>(HttpMethod.Get, "/api/v1/models/download-settings", null, ct);

        public Task SaveTransferSettingsAsync(ModelDownloadTransferSettings settings, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Put, "/api/v1/models/download-settings", settings, ct);

        public Task<ModelDownloadCredentialStatus> GetHubCredentialStatusAsync(CancellationToken ct) =>
            SendAsync<ModelDownloadCredentialStatus>(HttpMethod.Get, "/api/v1/models/download-credentials", null, ct);

        public Task SaveHubCredentialAsync(string? token, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Put, "/api/v1/models/download-credentials",
                new ModelDownloadCredentialUpdate { Token = token }, ct);

        public Task ClearHubCredentialAsync(CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/models/download-credentials", null, ct);

        public Task<List<ManagedModelInfo>> ListManagedModelsAsync(CancellationToken ct) =>
            SendAsync<List<ManagedModelInfo>>(HttpMethod.Get, "/api/v1/models/library", null, ct);

        public Task<List<ExternalModelCandidate>> ListExternalModelsAsync(CancellationToken ct) =>
            SendAsync<List<ExternalModelCandidate>>(HttpMethod.Get, "/api/v1/models/library/external", null, ct);

        /// <summary>Split sets in the managed library that are still missing parts. They are not models: each one
        /// carries the parts it lacks and cannot be loaded.</summary>
        public Task<List<ManagedModelInfo>> ListIncompleteSplitSetsAsync(CancellationToken ct) =>
            SendAsync<List<ManagedModelInfo>>(HttpMethod.Get, "/api/v1/models/library/split-sets", null, ct);

        /// <summary>Guarded delete: a 409 means PaRuX refused (assigned / loaded / linked / user-managed) and the outcome says why.
        /// TextRolesToReset lists the text roles to switch to Automatic first (empty when the refusal is not about a role).</summary>
        public async Task<ManagedModelDeleteOutcome> DeleteManagedModelCheckedAsync(string fileName, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                server.Endpoint.TrimEnd('/') + "/api/v1/models/library/" + Uri.EscapeDataString(fileName));
            request.Headers.Add("X-PaRuX-Token", server.Token);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new ManagedModelDeleteOutcome(true, string.Empty, Array.Empty<string>(), false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string message = body;
            var roles = new List<string>();
            bool otherBlockers = false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err)) message = err.GetString() ?? body;
                if (doc.RootElement.TryGetProperty("blockers", out var blockers))
                    foreach (var b in blockers.EnumerateArray())
                    {
                        string kind = b.TryGetProperty("kind", out var k) ? k.GetString() ?? string.Empty : string.Empty;
                        if (kind == "assigned-role" && b.TryGetProperty("role", out var r) && r.GetString() is { } role) roles.Add(role);
                        else otherBlockers = true;
                    }
            }
            catch (System.Text.Json.JsonException) { }
            if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + body);
            return new ManagedModelDeleteOutcome(false, message, roles, otherBlockers);
        }

        public Task DeleteManagedModelAsync(string fileName, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Delete, "/api/v1/models/library/" + Uri.EscapeDataString(fileName), null, ct);

        public Task<ManagedModelInfo> ImportManagedModelAsync(ImportManagedModelRequest request, CancellationToken ct) =>
            SendAsync<ManagedModelInfo>(HttpMethod.Post, "/api/v1/models/library/import", request, ct);

        public Task<ManagedModelInfo> ImportExternalModelAsync(ExternalModelImportRequest request, CancellationToken ct) =>
            SendAsync<ManagedModelInfo>(HttpMethod.Post, "/api/v1/models/library/import-external", request, ct);

        public Task<ManagedModelInfo> SetAliasAsync(string fileName, string alias, CancellationToken ct) =>
            SendAsync<ManagedModelInfo>(HttpMethod.Post, "/api/v1/models/library/" + Uri.EscapeDataString(fileName) + "/alias",
                new SetManagedModelAliasRequest { Alias = alias }, ct);

        public async Task<ModelDownloadStatus> StartDownloadAsync(ModelDownloadRequest request, CancellationToken ct)
        {
            if (ModelDownloadResumeCatalog.IsSyntheticId(request.ResumeFromDownloadId))
            {
                if (!ModelDownloadResumeCatalog.Current.TryResolve(request.ResumeFromDownloadId!, out ModelDownloadRequest resumed))
                    throw new LocalAiException("The persisted partial download is no longer available to resume.");
                request = resumed;
            }
            else if (string.IsNullOrWhiteSpace(request.ResumeFromDownloadId))
            {
                // Write before contacting the Core: even a process/Core crash after the first byte
                // must leave enough stable source identity to resume those bytes later.
                ModelDownloadResumeCatalog.Current.Remember(request);
            }

            return await SendAsync<ModelDownloadStatus>(
                HttpMethod.Post, "/api/v1/models/downloads", request, ct).ConfigureAwait(false);
        }

        /// <summary>Starts an explicit, pinned Candidate transfer. The Core resolves file identity
        /// and size from its validated discovery catalog; this client sends no transfer URL, path,
        /// revision or publisher-declared size.</summary>
        public Task<ModelDownloadStatus> StartCandidateDownloadAsync(
            string candidateKey, ModelDownloadLicenseEvidence licenseEvidence, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(candidateKey)) throw new ArgumentException("A Candidate key is required.", nameof(candidateKey));
            if (licenseEvidence == null) throw new ArgumentNullException(nameof(licenseEvidence));
            return SendAsync<ModelDownloadStatus>(HttpMethod.Post,
                "/api/v1/models/discovery/" + Uri.EscapeDataString(candidateKey) + "/downloads",
                new StartCandidateDownloadRequest { LicenseEvidence = licenseEvidence }, ct);
        }

        public Task CancelDownloadAsync(string id, CancellationToken ct)
        {
            // Synthetic rows represent a transfer that already ended with the previous Core
            // process; there is no live server job to cancel. Model Hub only presents Resume for
            // these rows, but keeping this guard makes the client contract robust to future UI use.
            if (ModelDownloadResumeCatalog.IsSyntheticId(id)) return Task.CompletedTask;
            return SendNoContentAsync(HttpMethod.Post,
                "/api/v1/models/downloads/" + Uri.EscapeDataString(id) + "/cancel", null, ct);
        }

        public Task PauseDownloadAsync(string id, CancellationToken ct)
        {
            if (ModelDownloadResumeCatalog.IsSyntheticId(id)) return Task.CompletedTask;
            return SendNoContentAsync(HttpMethod.Post,
                "/api/v1/models/downloads/" + Uri.EscapeDataString(id) + "/pause", null, ct);
        }

        /// <summary>
        /// Pause/Resume alone left no way to actually discard a paused or failed download - a real
        /// gap behind the "can't even cancel a download" report, since the job (and its .partial
        /// bytes) would otherwise sit there forever with only Resume offered. Synthetic rows (a
        /// resumable transfer persisted across a Core restart, see ModelDownloadResumeCatalog) have
        /// no live server job to delete; Model Hub only offers Remove for real, live job rows, so
        /// this mirrors CancelDownloadAsync's guard for contract robustness rather than a case it
        /// expects to hit today.
        /// </summary>
        public Task RemoveDownloadAsync(string id, CancellationToken ct)
        {
            if (ModelDownloadResumeCatalog.IsSyntheticId(id)) return Task.CompletedTask;
            return SendNoContentAsync(HttpMethod.Delete,
                "/api/v1/models/downloads/" + Uri.EscapeDataString(id), null, ct);
        }

        public Task<List<ModelBenchmarkSummary>> ListBenchmarkModelsAsync(CancellationToken ct) =>
            SendAsync<List<ModelBenchmarkSummary>>(HttpMethod.Get, "/api/v1/models/benchmarks/models", null, ct);

        public Task<ModelBenchmarkRun> StartBenchmarkAsync(ModelBenchmarkRequest request, CancellationToken ct) =>
            SendAsync<ModelBenchmarkRun>(HttpMethod.Post, "/api/v1/models/benchmarks", request, ct);

        public Task<ModelBenchmarkRun> GetBenchmarkAsync(string id, CancellationToken ct) =>
            SendAsync<ModelBenchmarkRun>(HttpMethod.Get,
                "/api/v1/models/benchmarks/" + Uri.EscapeDataString(id), null, ct);

        public Task CancelBenchmarkAsync(string id, CancellationToken ct) =>
            SendNoContentAsync(HttpMethod.Post,
                "/api/v1/models/benchmarks/" + Uri.EscapeDataString(id) + "/cancel", null, ct);

        private static async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
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
                throw new LocalAiException("Model Hub request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty Model Hub response.");
            return value;
        }
    }
}
