using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Where the "Set up automatically" run currently is. Persisted, so a restart resumes.</summary>
    public enum AutoSetupPhase
    {
        NotStarted,
        CheckingHardware,
        CheckingRuntime,
        SelectingModel,
        Downloading,
        AssigningRoles,
        VerifyingChat,
        /// <summary>Chat answered a one-time code through the local route. The only success state.</summary>
        Ready,
        /// <summary>No recommended model fits this PC. Manual path stays available.</summary>
        NoModelFits,
        /// <summary>A step failed. FailedPhase says which; Retry resumes there.</summary>
        Failed,
        /// <summary>The user cancelled, or declined the licence review. Nothing is marked ready.</summary>
        Cancelled,
        Skipped
    }

    public enum AutoSetupFit { Fits, Tight, TooBig, Unknown }

    /// <summary>Persisted run state. Contains no secrets, only identifiers of the pinned plan.</summary>
    public sealed class AutoSetupState
    {
        public int Version { get; set; } = 1;
        public AutoSetupPhase Phase { get; set; } = AutoSetupPhase.NotStarted;
        public AutoSetupPhase FailedPhase { get; set; } = AutoSetupPhase.NotStarted;
        public string? Message { get; set; }
        public string? RuntimeNote { get; set; }
        public string? RepoId { get; set; }
        public string? ModelTitle { get; set; }
        public string? Quantization { get; set; }
        public string? SetName { get; set; }
        public string? Directory { get; set; }
        public int PartCount { get; set; }
        public string? Revision { get; set; }
        public long TotalBytes { get; set; }
        public List<string> DownloadIds { get; set; } = new();
        public Dictionary<string, int> Attempts { get; set; } = new();
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public AutoSetupState Clone() => new()
        {
            Version = Version, Phase = Phase, FailedPhase = FailedPhase, Message = Message, RuntimeNote = RuntimeNote,
            RepoId = RepoId, ModelTitle = ModelTitle, Quantization = Quantization, SetName = SetName,
            Directory = Directory, PartCount = PartCount, Revision = Revision, TotalBytes = TotalBytes,
            DownloadIds = new List<string>(DownloadIds), Attempts = new Dictionary<string, int>(Attempts),
            UpdatedUtc = UpdatedUtc
        };
    }

    public sealed class AutoSetupSnapshot
    {
        public AutoSetupState State { get; init; } = new();
        public InstallProgressPresentation? Progress { get; init; }
    }

    public sealed class AutoSetupRuntimeOffer
    {
        public string ComponentId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        /// <summary>True when there is no usable text runtime and this install is the only way forward.</summary>
        public bool Required { get; set; }
    }

    public sealed class AutoSetupRuntimeInfo
    {
        public bool Detected { get; set; }
        public string? ComponentId { get; set; }
        /// <summary>PARUX_MANAGED, USER_MANAGED or SYSTEM_DETECTED.</summary>
        public string Ownership { get; set; } = string.Empty;
        public string? Version { get; set; }
        /// <summary>A better backend or a missing baseline PaRuX could install. Null when none.</summary>
        public AutoSetupRuntimeOffer? Offer { get; set; }

        public bool IsUserManaged =>
            string.Equals(Ownership, "USER_MANAGED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All I/O the orchestration needs. The desktop view implements it over the existing
    /// clients; tests fake it, so the whole flow is hermetic.</summary>
    public interface IAutoSetupHost
    {
        Task<FirstModelAdvice> GetAdviceAsync(CancellationToken ct);
        Task<AutoSetupRuntimeInfo> GetTextRuntimeAsync(CancellationToken ct);
        /// <summary>Explicit user confirmation for a PaRuX-managed runtime install. False = declined.</summary>
        Task<bool> ConfirmRuntimeInstallAsync(AutoSetupRuntimeOffer offer, CancellationToken ct);
        Task InstallRuntimeAsync(AutoSetupRuntimeOffer offer, IProgress<InstallProgressPresentation> progress, CancellationToken ct);
        Task<IReadOnlyList<ModelQuantChoice>> GetQuantChoicesAsync(string repoId, CancellationToken ct);
        Task<AutoSetupFit> EstimateFitAsync(long totalBytes, CancellationToken ct);
        Task<ModelDownloadPlan> GetPlanAsync(string repoId, ModelQuantChoice choice, CancellationToken ct);
        /// <summary>True when every file of the plan is already in managed storage.</summary>
        Task<bool> IsPlanInstalledAsync(ModelDownloadPlan plan, CancellationToken ct);
        /// <summary>Runs the existing licence review (ModelLicenseConsentPrompt) and, only if the user
        /// agrees, starts the pinned plan. Returns the download ids, or null when the user declined.</summary>
        Task<IReadOnlyList<string>?> ReviewLicenceAndStartAsync(ModelDownloadPlan plan, ModelQuantChoice choice, CancellationToken ct);
        Task<IReadOnlyList<ModelDownloadStatus>> ListDownloadsAsync(CancellationToken ct);
        Task CancelDownloadsAsync(IReadOnlyList<string> ids);
        Task<RoleAutoAssignResponse> AutoAssignRolesAsync(CancellationToken ct);
        /// <summary>Existing verified-chat challenge. Null = verified; otherwise the honest failure.</summary>
        Task<string?> VerifyChatAsync(CancellationToken ct);
    }

    public interface IAutoSetupStateStore
    {
        AutoSetupState? Load();
        void Save(AutoSetupState state);
        void Clear();
    }

    public sealed class InMemoryAutoSetupStateStore : IAutoSetupStateStore
    {
        private string? _json;
        public AutoSetupState? Load() => _json == null ? null : JsonSerializer.Deserialize<AutoSetupState>(_json);
        public void Save(AutoSetupState state) => _json = JsonSerializer.Serialize(state);
        public void Clear() => _json = null;
    }

    public sealed class FileAutoSetupStateStore : IAutoSetupStateStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
        private readonly string _path;
        public FileAutoSetupStateStore(string path) => _path = path;

        public AutoSetupState? Load()
        {
            try
            {
                if (!File.Exists(_path)) return null;
                AutoSetupState? state = JsonSerializer.Deserialize<AutoSetupState>(File.ReadAllText(_path));
                return state is { Version: 1 } ? state : null;
            }
            catch { return null; }
        }

        public void Save(AutoSetupState state)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
                File.Move(temp, _path, overwrite: true);
            }
            catch { /* progress persistence is best effort; the live run is unaffected */ }
        }

        public void Clear()
        {
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }

    /// <summary>Pure model selection for the automatic path.</summary>
    public static class AutoSetupModelPicker
    {
        private static readonly string[] PreferredQuants =
        {
            "Q4_K_M", "Q4_K_S", "Q5_K_M", "Q4_0", "Q5_K_S", "Q6_K", "Q4_1", "IQ4_XS", "Q3_K_L", "Q3_K_M", "Q8_0"
        };

        /// <summary>Best standard general-chat candidate that fits and whose runtime requirement is satisfied.
        /// Null when nothing fits (the TooLarge / no-model path).</summary>
        public static FirstModelCandidate? PickCandidate(FirstModelAdvice? advice)
        {
            if (advice == null) return null;
            var eligible = advice.Candidates.Where(c =>
                    c.Suitability is "FitsOnGpu" or "SplitWithRam" or "CpuOnly"
                    && !string.IsNullOrWhiteSpace(c.Model.HuggingFaceRepoId)
                    && c.Model.ContentProfileId == ModelContentProfileIds.Standard
                    && (c.Model.CapabilityIds.Count == 0 || c.Model.CapabilityIds.Contains(ModelCapabilityIds.GeneralChat))
                    && c.RuntimeRequirementState is null or "Satisfied")
                .ToList();
            if (eligible.Count == 0) return null;
            FirstModelCandidate? recommended = eligible.FirstOrDefault(c => c.Model.Id == advice.RecommendedId);
            if (recommended != null) return recommended;
            return eligible.OrderBy(c => TierOf(c.Suitability)).ThenBy(c => c.Rank).First();
        }

        private static int TierOf(string suitability) => suitability switch
        {
            "FitsOnGpu" => 0, "SplitWithRam" => 1, "CpuOnly" => 2, _ => 3
        };

        /// <summary>Picks the complete quantisation that fits best: fit tier, then the hinted quant,
        /// then the common-quality order, then the smaller file. An over-size file is only allowed when
        /// the candidate itself is already a RAM/CPU model and the file is not much bigger than estimated.</summary>
        public static async Task<ModelQuantChoice?> PickQuantAsync(
            IEnumerable<ModelQuantChoice> choices, FirstModelCandidate candidate,
            Func<long, Task<AutoSetupFit>> estimateFit)
        {
            var scored = new List<(ModelQuantChoice Choice, int Tier)>();
            foreach (ModelQuantChoice choice in choices)
            {
                if (!choice.Complete || choice.SizeBytes <= 0) continue;
                AutoSetupFit fit = await estimateFit(choice.SizeBytes).ConfigureAwait(false);
                int tier = fit switch { AutoSetupFit.Fits => 0, AutoSetupFit.Tight => 1, AutoSetupFit.Unknown => 2, _ => 3 };
                if (tier == 3)
                {
                    bool offloadModel = candidate.Suitability is "SplitWithRam" or "CpuOnly";
                    long estimate = candidate.Model.EstimatedSizeBytes;
                    if (!offloadModel || estimate <= 0 || choice.SizeBytes > estimate * 1.25) continue;
                }
                if (tier == 2 && candidate.Suitability == "TooLarge") continue;
                scored.Add((choice, tier));
            }

            string hint = candidate.Model.QuantHint?.Trim() ?? string.Empty;
            return scored
                .OrderBy(s => s.Tier)
                .ThenBy(s => string.Equals(s.Choice.Quantization, hint, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(s => PreferenceOf(s.Choice.Quantization))
                .ThenBy(s => s.Choice.SizeBytes)
                .Select(s => s.Choice)
                .FirstOrDefault();
        }

        private static int PreferenceOf(string quant)
        {
            int index = Array.FindIndex(PreferredQuants,
                q => string.Equals(q, quant, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? PreferredQuants.Length : index;
        }
    }

    /// <summary>
    /// Pure orchestration of "Set up automatically": hardware, runtime, one pinned model plan,
    /// download, role assignment, then the verified-chat challenge. Every external effect goes through
    /// <see cref="IAutoSetupHost"/>. State is persisted after each transition so a restart resumes,
    /// retries are bounded per phase, and Ready is only reached when chat verification passes.
    /// </summary>
    public sealed class OnboardingAutoSetupService
    {
        public const int MaxRetriesPerPhase = 3;
        public const int MaxConsecutivePollFailures = 8;
        public const int MaxModelRestarts = 2;

        private readonly IAutoSetupHost _host;
        private readonly IAutoSetupStateStore _store;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly Func<DateTimeOffset> _now;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _stallTimeout;
        private AutoSetupState _state;
        private int _modelRestarts;

        public event Action<AutoSetupSnapshot>? Changed;

        public OnboardingAutoSetupService(
            IAutoSetupHost host, IAutoSetupStateStore store,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            Func<DateTimeOffset>? now = null,
            TimeSpan? pollInterval = null, TimeSpan? stallTimeout = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _delay = delay ?? Task.Delay;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
            _stallTimeout = stallTimeout ?? TimeSpan.FromMinutes(15);
            _state = _store.Load() ?? new AutoSetupState();
        }

        public AutoSetupState State => _state.Clone();

        /// <summary>True when a previous process stopped mid-run and <see cref="RunAsync"/> would resume it.</summary>
        public bool IsResumable => _state.Phase is AutoSetupPhase.CheckingHardware or AutoSetupPhase.CheckingRuntime
            or AutoSetupPhase.SelectingModel or AutoSetupPhase.Downloading or AutoSetupPhase.AssigningRoles
            or AutoSetupPhase.VerifyingChat;

        public bool IsTerminal => !IsResumable && _state.Phase != AutoSetupPhase.NotStarted;

        /// <summary>Begins from the top (NotStarted, Cancelled, Skipped, NoModelFits), keeping a running or
        /// failed run untouched. Call <see cref="RunAsync"/> afterwards.</summary>
        public void Start()
        {
            if (_state.Phase is AutoSetupPhase.NotStarted or AutoSetupPhase.Cancelled
                or AutoSetupPhase.Skipped or AutoSetupPhase.NoModelFits)
            {
                _state = new AutoSetupState { Phase = AutoSetupPhase.CheckingHardware, Attempts = _state.Attempts };
                _modelRestarts = 0;
                Persist();
            }
        }

        /// <summary>Failed -> resume at the failed phase, unless that phase already used its retries.</summary>
        public bool TryPrepareRetry()
        {
            if (_state.Phase != AutoSetupPhase.Failed) return false;
            string key = _state.FailedPhase.ToString();
            _state.Attempts.TryGetValue(key, out int used);
            if (used >= MaxRetriesPerPhase)
            {
                _state.Message = "Automatic retries for this step are used up (" + MaxRetriesPerPhase
                    + "). Use the manual path in Components, Model Hub or Verify local chat, then continue.";
                Persist();
                return false;
            }

            _state.Attempts[key] = used + 1;
            _state.Phase = _state.FailedPhase == AutoSetupPhase.NotStarted ? AutoSetupPhase.CheckingHardware : _state.FailedPhase;
            _state.Message = null;
            Persist();
            return true;
        }

        public void Skip()
        {
            if (_state.Phase == AutoSetupPhase.Ready) return;
            _state.Phase = AutoSetupPhase.Skipped;
            _state.Message = "Automatic setup skipped. The manual steps below stay available.";
            Persist();
        }

        public void Reset()
        {
            _state = new AutoSetupState();
            _modelRestarts = 0;
            _store.Clear();
            Raise(null);
        }

        public async Task<AutoSetupState> RunAsync(CancellationToken ct)
        {
            if (_state.Phase == AutoSetupPhase.NotStarted) Start();
            AutoSetupPhase running = _state.Phase;
            try
            {
                while (IsResumable)
                {
                    ct.ThrowIfCancellationRequested();
                    running = _state.Phase;
                    switch (_state.Phase)
                    {
                        case AutoSetupPhase.CheckingHardware: await CheckHardwareAsync(ct).ConfigureAwait(false); break;
                        case AutoSetupPhase.CheckingRuntime: await CheckRuntimeAsync(ct).ConfigureAwait(false); break;
                        case AutoSetupPhase.SelectingModel: await SelectModelAsync(ct).ConfigureAwait(false); break;
                        case AutoSetupPhase.Downloading: await DownloadAsync(ct).ConfigureAwait(false); break;
                        case AutoSetupPhase.AssigningRoles: await AssignRolesAsync(ct).ConfigureAwait(false); break;
                        case AutoSetupPhase.VerifyingChat: await VerifyAsync(ct).ConfigureAwait(false); break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await HandleCancelAsync(running).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Fail(running, DescribeFailure(running, ex));
            }

            return State;
        }

        // ---- phases ---------------------------------------------------------------------------

        private FirstModelAdvice? _advice;

        private async Task CheckHardwareAsync(CancellationToken ct)
        {
            Move(AutoSetupPhase.CheckingHardware, "Reading this PC's GPU and memory...");
            _advice = await _host.GetAdviceAsync(ct).ConfigureAwait(false);
            _state.Message = _advice.HardwareSummary;
            Move(AutoSetupPhase.CheckingRuntime, "Checking the local text runtime...");
        }

        private async Task CheckRuntimeAsync(CancellationToken ct)
        {
            AutoSetupRuntimeInfo info = await _host.GetTextRuntimeAsync(ct).ConfigureAwait(false);
            AutoSetupRuntimeOffer? offer = info.Offer;

            if (info.Detected && info.IsUserManaged)
            {
                // Never offer, install or change anything next to software the user manages.
                _state.RuntimeNote = "Using your own text runtime (user-managed). PaRuX did not change it.";
            }
            else if (info.Detected && (offer == null || offer.Required))
            {
                _state.RuntimeNote = "Using the existing text runtime"
                    + (string.IsNullOrWhiteSpace(info.Version) ? "." : " " + info.Version + ".");
            }
            else if (info.Detected)
            {
                // A better backend is available. Optional, only after an explicit yes.
                if (await _host.ConfirmRuntimeInstallAsync(offer!, ct).ConfigureAwait(false))
                {
                    Move(AutoSetupPhase.CheckingRuntime, "Installing " + offer!.Title + "...");
                    await _host.InstallRuntimeAsync(offer, new Progress<InstallProgressPresentation>(p => Raise(p)), ct)
                        .ConfigureAwait(false);
                    _state.RuntimeNote = offer.Title + " installed by PaRuX.";
                }
                else
                {
                    _state.RuntimeNote = "Keeping the current runtime; the optional " + offer!.Title + " was not installed.";
                }
            }
            else
            {
                if (offer == null)
                    throw new InvalidOperationException(
                        "No text runtime is installed and PaRuX has no installable build for it. Locate one in Components.");
                if (!await _host.ConfirmRuntimeInstallAsync(offer, ct).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "A text runtime is required and its install was not confirmed. Choose Install with PaRuX or Locate existing in Components.");
                Move(AutoSetupPhase.CheckingRuntime, "Installing " + offer.Title + "...");
                await _host.InstallRuntimeAsync(offer, new Progress<InstallProgressPresentation>(p => Raise(p)), ct)
                    .ConfigureAwait(false);
                _state.RuntimeNote = offer.Title + " installed by PaRuX.";
            }

            Move(AutoSetupPhase.SelectingModel, "Choosing the best model for this PC...");
        }

        private async Task SelectModelAsync(CancellationToken ct)
        {
            _advice ??= await _host.GetAdviceAsync(ct).ConfigureAwait(false);
            FirstModelCandidate? candidate = AutoSetupModelPicker.PickCandidate(_advice);
            if (candidate == null)
            {
                _state.Phase = AutoSetupPhase.NoModelFits;
                _state.Message = "None of the recommended models fits this PC's memory, so nothing was downloaded. "
                    + "Browse smaller models in Model Hub or use a model you already have.";
                Persist();
                return;
            }

            string repoId = candidate.Model.HuggingFaceRepoId;
            IReadOnlyList<ModelQuantChoice> choices = await _host.GetQuantChoicesAsync(repoId, ct).ConfigureAwait(false);
            ModelQuantChoice? choice = await AutoSetupModelPicker.PickQuantAsync(
                choices, candidate, bytes => _host.EstimateFitAsync(bytes, ct)).ConfigureAwait(false);
            if (choice == null)
            {
                _state.Phase = AutoSetupPhase.NoModelFits;
                _state.Message = "No complete file of " + candidate.Model.Title + " fits this PC's memory, so nothing was downloaded. "
                    + "Choose another model in Model Hub.";
                Persist();
                return;
            }

            ModelDownloadPlan plan = await _host.GetPlanAsync(repoId, choice, ct).ConfigureAwait(false);
            if (!plan.Complete)
                throw new InvalidOperationException("The chosen file set is incomplete at the source and cannot be downloaded.");
            if (!ModelRevisionIdentity.IsValid(plan.Revision))
                throw new InvalidOperationException("The repository did not report a pinned revision for this download.");

            _state.RepoId = repoId;
            _state.ModelTitle = candidate.Model.Title;
            _state.Quantization = choice.Quantization;
            _state.SetName = choice.SetName;
            _state.Directory = choice.Directory;
            _state.PartCount = choice.PartCount;
            _state.Revision = plan.Revision;
            _state.TotalBytes = plan.TotalBytes;

            if (await _host.IsPlanInstalledAsync(plan, ct).ConfigureAwait(false))
            {
                _state.DownloadIds.Clear();
                Move(AutoSetupPhase.AssigningRoles, candidate.Model.Title + " is already downloaded. Assigning roles...");
                return;
            }

            Move(AutoSetupPhase.SelectingModel, "Review the licence for " + candidate.Model.Title + " to start the download...");
            IReadOnlyList<string>? ids = await _host.ReviewLicenceAndStartAsync(plan, choice, ct).ConfigureAwait(false);
            if (ids == null)
            {
                _state.Phase = AutoSetupPhase.Cancelled;
                _state.Message = "Licence review was declined, so nothing was downloaded.";
                Persist();
                return;
            }

            if (ids.Count == 0)
                throw new InvalidOperationException("The download did not start: Core returned no transfer.");
            _state.DownloadIds = ids.ToList();
            Move(AutoSetupPhase.Downloading, "Downloading " + candidate.Model.Title + "...");
        }

        private async Task DownloadAsync(CancellationToken ct)
        {
            int pollFailures = 0;
            long lastBytes = -1;
            DateTimeOffset lastProgressAt = _now();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<ModelDownloadStatus> all;
                try
                {
                    all = await _host.ListDownloadsAsync(ct).ConfigureAwait(false);
                    pollFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (++pollFailures >= MaxConsecutivePollFailures)
                        throw new InvalidOperationException("PaRuX Core stopped answering download status requests: " + ex.Message);
                    await _delay(_pollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                var mine = all.Where(s => _state.DownloadIds.Contains(s.Id)).ToList();
                if (mine.Count == 0)
                {
                    // Core restarted and forgot the job. Re-plan; an already complete file is detected there
                    // and a partial one resumes through the normal download path.
                    if (++_modelRestarts > MaxModelRestarts)
                        throw new InvalidOperationException("The download keeps disappearing from PaRuX Core. Use Model Hub to resume it.");
                    _state.DownloadIds.Clear();
                    Move(AutoSetupPhase.SelectingModel, "Reconnecting to the download...");
                    return;
                }

                ModelDownloadStatus? failed = mine.FirstOrDefault(s => s.Status is "failed" or "cancelled");
                if (failed != null)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(failed.Error)
                        ? "The download " + failed.Status + "."
                        : failed.Error);
                ModelDownloadStatus? unverified = mine.FirstOrDefault(s =>
                    s.Status == "completed" && string.Equals(s.VerificationStatus, "failed", StringComparison.OrdinalIgnoreCase));
                if (unverified != null)
                    throw new InvalidOperationException("The downloaded file failed its integrity check and cannot be used.");

                if (mine.Count >= _state.DownloadIds.Count && mine.All(s => s.Status == "completed"))
                {
                    _state.DownloadIds.Clear();
                    Move(AutoSetupPhase.AssigningRoles, "Download complete. Assigning model roles...");
                    return;
                }

                long done = mine.Sum(s => s.BytesDownloaded);
                long total = Math.Max(mine.Sum(s => s.TotalBytes), _state.TotalBytes);
                double speed = mine.Sum(s => s.SpeedBytesPerSecond ?? 0);
                bool paused = mine.Any(s => s.Status == "paused");
                Raise(InstallProgressPresenter.Present(done, total, paused ? "paused" : "downloading",
                    speed > 0 ? speed : null, _state.ModelTitle));

                DateTimeOffset now = _now();
                if (done != lastBytes) { lastBytes = done; lastProgressAt = now; }
                else if (!paused && now - lastProgressAt > _stallTimeout)
                    throw new InvalidOperationException("The download made no progress for "
                        + (int)_stallTimeout.TotalMinutes + " minutes.");

                await _delay(_pollInterval, ct).ConfigureAwait(false);
            }
        }

        private async Task AssignRolesAsync(CancellationToken ct)
        {
            Move(AutoSetupPhase.AssigningRoles, "Assigning model roles...");
            RoleAutoAssignResponse response = await _host.AutoAssignRolesAsync(ct).ConfigureAwait(false);
            if (!response.Success)
                throw new InvalidOperationException("Roles could not be assigned automatically: " + response.Summary);
            Move(AutoSetupPhase.VerifyingChat, "Asking the local model to repeat a one-time code...");
        }

        private async Task VerifyAsync(CancellationToken ct)
        {
            string? failure = await _host.VerifyChatAsync(ct).ConfigureAwait(false);
            if (failure != null) throw new InvalidOperationException(failure);
            _state.Phase = AutoSetupPhase.Ready;
            _state.Message = "Verified: a local model answered the one-time code. PaRuX is ready.";
            Persist();
        }

        // ---- helpers --------------------------------------------------------------------------

        private async Task HandleCancelAsync(AutoSetupPhase running)
        {
            List<string> ids = _state.DownloadIds.ToList();
            if (ids.Count > 0)
            {
                try { await _host.CancelDownloadsAsync(ids).ConfigureAwait(false); }
                catch { /* best effort; the state below stays honest either way */ }
            }

            _state.DownloadIds.Clear();
            _state.Phase = AutoSetupPhase.Cancelled;
            _state.Message = "Automatic setup was cancelled. Nothing is marked ready. Start it again or use the manual steps.";
            Persist();
        }

        private void Fail(AutoSetupPhase phase, string message)
        {
            _state.FailedPhase = phase;
            _state.Phase = AutoSetupPhase.Failed;
            _state.Message = message;
            Persist();
        }

        private static string DescribeFailure(AutoSetupPhase phase, Exception ex)
        {
            string step = phase switch
            {
                AutoSetupPhase.CheckingHardware => "Reading hardware",
                AutoSetupPhase.CheckingRuntime => "Preparing the text runtime",
                AutoSetupPhase.SelectingModel => "Choosing the model",
                AutoSetupPhase.Downloading => "Downloading the model",
                AutoSetupPhase.AssigningRoles => "Assigning roles",
                AutoSetupPhase.VerifyingChat => "Verifying local chat",
                _ => "Setup"
            };
            return step + " failed: " + ex.Message;
        }

        private void Move(AutoSetupPhase phase, string message)
        {
            _state.Phase = phase;
            _state.Message = message;
            Persist();
        }

        private void Persist()
        {
            _state.UpdatedUtc = DateTime.UtcNow;
            _store.Save(_state);
            Raise(null);
        }

        private void Raise(InstallProgressPresentation? progress) =>
            Changed?.Invoke(new AutoSetupSnapshot { State = _state.Clone(), Progress = progress });
    }
}
