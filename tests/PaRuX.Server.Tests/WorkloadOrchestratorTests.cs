using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaRuX.Contracts;
using PaRuX.Server;
using PaRuX.Server.Orchestration.Gaming;
using PaRuX.Server.Orchestration.Gaming.Providers;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}

public sealed class WorkloadOrchestratorTests
{
    [Fact]
    public void GamingProfileStore_BuildsDefaultPresets()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new GamingProfileStore(tempPath);
            var profiles = store.List();

            Assert.NotEmpty(profiles);
            Assert.Contains(profiles, p => p.Name.Contains("Local Console", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(profiles, p => p.Name.Contains("Remote", StringComparison.OrdinalIgnoreCase));
            GamingProfileDto local = Assert.Single(profiles.Where(p => p.Id == "preset-local-console"));
            Assert.Equal("native", local.InputProvider);
            Assert.Equal("nyrna", local.SuspendProvider);
            GamingProfileDto remote = Assert.Single(profiles.Where(p => p.Id == "preset-remote-sunshine"));
            Assert.Equal("nyrna", remote.SuspendProvider);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void GamingProfileStore_MigratesUnavailableProvidersFromBuiltInPresetsOnly()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new[]
            {
                new GamingProfileDto
                {
                    Id = "preset-local-console", Name = "Local Console", InputProvider = "xoutput", SuspendProvider = "nyrna"
                },
                new GamingProfileDto
                {
                    Id = "preset-remote-sunshine", Name = "Remote", InputProvider = "sunshine", SuspendProvider = "nyrna"
                },
                new GamingProfileDto
                {
                    Id = "user-custom-profile", Name = "My Profile", InputProvider = "xoutput", SuspendProvider = "nyrna"
                }
            }));

            var store = new GamingProfileStore(tempPath);
            Assert.Equal("native", store.Get("preset-local-console")!.InputProvider);
            Assert.Equal("nyrna", store.Get("preset-local-console")!.SuspendProvider);
            Assert.Equal("nyrna", store.Get("preset-remote-sunshine")!.SuspendProvider);
            Assert.Equal("xoutput", store.Get("user-custom-profile")!.InputProvider);
            Assert.Equal("nyrna", store.Get("user-custom-profile")!.SuspendProvider);
            string persisted = File.ReadAllText(tempPath);
            Assert.Contains("\"InputProvider\": \"native\"", persisted, StringComparison.Ordinal);
            Assert.Contains("\"SuspendProvider\": \"nyrna\"", persisted, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void GamingProviders_DetectStoreNyrnaAndInstalledSunshineExecutables()
    {
        string root = Path.Combine(Path.GetTempPath(), "parux-gaming-providers-" + Guid.NewGuid().ToString("N"));
        string appData = Path.Combine(root, "AppData");
        string programFiles = Path.Combine(root, "ProgramFiles");
        string nyrnaDirectory = Path.Combine(programFiles, "StorePackages", "33694MerrittCodes.Nyrna_2.27.2.0_x64__test");
        string sunshineDirectory = Path.Combine(programFiles, "Sunshine");
        try
        {
            Directory.CreateDirectory(nyrnaDirectory);
            Directory.CreateDirectory(sunshineDirectory);
            string nyrnaPath = Path.Combine(nyrnaDirectory, "nyrna.exe");
            string sunshinePath = Path.Combine(sunshineDirectory, "sunshine.exe");
            File.WriteAllBytes(nyrnaPath, Array.Empty<byte>());
            File.WriteAllBytes(sunshinePath, Array.Empty<byte>());

            Assert.Equal(nyrnaPath, NyrnaGameSuspendProvider.FindExecutable(appData, programFiles, new[] { nyrnaDirectory }));
            Assert.Equal(sunshinePath, SunshineSessionTriggerProvider.FindExecutable(appData, programFiles));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task GamingProviders_DoNotStopPreexistingXOutputOrPlayniteProcesses()
    {
        string currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process executable path is unavailable.");
        using Process currentProcess = Process.GetCurrentProcess();

        var inputProfile = new GamingProfileDto { SessionType = "local", InputProvider = "xoutput" };
        inputProfile.InputSettings["path"] = currentExecutable;
        var xoutput = new XOutputInputProvider(() => new[] { Process.GetCurrentProcess() });
        Assert.True(await xoutput.StartAsync(inputProfile, CancellationToken.None));
        Assert.True(await xoutput.StopAsync(inputProfile, CancellationToken.None));
        Assert.False(currentProcess.HasExited);

        var launcherProfile = new GamingProfileDto();
        launcherProfile.LauncherSettings["path"] = currentExecutable;
        var playnite = new PlayniteLauncherProvider(() => new[] { Process.GetCurrentProcess() });
        Assert.True(await playnite.LaunchAsync(launcherProfile, CancellationToken.None));
        Assert.True(await playnite.StopAsync(launcherProfile, CancellationToken.None));
        Assert.False(currentProcess.HasExited);
    }

    [Fact]
    public void GamingProfileStore_CanSaveAndDeleteCustomProfile()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new GamingProfileStore(tempPath);
            var custom = new GamingProfileDto
            {
                Name = "Test Custom Gaming Profile",
                SessionType = "local",
                LauncherProvider = "steam",
                QuickResumePolicy = "always",
                GracePeriodSeconds = 45
            };

            var saved = store.Save(custom);
            Assert.NotNull(saved.Id);

            var retrieved = store.Get(saved.Id);
            Assert.NotNull(retrieved);
            Assert.Equal("Test Custom Gaming Profile", retrieved.Name);
            Assert.Equal(45, retrieved.GracePeriodSeconds);

            bool deleted = store.Delete(saved.Id);
            Assert.True(deleted);
            Assert.Null(store.Get(saved.Id));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static WorkloadOrchestrator CreateTestOrchestrator(string profileStorePath)
        => CreateTestOrchestrator(profileStorePath, null, null, null);

    private static WorkloadOrchestrator CreateTestOrchestrator(
        string profileStorePath,
        System.Collections.Generic.IReadOnlyCollection<IInputProvider>? inputProviders,
        System.Collections.Generic.IReadOnlyCollection<IGameLauncherProvider>? launcherProviders = null,
        Func<CancellationToken, Task<bool>>? runtimeHealthCheck = null,
        Func<CancellationToken, Task>? restoreRuntimeToReady = null)
    {
        var env = new TestHostEnvironment { EnvironmentName = "Testing" };
        var processManager = new LocalAiProcessManager();
        var runtime = new LocalRuntimeService(env, processManager, new ExternalTextProviderService());
        var jobs = new MediaJobService(null!);
        var speech = new SpeechService(runtime);
        var store = new GamingProfileStore(profileStorePath);

        return new WorkloadOrchestrator(runtime, processManager, jobs, speech, store,
            inputProviders: inputProviders, launcherProviders: launcherProviders,
            runtimeHealthCheck: runtimeHealthCheck, restoreRuntimeToReady: restoreRuntimeToReady);
    }

    private sealed class MissingInputProvider : IInputProvider
    {
        public string Id => "missing-test-input";
        public string Name => "Missing Test Input";
        public bool IsInstalled => false;
        public string? ExecutablePath => null;
        public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => throw new InvalidOperationException("Unavailable provider must not start.");
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class SuccessfulInputProvider : IInputProvider
    {
        public string Id => "test-input";
        public string Name => "Test Input";
        public bool IsInstalled => true;
        public string? ExecutablePath => null;
        public Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FailedLauncherProvider : IGameLauncherProvider
    {
        public string Id => "test-launcher";
        public string Name => "Test Launcher";
        public bool IsInstalled => true;
        public string? ExecutablePath => null;
        public Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task WorkloadOrchestrator_RejectsUnknownConfiguredInputAndLauncherProviders()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new GamingProfileStore(tempPath);
            store.Save(new GamingProfileDto
            {
                Name = "Unknown Input Test",
                SessionType = "local",
                LauncherProvider = "none",
                InputProvider = "missing-input",
                SuspendProvider = "none"
            });
            store.Save(new GamingProfileDto
            {
                Name = "Unknown Launcher Test",
                SessionType = "local",
                LauncherProvider = "missing-launcher",
                InputProvider = "native",
                SuspendProvider = "none"
            });
            var orchestrator = CreateTestOrchestrator(tempPath);

            Assert.False(await orchestrator.EnterGamingSessionAsync("Unknown Input Test", CancellationToken.None));
            Assert.Contains("input provider 'missing-input' is not registered", orchestrator.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("AiReady", orchestrator.CurrentState);

            Assert.False(await orchestrator.EnterGamingSessionAsync("Unknown Launcher Test", CancellationToken.None));
            Assert.Contains("launcher provider 'missing-launcher' is not registered", orchestrator.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("AiReady", orchestrator.CurrentState);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task WorkloadOrchestrator_ReportsErrorWhenAiRuntimeIsStillUnhealthyAfterRollback()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        string tempStateDir = Path.Combine(Path.GetTempPath(), "parux-test-state-" + Guid.NewGuid().ToString("N"));
        using var scope = LocalAiPaths.UseStateDirForTests(tempStateDir);
        try
        {
            new GamingProfileStore(tempPath).Save(new GamingProfileDto
            {
                Name = "Failed Launcher Recovery Test",
                SessionType = "local",
                LauncherProvider = "test-launcher",
                InputProvider = "test-input",
                SuspendProvider = "none",
                // The rollback reloads the runtime only under the explicit resume policy
                // (see GamingTransitionIntegrationTests for the policy-off behavior).
                RestoreAiOnExit = true
            });
            var orchestrator = CreateTestOrchestrator(
                tempPath,
                new[] { new SuccessfulInputProvider() },
                new[] { new FailedLauncherProvider() },
                _ => Task.FromResult(false),
                _ => Task.CompletedTask);

            Assert.False(await orchestrator.EnterGamingSessionAsync("Failed Launcher Recovery Test", CancellationToken.None));

            Assert.Equal("Error", orchestrator.CurrentState);
            Assert.Contains("AI resource recovery failed", orchestrator.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("did not become healthy", orchestrator.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            try { if (Directory.Exists(tempStateDir)) Directory.Delete(tempStateDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WorkloadOrchestrator_RejectsUnavailableInputBeforeReleasingAiResources()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        string tempStateDir = Path.Combine(Path.GetTempPath(), "parux-test-state-" + Guid.NewGuid().ToString("N"));
        using var scope = LocalAiPaths.UseStateDirForTests(tempStateDir);
        try
        {
            new GamingProfileStore(tempPath).Save(new GamingProfileDto
            {
                Name = "Unavailable Input Test",
                SessionType = "local",
                LauncherProvider = "none",
                InputProvider = "missing-test-input",
                SuspendProvider = "none"
            });
            var orchestrator = CreateTestOrchestrator(tempPath, new[] { new MissingInputProvider() });

            bool entered = await orchestrator.EnterGamingSessionAsync("Unavailable Input Test", CancellationToken.None);

            Assert.False(entered);
            Assert.Equal("AiReady", orchestrator.CurrentState);
            Assert.False(orchestrator.IsGamingActive);
            Assert.Contains("not installed", orchestrator.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("missing-test-input", orchestrator.GetStatus().Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            try { if (Directory.Exists(tempStateDir)) Directory.Delete(tempStateDir, true); } catch { }
        }
    }

    [Fact]
    public void WorkloadOrchestrator_InitialStateIsAiReady()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var orchestrator = CreateTestOrchestrator(tempPath);

            Assert.Equal("AiReady", orchestrator.CurrentState);
            Assert.False(orchestrator.IsGamingActive);

            GamingStatusDto status = orchestrator.GetStatus();
            Assert.Equal("AiReady", status.State);
            Assert.False(status.IsGamingActive);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task WorkloadOrchestrator_CanEnterAndExitGamingSession()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        string tempStateDir = Path.Combine(Path.GetTempPath(), "parux-test-state-" + Guid.NewGuid().ToString("N"));
        using var scope = LocalAiPaths.UseStateDirForTests(tempStateDir);
        // The exit path only reaches "AiReady" when no game process is detected; otherwise it
        // suspends the "game" it found and lands in "GameSuspended" instead. The real detector
        // scans every window on the desktop running the test, so without this override the test
        // is at the mercy of whatever else happens to have a window open on that machine.
        using var gameFinderScope = WorkloadOrchestrator.UseActiveGameProcessFinderForTests(() => null);
        bool aiRuntimeRestored = false;
        try
        {
            new GamingProfileStore(tempPath).Save(new GamingProfileDto
            {
                Name = "Test Session Without Launcher",
                SessionType = "local",
                LauncherProvider = "none",
                InputProvider = "native",
                SuspendProvider = "none",
                QuickResumePolicy = "off"
            });
            var orchestrator = CreateTestOrchestrator(
                tempPath,
                null,
                null,
                _ => Task.FromResult(aiRuntimeRestored),
                _ =>
                {
                    aiRuntimeRestored = true;
                    return Task.CompletedTask;
                });

            bool entered = await orchestrator.EnterGamingSessionAsync(
                "Test Session Without Launcher", CancellationToken.None);
            Assert.True(entered);
            Assert.True(orchestrator.IsGamingActive);

            GamingStatusDto gamingStatus = orchestrator.GetStatus();
            Assert.Equal("GamingLocal", gamingStatus.State);
            Assert.Equal("none", gamingStatus.ActiveLauncher);
            Assert.Equal("native", gamingStatus.ActiveInput);

            bool exited = await orchestrator.ExitGamingSessionAsync(CancellationToken.None);
            Assert.True(exited);
            Assert.False(orchestrator.IsGamingActive);

            GamingStatusDto aiStatus = orchestrator.GetStatus();
            Assert.Equal("AiReady", aiStatus.State);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            try { if (Directory.Exists(tempStateDir)) Directory.Delete(tempStateDir, true); } catch { }
        }
    }

    [Fact]
    public void WorkloadOrchestrator_ReturnsProviderStatuses()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var orchestrator = CreateTestOrchestrator(tempPath);
            var providers = orchestrator.GetProviderStatuses();

            Assert.NotEmpty(providers);
            Assert.Contains(providers, p => p.Id == "playnite");
            Assert.Contains(providers, p => p.Id == "steam");
            Assert.Contains(providers, p => p.Id == "xoutput");
            Assert.Contains(providers, p => p.Id == "sunshine");
            Assert.Contains(providers, p => p.Id == "nyrna");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void WorkloadOrchestrator_DiConstructionRetainsDefaultLauncherAndInputProviders()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "parux-test-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var env = new TestHostEnvironment { EnvironmentName = "Testing" };
            var processManager = new LocalAiProcessManager();
            var runtime = new LocalRuntimeService(env, processManager, new ExternalTextProviderService());
            var jobs = new MediaJobService(null!);
            var speech = new SpeechService(runtime);
            var store = new GamingProfileStore(tempPath);
            var services = new ServiceCollection();
            services.AddSingleton(processManager);
            services.AddSingleton(runtime);
            services.AddSingleton(jobs);
            services.AddSingleton(speech);
            services.AddSingleton(store);
            services.AddSingleton<WorkloadOrchestrator>();

            using var provider = services.BuildServiceProvider();
            WorkloadOrchestrator orchestrator = provider.GetRequiredService<WorkloadOrchestrator>();
            var statuses = orchestrator.GetProviderStatuses();

            Assert.Contains(statuses, p => p.Category == "launcher" && p.Id == "none");
            Assert.Contains(statuses, p => p.Category == "input" && p.Id == "xoutput");
            Assert.Contains(statuses, p => p.Category == "input" && p.Id == "sunshine");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
