using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Counts prompt tokens with the tokenizer the model is actually being served with, instead of the
/// character heuristic in <see cref="PaRuX.Intelligence.ConversationWindowPlanner"/>.
/// Per-text counts keep planning cheap and cacheable. A separate final-payload path uses
/// llama-server's chat input-token endpoint so the actual chat template, tools, response schema and
/// template kwargs are verified together before generation when the managed baseline is active.
/// </summary>
public sealed class RuntimeTokenCounter
{
    /// <summary>
    /// How a token count was obtained. Callers surface this so an estimate is never presented as a
    /// measurement: <see cref="Tokenizer"/> counts come from the serving runtime's own tokenizer per
    /// text, <see cref="Estimated"/> counts come from the character heuristic.
    /// </summary>
    internal enum CountBasis { Estimated, PartlyEstimated, Tokenizer }

    /// <summary>Counts prepared against runtime measurements; an unseen text downgrades the basis.</summary>
    internal readonly record struct PreparedCounter
    {
        private readonly Func<CountBasis>? _basisProvider;

        internal PreparedCounter(Func<string, int> count, CountBasis basis)
            : this(count, () => basis)
        {
        }

        internal PreparedCounter(Func<string, int> count, Func<CountBasis> basisProvider)
        {
            Count = count;
            _basisProvider = basisProvider;
        }

        internal Func<string, int> Count { get; }
        internal CountBasis Basis => _basisProvider?.Invoke() ?? CountBasis.Estimated;

        internal PaRuX.Intelligence.ConversationWindowPlanner.TokenCountBasis PlanBasis => Basis switch
        {
            CountBasis.Tokenizer => PaRuX.Intelligence.ConversationWindowPlanner.TokenCountBasis.Tokenizer,
            CountBasis.PartlyEstimated => PaRuX.Intelligence.ConversationWindowPlanner.TokenCountBasis.PartlyEstimated,
            _ => PaRuX.Intelligence.ConversationWindowPlanner.TokenCountBasis.Estimated
        };
    }

    private const int MaximumCachedCounts = 512;
    internal const int PerMessageTemplateOverheadTokens = 8;
    private const string Separator = "\u0000";

    private readonly HttpClient _http;
    private readonly IRuntimeTokenCountCache _cache;
    private readonly ConcurrentQueue<string> _order = new();

    private sealed class CountBasisTracker(CountBasis initialBasis)
    {
        private int _basis = (int)initialBasis;

        internal CountBasis Basis => (CountBasis)Volatile.Read(ref _basis);

        internal void MarkEstimated() => Interlocked.CompareExchange(
            ref _basis, (int)CountBasis.PartlyEstimated, (int)CountBasis.Tokenizer);
    }

    public RuntimeTokenCounter(HttpClient http)
        : this(http, new ConcurrentRuntimeTokenCountCache())
    {
    }

    internal RuntimeTokenCounter(HttpClient http, IRuntimeTokenCountCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public Task<Func<string, int>> PrepareAsync(
        string providerEndpoint, string model, IEnumerable<string> texts, CancellationToken cancellationToken) =>
        PrepareAsync(
            providerEndpoint,
            model,
            ResolveRuntimeTokenizerIdentity(providerEndpoint, model),
            texts,
            cancellationToken);

    public Task<Func<string, int>> PrepareAsync(
        string providerEndpoint, string model, IEnumerable<string> texts, CancellationToken cancellationToken, string? explicitModelPath) =>
        PrepareAsync(
            providerEndpoint,
            model,
            ResolveRuntimeTokenizerIdentity(providerEndpoint, model, explicitModelPath),
            texts,
            cancellationToken);

    public void ClearCache()
    {
        _cache.Clear();
        while (_order.TryDequeue(out _)) { }
    }

    internal async Task<Func<string, int>> PrepareAsync(
        string providerEndpoint,
        string model,
        string tokenizerIdentity,
        IEnumerable<string> texts,
        CancellationToken cancellationToken) =>
        (await PrepareWithBasisAsync(providerEndpoint, model, tokenizerIdentity, texts, cancellationToken)
            .ConfigureAwait(false)).Count;

    /// <summary>Like <c>PrepareAsync</c> but also reports whether the counter is tokenizer-backed.</summary>
    internal Task<PreparedCounter> PrepareWithBasisAsync(
        string providerEndpoint, string model, IEnumerable<string> texts, CancellationToken cancellationToken, string? explicitModelPath) =>
        PrepareWithBasisAsync(
            providerEndpoint,
            model,
            ResolveRuntimeTokenizerIdentity(providerEndpoint, model, explicitModelPath),
            texts,
            cancellationToken);

    /// <summary>
    /// A tokenizer identity may key the shared cache only while it names one backend generation:
    /// the model file (or an explicit unresolved-model marker) plus the llama-server PID and start
    /// time, so a swap/restart produces a different key. When the backend process cannot be
    /// resolved (<c>backend=unresolved</c>/<c>backend=unknown</c>) or the identity itself failed,
    /// nothing distinguishes generations and counts are priced for the current call only.
    /// </summary>
    internal static bool IsStableTokenizerIdentity(string? identity) =>
        !string.IsNullOrWhiteSpace(identity)
        && !identity.Contains("backend=unresolved", StringComparison.Ordinal)
        && !identity.Contains("backend=unknown", StringComparison.Ordinal)
        && !identity.Contains("identity=unresolved", StringComparison.Ordinal);

    internal async Task<PreparedCounter> PrepareWithBasisAsync(
        string providerEndpoint,
        string model,
        string tokenizerIdentity,
        IEnumerable<string> texts,
        CancellationToken cancellationToken)
    {
        var estimated = new PreparedCounter(
            PaRuX.Intelligence.ConversationWindowPlanner.EstimateTokens, CountBasis.Estimated);
        string endpoint = (providerEndpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0) return estimated;
        string scope = endpoint + Separator + (model ?? string.Empty) + Separator + (tokenizerIdentity ?? string.Empty);
        bool shared = IsStableTokenizerIdentity(tokenizerIdentity);
        IRuntimeTokenCountCache cache = shared
            ? _cache
            : new ConcurrentRuntimeTokenCountCache();

        string[] corpus = texts.Where(text => !string.IsNullOrEmpty(text)).ToArray();
        string[] pending = corpus
            .Distinct(StringComparer.Ordinal)
            .Where(text => !cache.ContainsKey(Key(scope, text)))
            .ToArray();

        using var throttle = new SemaphoreSlim(4, 4);
        await Task.WhenAll(pending.Select(async text =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int tokens = await TokenizeAsync(endpoint, text, cancellationToken).ConfigureAwait(false);
                if (tokens > 0) Remember(cache, shared, Key(scope, text), tokens);
            }
            finally { throttle.Release(); }
        })).ConfigureAwait(false);

        double observed = ObservedCharactersPerToken(cache, scope, corpus);
        if (observed <= 0) return estimated;
        bool allMeasured = corpus.All(text => cache.ContainsKey(Key(scope, text)));
        var basis = new CountBasisTracker(allMeasured ? CountBasis.Tokenizer : CountBasis.PartlyEstimated);

        return new PreparedCounter(text =>
        {
            if (string.IsNullOrEmpty(text))
            {
                basis.MarkEstimated();
                return PaRuX.Intelligence.ConversationWindowPlanner.EstimateTokens(text);
            }
            if (cache.TryGetValue(Key(scope, text), out int tokens))
                return tokens + PerMessageTemplateOverheadTokens;
            basis.MarkEstimated();
            return (int)Math.Ceiling(text.Length / observed) + PerMessageTemplateOverheadTokens;
        }, () => basis.Basis);
    }

    internal async Task<int?> TryCountRenderedChatPromptAsync(
        string providerEndpoint,
        JsonObject providerPayload,
        CancellationToken cancellationToken)
    {
        string endpoint = (providerEndpoint ?? string.Empty).Trim().TrimEnd('/');
        if (!string.Equals(endpoint, LocalRuntimeService.CurrentManagedProviderEndpoint, StringComparison.OrdinalIgnoreCase))
            return null;
        return await CountRenderedChatPromptCoreAsync(endpoint, providerPayload, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Ungated rendered-count request. Production callers go through the ownership gate above.</summary>
    internal async Task<int?> CountRenderedChatPromptCoreAsync(
        string endpoint, JsonObject providerPayload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerPayload);

        JsonObject body = (JsonObject)providerPayload.DeepClone();
        body.Remove("stream");
        body.Remove("cache_prompt");
        body.Remove("n_cache_reuse");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/chat/completions/input_tokens")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("input_tokens", out JsonElement tokens)
                || !tokens.TryGetInt32(out int count)
                || count <= 0)
                return null;
            return count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    internal static string ResolveRuntimeTokenizerIdentity(
        string providerEndpoint, string model, string? explicitModelPath = null)
    {
        string endpoint = (providerEndpoint ?? string.Empty).Trim().TrimEnd('/');
        string alias = (model ?? string.Empty).Trim();
        if (!string.Equals(endpoint, LocalRuntimeService.CurrentManagedProviderEndpoint, StringComparison.OrdinalIgnoreCase))
            return "provider-model:" + alias;

        try
        {
            string modelPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(explicitModelPath) && File.Exists(explicitModelPath))
            {
                modelPath = explicitModelPath;
            }
            else if (HotModelPoolCatalog.TryGetPathForAlias(alias, out string poolModelPath) && File.Exists(poolModelPath))
            {
                modelPath = poolModelPath;
            }
            else if ((alias.Length == 0 || string.Equals(alias, "local-default", StringComparison.OrdinalIgnoreCase))
                && HotModelPoolCatalog.TryGetDefaultAlias(out string defaultAlias)
                && HotModelPoolCatalog.TryGetPathForAlias(defaultAlias, out string defaultPath)
                && File.Exists(defaultPath))
            {
                modelPath = defaultPath;
            }
            else
            {
                modelPath = ReadTrimmed(LocalAiPaths.ActiveModelPathFile);
            }
            string backendIdentity = ResolveManagedBackendIdentity();
            if (modelPath.Length == 0 || !File.Exists(modelPath))
                return "managed-alias:" + alias + "|model=unresolved|" + backendIdentity;

            var file = new FileInfo(modelPath);
            ManagedModelRevisionStore revisions = new();
            ManagedModelRevisionRecord? revision = revisions.FindByFileName(file.Name);
            bool trustedRevision = revisions.CanReuseLocalHash(revision, file);
            return BuildManagedTokenizerIdentity(alias, file, backendIdentity,
                trustedRevision ? revision : null);
        }
        catch
        {
            return "managed-alias:" + alias + "|identity=unresolved";
        }
    }

    /// <summary>
    /// Builds the tokenizer-cache generation key without hashing a multi-GB model on the chat path.
    /// PaRuX-managed downloads already record their verified local SHA-256, so that exact byte
    /// identity is preferred when size+mtime prove the cached SHA still belongs to the active file.
    /// USER_MANAGED/unreconciled files fall back to path+generation metadata; backend build remains
    /// part of both identities because runtime/template revisions can change tokenization semantics.
    /// </summary>
    internal static string BuildManagedTokenizerIdentity(
        string alias,
        FileInfo file,
        string backendIdentity,
        ManagedModelRevisionRecord? trustedRevision)
    {
        ArgumentNullException.ThrowIfNull(file);
        string full = file.FullName;
        if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant();
        string common = string.Join('|',
            "managed",
            "alias=" + (alias ?? string.Empty).Trim(),
            "path=" + full,
            "length=" + file.Length,
            "written=" + file.LastWriteTimeUtc.Ticks);

        if (trustedRevision != null
            && trustedRevision.LocalSha256.Length == 64
            && trustedRevision.LocalSha256.All(Uri.IsHexDigit))
        {
            return common
                + "|revision=" + (trustedRevision.RepoRevision ?? "local").Trim()
                + "|sha256=" + trustedRevision.LocalSha256.ToLowerInvariant()
                + "|" + backendIdentity;
        }

        return common
            + "|created=" + file.CreationTimeUtc.Ticks
            + "|" + backendIdentity;
    }

    private static string ResolveManagedBackendIdentity()
    {
        string pidText = ReadTrimmed(LocalAiPaths.TextPidFile);
        if (!int.TryParse(pidText, out int pid) || pid <= 0) return "backend=unresolved";
        try
        {
            using Process process = Process.GetProcessById(pid);
            string? executable = process.MainModule?.FileName;
            string? backend = string.IsNullOrWhiteSpace(executable) ? null
                : IsInside(executable, LocalAiPaths.RuntimeCudaPath) ? "text-cuda"
                : IsInside(executable, LocalAiPaths.RuntimeVulkanPath) ? "text-vulkan"
                : null;
            if (backend == null) return "backend=unknown";
            string tag = LocalAiPaths.GetActiveRuntimeBuildTag(backend)
                ?? LocalAiPaths.DefaultRuntimeBuildTag(backend);
            return "backend=" + backend + ";build=" + tag
                + ";pid=" + pid + ";start=" + process.StartTime.ToUniversalTime().Ticks;
        }
        catch { return "backend=unresolved"; }
    }

    private static bool IsInside(string path, string directory)
    {
        try
        {
            string root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ReadTrimmed(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch { return string.Empty; }
    }

    private static double ObservedCharactersPerToken(
        IRuntimeTokenCountCache cache, string scope, IEnumerable<string> texts)
    {
        long characters = 0;
        long tokens = 0;
        foreach (string text in texts.Where(text => !string.IsNullOrEmpty(text)).Distinct(StringComparer.Ordinal))
        {
            if (!cache.TryGetValue(Key(scope, text), out int counted)) continue;
            characters += text.Length;
            tokens += counted;
        }
        return tokens <= 0 ? 0 : Math.Max(0.25, (double)characters / tokens);
    }

    private async Task<int> TokenizeAsync(string endpoint, string text, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http
                .PostAsJsonAsync(endpoint + "/tokenize", new { content = text }, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ParseTokenCount(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return 0; }
    }

    internal static int ParseTokenCount(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("tokens", out JsonElement tokens)
        && tokens.ValueKind == JsonValueKind.Array
            ? tokens.GetArrayLength()
            : 0;

    private void Remember(IRuntimeTokenCountCache cache, bool shared, string key, int tokens)
    {
        if (!cache.TryAdd(key, tokens)) return;
        if (!shared) return;
        _order.Enqueue(key);
        while (_order.Count > MaximumCachedCounts && _order.TryDequeue(out string? oldest))
            _cache.TryRemove(oldest, out _);
    }

    private static string Key(string scope, string text) => scope + Separator + text;
}

internal interface IRuntimeTokenCountCache
{
    bool ContainsKey(string key);
    bool TryGetValue(string key, out int value);
    bool TryAdd(string key, int value);
    bool TryRemove(string key, out int value);
    void Clear();
}

internal sealed class ConcurrentRuntimeTokenCountCache : IRuntimeTokenCountCache
{
    private readonly ConcurrentDictionary<string, int> _entries = new(StringComparer.Ordinal);

    public bool ContainsKey(string key) => _entries.ContainsKey(key);
    public bool TryGetValue(string key, out int value) => _entries.TryGetValue(key, out value);
    public bool TryAdd(string key, int value) => _entries.TryAdd(key, value);
    public bool TryRemove(string key, out int value) => _entries.TryRemove(key, out value);
    public void Clear() => _entries.Clear();
}
