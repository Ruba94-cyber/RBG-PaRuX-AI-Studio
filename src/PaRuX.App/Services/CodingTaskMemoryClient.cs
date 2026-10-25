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
    /// Durable, task-scoped memory for the coding agent, on top of Core's Virtual Context ledger.
    ///
    /// The agent's prompt can only replay a bounded slice of its own execution evidence, so
    /// everything older falls out of the window and it re-reads what it already knows. Virtual
    /// Context exists precisely to carry durable facts past the physical window, but
    /// <c>ContextEngineService</c> only ever built it for scope "conversation" - the coding agent
    /// was never wired to it and ran on the raw physical window alone.
    ///
    /// Measured on the physical PC (2026-09-21): across four real runs against an external .NET
    /// repository the agent re-read the same 154-line file until its repeat guard and exploration
    /// budget stopped it, producing no edit. Raising the physical window from 8,192 to a verified
    /// 32,768 tokens did not break the loop, because recall - not window size - was the binding
    /// constraint.
    ///
    /// This client gives each coding task its own ledger scope, so what the agent learned in step
    /// 3 is still in front of it at step 30. Memory is best-effort by design: a ledger failure
    /// degrades recall, and must never fail the task or mask a real coding error.
    /// </summary>
    public sealed class CodingTaskMemoryClient
    {
        public static CodingTaskMemoryClient Current { get; } = new CodingTaskMemoryClient();

        /// <summary>Ledger scope token. Lowercase and short - the store normalizes to 40 chars.</summary>
        public const string Scope = "coding-task";

        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(20));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        /// <summary>One ledger entry is a durable fact, not a transcript dump.</summary>
        internal const int MaxEntryCharacters = 4_000;

        private CodingTaskMemoryClient() { }

        /// <summary>
        /// Records one durable fact for this task. Returns false when the fact could not be stored;
        /// callers treat that as reduced recall, never as a task failure.
        /// </summary>
        public async Task<bool> RememberAsync(
            string taskId, string kind, string text, string source, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(text)) return false;

            var body = new AppendVirtualContextLedgerRequest
            {
                Scope = Scope,
                ScopeId = taskId,
                Kind = string.IsNullOrWhiteSpace(kind) ? VirtualContextLedgerKind.Note : kind,
                Text = Truncate(text, MaxEntryCharacters),
                Source = string.IsNullOrWhiteSpace(source) ? "coding-agent" : source
            };

            try
            {
                await SendAsync<VirtualContextLedgerEntryDto>(
                    HttpMethod.Post, "/api/v2/virtual-context/ledger", body, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }

        /// <summary>
        /// The durable memory to put in front of the model, already rendered by the ledger, or an
        /// empty string when there is nothing yet or Core could not be reached.
        /// </summary>
        public async Task<string> RecallAsync(string taskId, int maxCharacters, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(taskId) || maxCharacters <= 0) return string.Empty;

            try
            {
                VirtualContextLedgerStateResponse state = await SendAsync<VirtualContextLedgerStateResponse>(
                    HttpMethod.Get,
                    "/api/v2/virtual-context/ledger/" + Uri.EscapeDataString(Scope) + "/" + Uri.EscapeDataString(taskId),
                    null, ct).ConfigureAwait(false);
                return Truncate(state.PromptText ?? string.Empty, maxCharacters);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return string.Empty; }
        }

        internal static string Truncate(string value, int maxCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxCharacters) return value ?? string.Empty;
            if (maxCharacters <= 0) return string.Empty;
            const string marker = "\n[truncated]";
            if (maxCharacters <= marker.Length) return value.Substring(0, maxCharacters);
            return value.Substring(0, maxCharacters - marker.Length) + marker;
        }

        private static async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, bootstrap.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
            if (body != null) request.Content = JsonContent.Create(body);
            using HttpResponseMessage response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new LocalAiException(
                    "Coding task memory request failed (" + (int)response.StatusCode + "): " + detail);
            }
            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (value == null) throw new LocalAiException("PaRuX Core returned an empty task memory response.");
            return value;
        }
    }
}
