using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Atomic, bounded persistence of dismissed suggestion ids, so the same suggestion is not shown again.
/// A suggestion id embeds its target build, so a genuinely newer build is a new suggestion.</summary>
internal sealed class RuntimeSuggestionDismissalStore
{
    internal const string FileName = "runtime-suggestion-dismissals.json";
    internal const int MaximumIds = 200;
    internal const int MaximumIdLength = 120;
    private static readonly object Gate = new();
    private readonly string _path;

    internal RuntimeSuggestionDismissalStore() : this(ServerState.StateDirectory) { }
    internal RuntimeSuggestionDismissalStore(string stateDirectory) => _path = Path.Combine(stateDirectory, FileName);

    internal HashSet<string> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_path)) return new HashSet<string>(StringComparer.Ordinal);
                var info = new FileInfo(_path);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 64 * 1024)
                    return new HashSet<string>(StringComparer.Ordinal);
                var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new List<string>();
                return new HashSet<string>(ids.Where(Valid), StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Returns false for an empty/oversized/odd id.</summary>
    internal bool Dismiss(string id)
    {
        if (!Valid(id)) return false;
        lock (Gate)
        {
            HashSet<string> ids = Load();
            ids.Add(id);
            List<string> bounded = ids.OrderBy(i => i, StringComparer.Ordinal).TakeLast(MaximumIds).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(bounded), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return true;
        }
    }

    private static bool Valid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaximumIdLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.');
}

/// <summary>Builds advisor suggestions from a hardware/runtime probe and hides dismissed ones. Never installs anything.</summary>
internal sealed class RuntimeSuggestionService
{
    private readonly Func<CancellationToken, Task<AdvisorInput>> _probe;
    private readonly RuntimeSuggestionDismissalStore _dismissals;

    internal RuntimeSuggestionService(Func<CancellationToken, Task<AdvisorInput>> probe, RuntimeSuggestionDismissalStore dismissals)
    {
        _probe = probe;
        _dismissals = dismissals;
    }

    internal async Task<RuntimeSuggestionsResponse> GetAsync(CancellationToken ct)
    {
        List<RuntimeBackendSuggestion> all = RuntimeBackendAdvisor.Advise(await _probe(ct).ConfigureAwait(false));
        HashSet<string> dismissed = _dismissals.Load();
        return new RuntimeSuggestionsResponse
        {
            Suggestions = all.Where(s => !dismissed.Contains(s.Id)).ToList(),
            DismissedCount = all.Count(s => dismissed.Contains(s.Id))
        };
    }

    internal bool Dismiss(string id) => _dismissals.Dismiss(id);

    /// <summary>Hardware-switch suggestions for the maintenance report. Upgrades are excluded because the
    /// existing runtime_update proposals already report newer builds.</summary>
    internal async Task<IReadOnlyList<MaintenanceProposal>> ProposalsAsync(CancellationToken ct)
    {
        RuntimeSuggestionsResponse response = await GetAsync(ct).ConfigureAwait(false);
        return response.Suggestions
            .Where(s => s.Kind == "switch" && s.EvidenceLevel == "unmeasured" && s.Action == "install_managed")
            .Select(s => new MaintenanceProposal
            {
                Kind = "runtime_backend",
                Key = "backend:" + s.Id,
                Detail = s.Title + ": " + s.Why,
                Reason = string.Join("; ", s.Evidence)
            }).ToList();
    }
}

internal static class RuntimeSuggestionComposition
{
    private static readonly TimeSpan NetworkBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private static readonly object CacheGate = new();
    private static DateTime _cachedAt = DateTime.MinValue;
    private static Dictionary<string, string> _cachedLatest = new();

    internal static RuntimeSuggestionService Create(IServiceProvider sp)
    {
        var gpuInventory = new GpuInventoryService();
        var telemetry = new GpuPerformanceTelemetryService();
        ComponentInventoryService inventory = sp.GetRequiredService<ComponentInventoryService>();
        RuntimeInstallService installer = sp.GetRequiredService<RuntimeInstallService>();
        RuntimeManagerService runtimeManager = sp.GetRequiredService<RuntimeManagerService>();
        LocalBenchmarkHistoryStore benchmarkHistory = sp.GetRequiredService<LocalBenchmarkHistoryStore>();

        return new RuntimeSuggestionService(async ct =>
        {
            GpuInventoryStatus gpus = gpuInventory.GetStatus();
            var adapters = new List<AdvisorGpu>();
            List<GpuPerformanceTelemetryDto> smi = new();
            if (gpus.Adapters.Any(a => a.VendorId == RuntimeBackendAdvisor.NvidiaVendor && !a.IsSoftware))
            {
                try { smi = await telemetry.QueryAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { smi = new(); }
            }
            foreach (GpuAdapterStatus a in gpus.Adapters.Where(a => !a.IsSoftware))
            {
                double? cc = a.VendorId == RuntimeBackendAdvisor.NvidiaVendor
                    ? smi.FirstOrDefault(s => s.ComputeCapability is not null && a.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase))?.ComputeCapability
                    : null;
                adapters.Add(new AdvisorGpu(a.Name, a.VendorId, a.DedicatedVideoMemoryBytes, a.SharedSystemMemoryBytes, a.DriverVersion, cc));
            }

            var installed = new List<AdvisorRuntime>();
            try
            {
                IReadOnlyList<ComponentStatus> components = await inventory.DiscoverAsync(ct).ConfigureAwait(false);
                ResolvedTextRuntime? active = InstalledRuntimeResolver.Resolve(components,
                    b => LocalAiPaths.GetActiveRuntimeBuildTag(b) ?? LocalAiPaths.DefaultRuntimeBuildTag(b));
                foreach ((string id, string backend) in new[] { ("llama-server-cuda", "text-cuda"), ("llama-server-vulkan", "text-vulkan") })
                {
                    ComponentStatus? c = components.FirstOrDefault(x => x.Id == id && x.Detected);
                    if (c is null) continue;
                    installed.Add(new AdvisorRuntime(backend,
                        LocalAiPaths.GetActiveRuntimeBuildTag(backend) ?? LocalAiPaths.DefaultRuntimeBuildTag(backend),
                        c.Ownership, active?.Backend == backend));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { installed.Clear(); }

            Dictionary<string, string> latest = await LatestBuildsAsync(installer, installed, ct).ConfigureAwait(false);
            bool known = gpus.Available || adapters.Count > 0;
            // Read-only lookup of local measurements for exactly this hardware; with none, suggestions stay "unmeasured".
            IReadOnlyList<AdvisorBenchmark>? measured = null;
            try
            {
                long ram = 0;
                try { ram = (await runtimeManager.GetStatusAsync(ct).ConfigureAwait(false))?.RamTotalBytes ?? 0; }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
                string digest = LocalBenchmarkIdentity.HardwareDigest(gpus, ram, LocalBenchmarkIdentity.CpuLabel());
                measured = benchmarkHistory.MeasuredDecodeByBackend(digest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { measured = null; }
            return new AdvisorInput(known, adapters, DetectCpu(), installed, latest, measured);
        }, new RuntimeSuggestionDismissalStore());
    }

    private static AdvisorCpu DetectCpu() =>
        Avx2.IsSupported || Sse2.IsSupported ? new AdvisorCpu(Avx2.IsSupported, Avx512F.IsSupported) : new AdvisorCpu();

    private static async Task<Dictionary<string, string>> LatestBuildsAsync(
        RuntimeInstallService installer, List<AdvisorRuntime> installed, CancellationToken ct)
    {
        lock (CacheGate)
        {
            if (DateTime.UtcNow - _cachedAt < CacheFor) return new Dictionary<string, string>(_cachedLatest);
        }
        var latest = new Dictionary<string, string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(NetworkBudget);
        foreach (AdvisorRuntime r in installed)
        {
            try
            {
                List<RuntimeBuildOption> builds = await installer.ListAvailableAsync(r.Backend, budget.Token).ConfigureAwait(false);
                string? tag = builds.Select(b => b.Tag).Where(t => ModelRuntimeRequirements.TryParseBuild(t) is not null)
                    .OrderByDescending(t => ModelRuntimeRequirements.TryParseBuild(t)).FirstOrDefault();
                if (tag is not null) latest[r.Backend] = tag;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { /* offline: no upgrade claim */ }
        }
        if (installed.Count > 0 && latest.Count == installed.Count)
            lock (CacheGate) { _cachedAt = DateTime.UtcNow; _cachedLatest = new Dictionary<string, string>(latest); }
        return latest;
    }
}

internal static class RuntimeSuggestionEndpoints
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/runtime/suggestions", async (RuntimeSuggestionService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.GetAsync(ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return Results.Problem(detail: "Runtime suggestions are unavailable.", statusCode: 503); }
        }).RequireDesktopOwner();

        app.MapPost("/api/v1/runtime/suggestions/dismiss", (RuntimeSuggestionDismissRequest request, RuntimeSuggestionService service) =>
        {
            if (request is null || !service.Dismiss(request.Id)) return Results.BadRequest(new { error = "A valid suggestion id is required." });
            return Results.NoContent();
        }).RequireDesktopOwner();
    }
}
