using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming.Providers;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>Background work that pauses itself for Gaming and can be resumed under an explicit policy.</summary>
public interface IGamingResumable
{
    Task<int> ResumeGamingPausedAsync(CancellationToken ct);
}

public sealed class WorkloadOrchestrator : BackgroundService
{
    private readonly List<IGamingResumable> _resumables;
    private readonly LocalRuntimeService _runtime;
    private readonly LocalAiProcessManager _processManager;
    private readonly MediaJobService _jobs;
    private readonly SpeechService _speech;
    private readonly GamingProfileStore _profileStore;
    private readonly ILogger<WorkloadOrchestrator>? _logger;

    private readonly object _stateGate = new();

    private string _state = "AiReady";
    private GamingProfileDto? _activeProfile;
    private string? _activeLauncherId;
    private string? _activeInputId;
    private string? _activeTriggerId;
    private DateTime _lastStateChangeUtc = DateTime.UtcNow;
    private DateTime? _remoteDisconnectUtc;
    /// <summary>When the current gaming session began; only processes started after it can be "the game".</summary>
    private DateTime? _sessionStartedUtc;
    private string? _lastErrorMessage;

    private int? _suspendedProcessId;
    private string? _suspendedProcessName;
    // 0 when nothing is suspended; null while a game is suspended and its GPU memory cannot be read.
    private long? _suspendedVramMib = 0;
    private readonly List<string> _suspendedAiWorkloads = new();

    private static readonly AsyncLocal<Func<int?>?> ActiveGameProcessFinderOverride = new();

    /// <summary>
    /// Test seam: <see cref="FindActiveGameProcessId"/> scans every process on the desktop for one
    /// with a visible main window, so its result depends on whatever else the machine running the
    /// test happens to have open (a browser, an editor, a terminal). Real desktop dependence in a
    /// unit test is what made this state machine flaky: exiting a gaming session correctly reaches
    /// "AiReady" only when no game is detected, but a windowed process on the test host can make
    /// this method report one anyway, landing the exit path in "GameSuspended" instead.
    /// </summary>
    internal static IDisposable UseActiveGameProcessFinderForTests(Func<int?> finder)
    {
        var previous = ActiveGameProcessFinderOverride.Value;
        ActiveGameProcessFinderOverride.Value = finder;
        return new ActiveGameProcessFinderScope(previous);
    }

    private sealed class ActiveGameProcessFinderScope : IDisposable
    {
        private readonly Func<int?>? _previous;
        private bool _disposed;

        internal ActiveGameProcessFinderScope(Func<int?>? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ActiveGameProcessFinderOverride.Value = _previous;
        }
    }

    // Provider Registry
    private readonly List<IGamingTriggerProvider> _triggerProviders;
    private readonly List<IGameLauncherProvider> _launcherProviders;
    private readonly List<IInputProvider> _inputProviders;
    private readonly List<IGameSuspendProvider> _suspendProviders;
    private readonly Func<CancellationToken, Task<bool>> _runtimeHealthCheck;
    private readonly Func<CancellationToken, Task> _restoreRuntimeToReady;

    private readonly Action? _cancelEvaluations;

    public WorkloadOrchestrator(
        LocalRuntimeService runtime,
        LocalAiProcessManager processManager,
        MediaJobService jobs,
        SpeechService speech,
        GamingProfileStore profileStore,
        ILogger<WorkloadOrchestrator>? logger = null,
        IReadOnlyCollection<IInputProvider>? inputProviders = null,
        IReadOnlyCollection<IGameLauncherProvider>? launcherProviders = null,
        Func<CancellationToken, Task<bool>>? runtimeHealthCheck = null,
        Func<CancellationToken, Task>? restoreRuntimeToReady = null,
        IEnumerable<IGamingResumable>? resumables = null,
        Action? cancelEvaluations = null)
    {
        _resumables = resumables?.ToList() ?? new List<IGamingResumable>();
        _runtime = runtime;
        _processManager = processManager;
        _jobs = jobs;
        _speech = speech;
        _profileStore = profileStore;
        _logger = logger;
        _runtimeHealthCheck = runtimeHealthCheck ?? runtime.IsHealthyAsync;
        _restoreRuntimeToReady = restoreRuntimeToReady ?? runtime.EnsureReadyAsync;
        _cancelEvaluations = cancelEvaluations;
        _triggerProviders = new()
        {
            new ManualTriggerProvider(),
            new WindowsDisplayTriggerProvider(),
            new SunshineSessionTriggerProvider()
        };
        _launcherProviders = launcherProviders?.ToList() ?? new()
        {
            new PlayniteLauncherProvider(),
            new SteamBigPictureLauncherProvider(),
            new CustomExecutableLauncherProvider(),
            new NoneLauncherProvider()
        };
        _inputProviders = inputProviders?.ToList() ?? new()
        {
            new NativeInputProvider(),
            new XOutputInputProvider(),
            new SunshineInputProvider(),
            new CustomExecutableInputProvider()
        };
        _suspendProviders = new()
        {
            new WindowsNativeSuspendProvider(),
            new NyrnaGameSuspendProvider(),
            new NoneGameSuspendProvider()
        };
    }

    public string CurrentState { get { lock (_stateGate) return _state; } }
    public bool IsGamingActive { get { lock (_stateGate) return _state is "EnteringGaming" or "GamingLocal" or "GamingRemote" or "ExitingGaming" or "GameSuspended"; } }

    public GamingStatusDto GetStatus()
    {
        // Read before taking _stateGate: a cold probe can wait up to its timeout, and state transitions
        // must never queue behind nvidia-smi.
        long systemTotalVram = ReadSystemTotalVramMib();
        lock (_stateGate)
        {
            long? suspendedVram = _suspendedVramMib;

            return new GamingStatusDto
            {
                State = _state,
                IsGamingActive = IsGamingActive,
                ActiveProfileName = _activeProfile?.Name,
                ActiveLauncher = _activeLauncherId,
                ActiveInput = _activeInputId,
                ActiveTrigger = _activeTriggerId,
                SuspendedProcessId = _suspendedProcessId,
                SuspendedProcessName = _suspendedProcessName,
                SuspendedVramMib = suspendedVram,
                SystemTotalVramMib = systemTotalVram,
                // An unknown suspended figure counts as zero, so free VRAM is then an upper bound.
                SystemFreeVramMib = Math.Max(0, systemTotalVram - (suspendedVram ?? 0)),
                SuspendedAiWorkloads = _suspendedAiWorkloads.ToList(),
                CanQuickResume = _suspendedProcessId.HasValue && _suspendedProcessId > 0,
                Message = _lastErrorMessage ?? GetStateDescription(_state),
                LastStateChangeUtc = _lastStateChangeUtc
            };
        }
    }

    private long ReadSystemTotalVramMib()
    {
        try { return _processManager.GetVramMibAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { return 0; }
    }

    /// <summary>
    /// Label for the text model that the gaming hard-off unloads. CPU wording applies only when DXGI
    /// positively reports no usable adapter; an unknown inventory keeps the GPU wording it always had.
    /// </summary>
    internal static string TextModelUnloadedLabel(GpuInventoryStatus inventory) =>
        RuntimeLaunchOptimizer.HasNoUsableGpuAdapter(inventory)
            ? "llama-server model unloaded (CPU only)"
            : "llama-server GPU model unloaded";

    public List<GamingProviderStatusDto> GetProviderStatuses()
    {
        var list = new List<GamingProviderStatusDto>();

        foreach (var p in _launcherProviders)
        {
            list.Add(new GamingProviderStatusDto
            {
                Category = "launcher",
                Id = p.Id,
                Name = p.Name,
                Installed = p.IsInstalled,
                Available = p.IsInstalled,
                ExecutablePath = p.ExecutablePath,
                Description = $"Game launcher: {p.Name}"
            });
        }

        foreach (var p in _inputProviders)
        {
            list.Add(new GamingProviderStatusDto
            {
                Category = "input",
                Id = p.Id,
                Name = p.Name,
                Installed = p.IsInstalled,
                Available = p.IsInstalled,
                ExecutablePath = p.ExecutablePath,
                Description = $"Input helper: {p.Name}"
            });
        }

        foreach (var p in _suspendProviders)
        {
            list.Add(new GamingProviderStatusDto
            {
                Category = "suspend",
                Id = p.Id,
                Name = p.Name,
                Installed = p.IsAvailable,
                Available = p.IsAvailable,
                ExecutablePath = null,
                Description = $"Game suspend utility: {p.Name}"
            });
        }

        foreach (var p in _triggerProviders)
        {
            list.Add(new GamingProviderStatusDto
            {
                Category = "trigger",
                Id = p.Id,
                Name = p.Name,
                Installed = p.IsDetected,
                Available = p.IsDetected,
                ExecutablePath = null,
                Description = $"Session trigger: {p.Name}"
            });
        }

        return list;
    }

    public async Task<bool> EnterGamingSessionAsync(string? profileName = null, CancellationToken ct = default)
    {
        GamingProfileDto profile = _profileStore.GetActiveOrDefault(profileName);
        return await TransitionToGamingAsync(profile, triggerId: "manual", ct).ConfigureAwait(false);
    }

    public async Task<bool> ExitGamingSessionAsync(CancellationToken ct = default)
    {
        // Called from the explicit "Return to AI" action: the user asked for AI back, so the
        // runtime is restored regardless of the profile's automatic-exit policy.
        return await TransitionToAiAsync(userInitiated: true, ct).ConfigureAwait(false);
    }

    /// <summary>Session end without a user request (remote grace-period expiry): the runtime
    /// reloads only when the profile's explicit RestoreAiOnExit policy is on.</summary>
    internal Task<bool> ExitGamingSessionAutomaticallyAsync(CancellationToken ct = default) =>
        TransitionToAiAsync(userInitiated: false, ct);

    public async Task<bool> SuspendActiveGameAsync(CancellationToken ct = default)
    {
        lock (_stateGate)
        {
            if (_state is not ("GamingLocal" or "GamingRemote")) return false;
        }

        int? gamePid = FindActiveGameProcessId(SessionStartedUtc());
        if (!gamePid.HasValue) return false;

        GamingProfileDto profile;
        lock (_stateGate) profile = _activeProfile ?? _profileStore.GetActiveOrDefault();

        IGameSuspendProvider provider = ResolveSuspendProvider(profile.SuspendProvider);
        bool suspended = await provider.SuspendProcessAsync(gamePid.Value, ct).ConfigureAwait(false);
        if (suspended)
        {
            // Measured outside _stateGate: nvidia-smi is a bounded process call and must not hold the lock.
            long? vram = await MeasureProcessVramMibAsync(gamePid.Value).ConfigureAwait(false);
            lock (_stateGate)
            {
                _suspendedProcessId = gamePid.Value;
                try { _suspendedProcessName = Process.GetProcessById(gamePid.Value).ProcessName; } catch { _suspendedProcessName = "Game"; }
                _suspendedVramMib = vram;
                SetState("GameSuspended");
            }
            return true;
        }

        return false;
    }

    public async Task<bool> ResumeSuspendedGameAsync(CancellationToken ct = default)
    {
        int pid;
        GamingProfileDto profile;
        lock (_stateGate)
        {
            if (_state != "GameSuspended" || !_suspendedProcessId.HasValue) return false;
            pid = _suspendedProcessId.Value;
            profile = _activeProfile ?? _profileStore.GetActiveOrDefault();
        }

        IGameSuspendProvider provider = ResolveSuspendProvider(profile.SuspendProvider);
        bool resumed = await provider.ResumeProcessAsync(pid, ct).ConfigureAwait(false);
        if (resumed)
        {
            lock (_stateGate)
            {
                _suspendedProcessId = null;
                _suspendedProcessName = null;
                _suspendedVramMib = 0;
                string targetState = string.Equals(profile.SessionType, "remote", StringComparison.OrdinalIgnoreCase) ? "GamingRemote" : "GamingLocal";
                SetState(targetState);
            }
            return true;
        }

        return false;
    }

    private async Task<bool> TransitionToGamingAsync(GamingProfileDto profile, string triggerId, CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_state is "EnteringGaming" or "GamingLocal" or "GamingRemote") return true;
            SetState("EnteringGaming");
            _sessionStartedUtc = DateTime.UtcNow;
            _activeProfile = profile;
            _activeTriggerId = triggerId;
            _lastErrorMessage = null;
        }

        IInputProvider? inputProvider = null;
        IGameLauncherProvider? launcherProvider = null;
        bool resourcesMayHaveBeenReleased = false;
        bool inputStartAttempted = false;
        bool launcherStartAttempted = false;
        try
        {
            inputProvider = ResolveInputProvider(profile.InputProvider);
            launcherProvider = ResolveLauncherProvider(profile.LauncherProvider);
            if (!inputProvider.IsInstalled)
                throw new InvalidOperationException($"Gaming input provider '{inputProvider.Id}' is not installed.");
            if (!launcherProvider.IsInstalled)
                throw new InvalidOperationException($"Gaming launcher provider '{launcherProvider.Id}' is not installed.");

            // 1. Declare GPU priority = gaming & release AI resources
            resourcesMayHaveBeenReleased = true;
            await ReleaseResourcesForGamingAsync(ct).ConfigureAwait(false);

            // 2. Start Input helper
            inputStartAttempted = true;
            if (!await inputProvider.StartAsync(profile, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"Gaming input provider '{inputProvider.Id}' could not start.");
            lock (_stateGate) _activeInputId = inputProvider.Id;

            // 3. Launch Launcher (e.g. Playnite Fullscreen)
            launcherStartAttempted = true;
            if (!await launcherProvider.LaunchAsync(profile, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"Gaming launcher provider '{launcherProvider.Id}' could not start.");
            lock (_stateGate) _activeLauncherId = launcherProvider.Id;

            // 4. Set final gaming state based on session type
            lock (_stateGate)
            {
                string nextState = string.Equals(profile.SessionType, "remote", StringComparison.OrdinalIgnoreCase) ? "GamingRemote" : "GamingLocal";
                SetState(nextState);
            }

            return true;
        }
        catch (Exception ex)
        {
            string? recoveryFailure = null;
            if (launcherStartAttempted && launcherProvider != null)
            {
                try
                {
                    if (!await launcherProvider.StopAsync(profile, CancellationToken.None).ConfigureAwait(false))
                        recoveryFailure = $"launcher '{launcherProvider.Id}' cleanup failed";
                }
                catch (Exception cleanupException)
                {
                    recoveryFailure = $"launcher '{launcherProvider.Id}' cleanup failed: {cleanupException.Message}";
                }
            }
            if (inputStartAttempted && inputProvider != null)
            {
                try
                {
                    if (!await inputProvider.StopAsync(profile, CancellationToken.None).ConfigureAwait(false))
                        recoveryFailure ??= $"input provider '{inputProvider.Id}' cleanup failed";
                }
                catch (Exception cleanupException)
                {
                    recoveryFailure ??= $"input provider '{inputProvider.Id}' cleanup failed: {cleanupException.Message}";
                }
            }
            if (resourcesMayHaveBeenReleased)
            {
                // Rolling back a failed entry lifts the hard-off, but reloading the runtime is a
                // resume and follows the explicit profile policy, not an unconditional reload.
                try { await RestoreAiResourcesAsync(CancellationToken.None, profile.RestoreAiOnExit, profile.RestoreAiOnExit).ConfigureAwait(false); }
                catch (Exception recoveryException)
                {
                    recoveryFailure ??= $"AI resource recovery failed: {recoveryException.Message}";
                }
            }

            lock (_stateGate)
            {
                _lastErrorMessage = "Failed to enter gaming: " + ex.Message;
                if (recoveryFailure != null) _lastErrorMessage += "; " + recoveryFailure;
                _activeProfile = null;
                _activeLauncherId = null;
                _activeInputId = null;
                _activeTriggerId = null;
                _sessionStartedUtc = null;
                _remoteDisconnectUtc = null;
                SetState(recoveryFailure == null ? "AiReady" : "Error");
            }
            return false;
        }
    }

    private async Task<bool> TransitionToAiAsync(bool userInitiated, CancellationToken ct)
    {
        GamingProfileDto profile;
        lock (_stateGate)
        {
            // "AiReady" is only really ready when the runtime agrees: after a restart during a
            // gaming session the runtime's persisted gaming flag stayed on while this state said
            // AiReady, and Return to AI returned here without clearing it (live test: every AI
            // request refused, no way out from the UI).
            if (_state == "RestoringAi" || (_state == "AiReady" && !_runtime.GamingMode)) return true;
            SetState("ExitingGaming");
            profile = _activeProfile ?? _profileStore.GetActiveOrDefault();
        }

        try
        {
            // 1. Check Quick Resume / Suspend policy if game is running
            if (!string.Equals(profile.QuickResumePolicy, "off", StringComparison.OrdinalIgnoreCase))
            {
                int? activeGamePid = FindActiveGameProcessId(SessionStartedUtc());
                if (activeGamePid.HasValue)
                {
                    IGameSuspendProvider suspendProvider = ResolveSuspendProvider(profile.SuspendProvider);
                    bool suspended = await suspendProvider.SuspendProcessAsync(activeGamePid.Value, ct).ConfigureAwait(false);
                    if (suspended)
                    {
                        long? vram = await MeasureProcessVramMibAsync(activeGamePid.Value).ConfigureAwait(false);
                        lock (_stateGate)
                        {
                            _suspendedProcessId = activeGamePid.Value;
                            try { _suspendedProcessName = Process.GetProcessById(activeGamePid.Value).ProcessName; } catch { _suspendedProcessName = "GameProcess"; }
                            _suspendedVramMib = vram;
                        }
                    }
                }
            }

            // 2. Stop input helper
            IInputProvider inputProvider = ResolveInputProvider(profile.InputProvider);
            await inputProvider.StopAsync(profile, ct).ConfigureAwait(false);

            // 3. Stop launcher if applicable
            IGameLauncherProvider launcherProvider = ResolveLauncherProvider(profile.LauncherProvider);
            await launcherProvider.StopAsync(profile, ct).ConfigureAwait(false);

            // 4. Restore AI resources
            lock (_stateGate) SetState("RestoringAi");
            await RestoreAiResourcesAsync(ct, userInitiated || profile.RestoreAiOnExit, profile.RestoreAiOnExit).ConfigureAwait(false);

            lock (_stateGate)
            {
                _activeProfile = null;
                _activeLauncherId = null;
                _activeInputId = null;
                _activeTriggerId = null;
                _remoteDisconnectUtc = null;
                _sessionStartedUtc = null;
                SetState(_suspendedProcessId.HasValue ? "GameSuspended" : "AiReady");
            }

            return true;
        }
        catch (Exception ex)
        {
            lock (_stateGate)
            {
                _lastErrorMessage = "Failed to restore AI: " + ex.Message;
                SetState("Error");
            }
            return false;
        }
    }

    public async Task ReleaseResourcesForGamingAsync(CancellationToken ct)
    {
        _runtime.SetGamingState(true);
        _jobs.CancelAllForGaming();
        _speech.StopWorkersForGaming();
        _cancelEvaluations?.Invoke();

        string unloadedLabel = TextModelUnloadedLabel(new GpuInventoryService().GetStatus());
        lock (_stateGate)
        {
            _suspendedAiWorkloads.Clear();
            _suspendedAiWorkloads.Add(unloadedLabel);
            _suspendedAiWorkloads.Add("ComfyUI media engine stopped");
            _suspendedAiWorkloads.Add("Speech TTS/STT workers stopped");
        }

        // A runtime that did not stop still holds VRAM the game needs: report it instead of calling the hard-off done.
        // The Gaming guard keeps retrying the release while Gaming Mode stays on.
        if (!await _runtime.ForceGamingShutdownAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Gaming Mode is on, but a local AI runtime did not stop and still holds resources; PaRuX keeps retrying the release.");
    }

    /// <summary>
    /// The runtime persists Gaming Mode across restarts (PaRuX must not reload AI mid-game), but
    /// this orchestrator's state is in memory and started as AiReady, so the UI claimed "AI READY"
    /// while every AI request was refused. Adopt the persisted state instead.
    /// </summary>
    internal void ReconcileWithPersistedRuntimeState()
    {
        if (!_runtime.GamingMode) return;
        lock (_stateGate)
        {
            if (_state != "AiReady") return;
            _sessionStartedUtc ??= DateTime.UtcNow;
            SetState("GamingLocal");
        }
    }

    /// <summary>
    /// Lifts the Gaming hard-off. <paramref name="reloadRuntime"/> is true only for an explicit
    /// user "Return to AI" or an explicit RestoreAiOnExit profile policy; otherwise the runtime
    /// stays stopped and loads lazily on the user's next request. Nothing is ever downloaded,
    /// benchmarked or tuned here.
    /// </summary>
    public async Task RestoreAiResourcesAsync(CancellationToken ct, bool reloadRuntime = true, bool resumePausedWork = false)
    {
        await _runtime.EndGamingAsync(ct).ConfigureAwait(false);
        _runtime.SetGamingState(false);
        if (reloadRuntime)
        {
            // GameStop intentionally leaves the local engines stopped. Bring the configured text
            // runtime back before claiming AI is ready.
            await _restoreRuntimeToReady(ct).ConfigureAwait(false);
            if (!await _runtimeHealthCheck(ct).ConfigureAwait(false))
                throw new InvalidOperationException("The AI runtime did not become healthy after leaving Gaming Mode.");
        }
        lock (_stateGate)
        {
            _suspendedAiWorkloads.Clear();
        }
        // Downloads paused for Gaming resume automatically only under the explicit policy;
        // otherwise they stay paused for a manual resume. A resume failure never fails the exit.
        if (resumePausedWork)
        {
            foreach (IGamingResumable resumable in _resumables)
            {
                try { await resumable.ResumeGamingPausedAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Could not resume work paused for Gaming"); }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReconcileWithPersistedRuntimeState();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollTriggersAndSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in WorkloadOrchestrator background loop");
            }

            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollTriggersAndSessionAsync(CancellationToken ct)
    {
        string currentState;
        GamingProfileDto profile;
        lock (_stateGate)
        {
            currentState = _state;
            profile = _activeProfile ?? _profileStore.GetActiveOrDefault();
        }

        // 1. If currently in Remote Gaming session, check for Sunshine disconnect & grace period
        if (currentState == "GamingRemote")
        {
            IGamingTriggerProvider sunshineTrigger = _triggerProviders.First(t => t.Id == "sunshine");
            bool isTriggered = await sunshineTrigger.IsTriggeredAsync(profile, ct).ConfigureAwait(false);
            if (!isTriggered)
            {
                lock (_stateGate)
                {
                    _remoteDisconnectUtc ??= DateTime.UtcNow;
                    TimeSpan disconnectedDuration = DateTime.UtcNow - _remoteDisconnectUtc.Value;
                    if (disconnectedDuration.TotalSeconds >= profile.GracePeriodSeconds)
                    {
                        // Grace period expired without reconnect: exit gaming session
                        _ = ExitGamingSessionAutomaticallyAsync(ct);
                    }
                }
            }
            else
            {
                lock (_stateGate)
                {
                    _remoteDisconnectUtc = null; // Reconnected! Reset grace period timer
                }
            }
            return;
        }

        // 2. If in AiReady state, check if any enabled trigger provider triggers a session
        if (currentState == "AiReady")
        {
            IReadOnlyList<GamingProfileDto> profiles = _profileStore.List();
            foreach (GamingProfileDto p in profiles.Where(p => p.Enabled))
            {
                IGamingTriggerProvider? trigger = _triggerProviders.FirstOrDefault(t => t.Id.Equals(p.TriggerProvider, StringComparison.OrdinalIgnoreCase));
                if (trigger != null && trigger.IsDetected)
                {
                    bool triggered = await trigger.IsTriggeredAsync(p, ct).ConfigureAwait(false);
                    if (triggered)
                    {
                        _ = TransitionToGamingAsync(p, trigger.Id, ct);
                        break;
                    }
                }
            }
        }
    }

    private void SetState(string newState)
    {
        _state = newState;
        _lastStateChangeUtc = DateTime.UtcNow;
    }

    private static string GetStateDescription(string state) => state switch
    {
        "AiReady" => "PaRuX local AI server is active and ready.",
        "EnteringGaming" => "Releasing AI GPU memory and preparing gaming session...",
        "GamingLocal" => "Local gaming session active.",
        "GamingRemote" => "Remote Sunshine streaming session active.",
        "ExitingGaming" => "Stopping gaming helpers and restoring AI...",
        "GameSuspended" => "Game process is suspended in RAM.",
        "RestoringAi" => "Restoring local AI engines and profiles...",
        "Error" => "Orchestrator error.",
        _ => "Idle"
    };

    private DateTime? SessionStartedUtc() { lock (_stateGate) return _sessionStartedUtc; }

    internal sealed record GameProcessCandidate(int Id, string Name, string? Path, DateTime StartUtc, bool HasWindow);

    /// <summary>
    /// Processes that must never be treated as "the game" (and so never suspended): PaRuX itself,
    /// launchers, shells, browsers, terminals, editors, office and communication apps.
    /// </summary>
    private static readonly HashSet<string> NeverTheGame = new(StringComparer.OrdinalIgnoreCase)
    {
        "parux", "parux.server", "llama-server", "python", "pythonw",
        "playnite", "playnite.desktopapp", "playnite.fullscreenapp", "playniteui", "steam", "steamwebhelper",
        "galaxyclient", "epicgameslauncher", "eadesktop", "ubisoftconnect", "battle.net", "xboxpcapp",
        "explorer", "cmd", "powershell", "pwsh", "windowsterminal", "conhost", "openconsole",
        "chrome", "msedge", "firefox", "brave", "opera", "code", "devenv", "rider64", "idea64",
        "winword", "excel", "powerpnt", "outlook", "olk", "onenote", "teams", "ms-teams", "slack", "discord",
        "spotify", "notepad", "notepad++", "claude", "chatgpt", "obs64", "sunshine", "moonlight"
    };

    /// <summary>
    /// The game is a windowed process started after the gaming session began that is neither a
    /// Windows component nor a known desktop/launcher app; the foreground one wins, otherwise the
    /// most recently started. When nothing qualifies there is no game: suspending a guess is far
    /// worse than suspending nothing. (The previous rule, "first process with a window title not
    /// in a short launcher list", picked C:\Windows\SystemApps\...\TextInputHost.exe in a live
    /// test and would have frozen it with the default Windows suspend provider.)
    /// </summary>
    internal static int? SelectGameProcess(IEnumerable<GameProcessCandidate> candidates, DateTime? sessionStartedUtc, int? foregroundProcessId)
    {
        if (sessionStartedUtc is not DateTime started) return null;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        List<GameProcessCandidate> eligible = candidates
            .Where(c => c.HasWindow
                && c.StartUtc >= started.AddSeconds(-2)
                && !NeverTheGame.Contains(c.Name)
                && !string.IsNullOrWhiteSpace(c.Path)
                && (windows.Length == 0 || !c.Path!.StartsWith(windows, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        GameProcessCandidate? foreground = eligible.FirstOrDefault(c => c.Id == foregroundProcessId);
        return (foreground ?? eligible.OrderByDescending(c => c.StartUtc).FirstOrDefault())?.Id;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static int? FindActiveGameProcessId(DateTime? sessionStartedUtc)
    {
        Func<int?>? overrideFinder = ActiveGameProcessFinderOverride.Value;
        if (overrideFinder != null) return overrideFinder();

        if (!OperatingSystem.IsWindows() || sessionStartedUtc == null) return null;
        var candidates = new List<GameProcessCandidate>();
        foreach (Process p in Process.GetProcesses())
        {
            try
            {
                using (p)
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* protected process: no path, never eligible */ }
                    candidates.Add(new GameProcessCandidate(p.Id, p.ProcessName, path, p.StartTime.ToUniversalTime(), true));
                }
            }
            catch { /* exited or access denied */ }
        }
        int? foreground = null;
        try
        {
            IntPtr window = GetForegroundWindow();
            if (window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint pid) != 0) foreground = (int)pid;
        }
        catch { }
        return SelectGameProcess(candidates, sessionStartedUtc, foreground);
    }

    /// <summary>
    /// VRAM held by a process, in MiB, or null when it cannot be read. This used to return the working
    /// set, which is system RAM, and the gaming figures reported it as VRAM. Only nvidia-smi reports
    /// per-process GPU memory, and it does not always do so on WDDM, so unknown is a normal answer.
    /// Callers must not substitute another number.
    /// </summary>
    private async Task<long?> MeasureProcessVramMibAsync(int pid)
    {
        // Without an NVIDIA card there is no per-process GPU figure, so nvidia-smi is not spawned at all.
        if (await _processManager.GetVramMibAsync(CancellationToken.None).ConfigureAwait(false) <= 0) return null;
        try
        {
            return await NvidiaSmiProbe.ComputeAppMemoryMibAsync(
                NvidiaSmiProbe.RunProcessAsync, pid, NvidiaSmiProbe.Timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch { return null; }
    }

    private IGamingTriggerProvider ResolveTriggerProvider(string id) =>
        _triggerProviders.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw UnknownProvider("trigger", id);

    private IGameLauncherProvider ResolveLauncherProvider(string id) =>
        _launcherProviders.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw UnknownProvider("launcher", id);

    private IInputProvider ResolveInputProvider(string id) =>
        _inputProviders.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw UnknownProvider("input", id);

    private IGameSuspendProvider ResolveSuspendProvider(string id) =>
        _suspendProviders.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw UnknownProvider("suspend", id);

    private static InvalidOperationException UnknownProvider(string category, string id) =>
        new($"Gaming {category} provider '{id}' is not registered.");
}
