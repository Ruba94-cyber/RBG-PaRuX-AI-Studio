using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class OnboardingAutoSetupTests
    {
        private const string Rev = "0123456789abcdef0123456789abcdef01234567";
        private const long Gb = 1024L * 1024 * 1024;

        private sealed class FakeHost : IAutoSetupHost
        {
            public FirstModelAdvice Advice = MakeAdvice("FitsOnGpu");
            public AutoSetupRuntimeInfo Runtime = new() { Detected = true, ComponentId = "llama-server-vulkan", Ownership = "PARUX_MANAGED", Version = "b10549" };
            public bool ConfirmRuntime = true;
            public int RuntimeConfirmCalls, RuntimeInstallCalls, LicenceCalls, AssignCalls, VerifyCalls, PlanCalls, CancelCalls;
            public List<ModelQuantChoice> Choices = new()
            {
                Choice("Q8_0", 8 * Gb), Choice("Q4_K_M", 4 * Gb), Choice("Q2_K", 2 * Gb)
            };
            public Func<long, AutoSetupFit> Fit = bytes => bytes <= 6 * Gb ? AutoSetupFit.Fits : AutoSetupFit.TooBig;
            public bool PlanInstalled;
            public bool LicenceAccepted = true;
            public Queue<List<ModelDownloadStatus>> Polls = new();
            public List<ModelDownloadStatus> LastPoll = new();
            public Func<CancellationToken, Task>? OnPoll;
            public RoleAutoAssignResponse Roles = new() { Success = true, Summary = "ok" };
            public string? VerifyFailure;
            public ModelQuantChoice? ChosenForPlan;

            public Task<FirstModelAdvice> GetAdviceAsync(CancellationToken ct) => Task.FromResult(Advice);
            public Task<AutoSetupRuntimeInfo> GetTextRuntimeAsync(CancellationToken ct) => Task.FromResult(Runtime);
            public Task<bool> ConfirmRuntimeInstallAsync(AutoSetupRuntimeOffer offer, CancellationToken ct)
            { RuntimeConfirmCalls++; return Task.FromResult(ConfirmRuntime); }
            public Task InstallRuntimeAsync(AutoSetupRuntimeOffer offer, IProgress<InstallProgressPresentation> progress, CancellationToken ct)
            { RuntimeInstallCalls++; return Task.CompletedTask; }
            public Task<IReadOnlyList<ModelQuantChoice>> GetQuantChoicesAsync(string repoId, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<ModelQuantChoice>>(Choices);
            public Task<AutoSetupFit> EstimateFitAsync(long totalBytes, CancellationToken ct) => Task.FromResult(Fit(totalBytes));
            public Task<ModelDownloadPlan> GetPlanAsync(string repoId, ModelQuantChoice choice, CancellationToken ct)
            {
                PlanCalls++;
                ChosenForPlan = choice;
                return Task.FromResult(new ModelDownloadPlan
                {
                    RepoId = repoId, Revision = Rev, SetName = choice.SetName, Quantization = choice.Quantization, PartCount = 1,
                    Parts = new List<ModelDownloadPlanFile> { new() { Index = 1, FileName = choice.SetName + ".gguf", SizeBytes = choice.SizeBytes } }
                });
            }
            public Task<bool> IsPlanInstalledAsync(ModelDownloadPlan plan, CancellationToken ct) => Task.FromResult(PlanInstalled);
            public Task<IReadOnlyList<string>?> ReviewLicenceAndStartAsync(ModelDownloadPlan plan, ModelQuantChoice choice, CancellationToken ct)
            {
                LicenceCalls++;
                return Task.FromResult<IReadOnlyList<string>?>(LicenceAccepted ? new[] { "dl-1" } : null);
            }
            public async Task<IReadOnlyList<ModelDownloadStatus>> ListDownloadsAsync(CancellationToken ct)
            {
                if (OnPoll != null) await OnPoll(ct);
                if (Polls.Count > 0) LastPoll = Polls.Dequeue();
                return LastPoll;
            }
            public Task CancelDownloadsAsync(IReadOnlyList<string> ids) { CancelCalls++; return Task.CompletedTask; }
            public Task<RoleAutoAssignResponse> AutoAssignRolesAsync(CancellationToken ct) { AssignCalls++; return Task.FromResult(Roles); }
            public Task<string?> VerifyChatAsync(CancellationToken ct) { VerifyCalls++; return Task.FromResult(VerifyFailure); }
        }

        private static ModelQuantChoice Choice(string quant, long size) => new()
        {
            Quantization = quant, SetName = "m-" + quant, Complete = true, PartCount = 1, SizeBytes = size
        };

        private static FirstModelAdvice MakeAdvice(string suitability) => new()
        {
            HardwareSummary = "fixture hw",
            RecommendedId = "m1",
            Candidates = new List<FirstModelCandidate>
            {
                new()
                {
                    Suitability = suitability, Rank = 1, Reason = "r",
                    Model = new RecommendedModel
                    {
                        Id = "m1", Title = "Fixture 7B", HuggingFaceRepoId = "fixture/general", QuantHint = "Q4_K_M",
                        EstimatedSizeBytes = 4 * Gb, CapabilityIds = new List<string> { ModelCapabilityIds.GeneralChat }
                    }
                }
            }
        };

        private static ModelDownloadStatus Dl(string status, long done, long total, string? error = null) => new()
        {
            Id = "dl-1", Status = status, FileName = "m.gguf", BytesDownloaded = done, TotalBytes = total, Error = error
        };

        private static OnboardingAutoSetupService Make(FakeHost host, IAutoSetupStateStore? store = null) =>
            new(host, store ?? new InMemoryAutoSetupStateStore(), delay: (_, _) => Task.CompletedTask);

        [Fact]
        public async Task HappyPath_RunsEveryStepInOrder_AndOnlyThenIsReady()
        {
            var host = new FakeHost();
            host.Polls.Enqueue(new() { Dl("downloading", Gb, 4 * Gb) });
            host.Polls.Enqueue(new() { Dl("completed", 4 * Gb, 4 * Gb) });
            var service = Make(host);
            var seen = new List<AutoSetupPhase>();
            service.Changed += s => { if (seen.LastOrDefault() != s.State.Phase) seen.Add(s.State.Phase); };

            AutoSetupState result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Ready, result.Phase);
            Assert.Equal("Q4_K_M", result.Quantization);
            Assert.Equal(Rev, result.Revision);
            Assert.Equal(1, host.LicenceCalls);
            Assert.Equal(1, host.AssignCalls);
            Assert.Equal(1, host.VerifyCalls);
            Assert.Equal(0, host.RuntimeInstallCalls);
            Assert.True(seen.IndexOf(AutoSetupPhase.Downloading) < seen.IndexOf(AutoSetupPhase.AssigningRoles));
            Assert.True(seen.IndexOf(AutoSetupPhase.AssigningRoles) < seen.IndexOf(AutoSetupPhase.VerifyingChat));
            Assert.True(seen.IndexOf(AutoSetupPhase.VerifyingChat) < seen.IndexOf(AutoSetupPhase.Ready));
        }

        [Fact]
        public async Task ChatVerificationFailure_IsNeverReady_AndRetryResumesAtVerification()
        {
            var host = new FakeHost { PlanInstalled = true, VerifyFailure = "The local model did not repeat the verification code." };
            var service = Make(host);

            AutoSetupState first = await service.RunAsync(CancellationToken.None);
            Assert.Equal(AutoSetupPhase.Failed, first.Phase);
            Assert.Equal(AutoSetupPhase.VerifyingChat, first.FailedPhase);
            Assert.Contains("did not repeat", first.Message);

            host.VerifyFailure = null;
            Assert.True(service.TryPrepareRetry());
            AutoSetupState second = await service.RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Ready, second.Phase);
            Assert.Equal(1, host.AssignCalls);   // not repeated: retry resumed at the failed step
            Assert.Equal(2, host.VerifyCalls);
        }

        [Fact]
        public async Task DownloadFailure_ShowsHonestError_ThenRetryRestartsSelectionAndSucceeds()
        {
            var host = new FakeHost();
            host.Polls.Enqueue(new() { Dl("failed", Gb, 4 * Gb, "disk full") });
            var service = Make(host);

            AutoSetupState failed = await service.RunAsync(CancellationToken.None);
            Assert.Equal(AutoSetupPhase.Failed, failed.Phase);
            Assert.Equal(AutoSetupPhase.Downloading, failed.FailedPhase);
            Assert.Contains("disk full", failed.Message);
            Assert.Equal(0, host.VerifyCalls);

            host.Polls.Enqueue(new() { Dl("completed", 4 * Gb, 4 * Gb) });
            Assert.True(service.TryPrepareRetry());
            AutoSetupState ok = await service.RunAsync(CancellationToken.None);
            Assert.Equal(AutoSetupPhase.Ready, ok.Phase);
        }

        [Fact]
        public async Task Retries_AreBoundedPerPhase()
        {
            var host = new FakeHost { PlanInstalled = true, VerifyFailure = "nope" };
            var service = Make(host);
            await service.RunAsync(CancellationToken.None);

            for (int i = 0; i < OnboardingAutoSetupService.MaxRetriesPerPhase; i++)
            {
                Assert.True(service.TryPrepareRetry());
                await service.RunAsync(CancellationToken.None);
            }

            Assert.False(service.TryPrepareRetry());
            Assert.Equal(AutoSetupPhase.Failed, service.State.Phase);
            Assert.Contains("manual", service.State.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Cancel_DuringDownload_CancelsTransfer_AndNeverReaches_Ready()
        {
            var host = new FakeHost();
            using var cts = new CancellationTokenSource();
            host.Polls.Enqueue(new() { Dl("downloading", Gb, 4 * Gb) });
            host.OnPoll = _ => { cts.Cancel(); return Task.CompletedTask; };
            var service = new OnboardingAutoSetupService(host, new InMemoryAutoSetupStateStore(),
                delay: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

            AutoSetupState result = await service.RunAsync(cts.Token);

            Assert.Equal(AutoSetupPhase.Cancelled, result.Phase);
            Assert.Equal(1, host.CancelCalls);
            Assert.Empty(result.DownloadIds);
            Assert.Equal(0, host.AssignCalls);
            Assert.Equal(0, host.VerifyCalls);

            // The user can start again from the top after a cancel.
            host.OnPoll = null;
            host.Polls.Enqueue(new() { Dl("completed", 4 * Gb, 4 * Gb) });
            service.Start();
            Assert.Equal(AutoSetupPhase.CheckingHardware, service.State.Phase);
            Assert.Equal(AutoSetupPhase.Ready, (await service.RunAsync(CancellationToken.None)).Phase);
        }

        [Fact]
        public async Task DecliningTheLicenceReview_DownloadsNothing()
        {
            var host = new FakeHost { LicenceAccepted = false };
            AutoSetupState result = await Make(host).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Cancelled, result.Phase);
            Assert.Contains("licence", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, host.AssignCalls);
        }

        [Fact]
        public async Task NoModelFits_TooLargeCandidates_DownloadNothingAndLeaveManualPath()
        {
            var host = new FakeHost { Advice = MakeAdvice("TooLarge") };
            AutoSetupState result = await Make(host).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.NoModelFits, result.Phase);
            Assert.Equal(0, host.PlanCalls);
            Assert.Equal(0, host.LicenceCalls);
            Assert.Contains("Model Hub", result.Message);
            AutoSetupViewModel vm = OnboardingAutoSetupPresenter.Present(result);
            Assert.True(vm.ShowSkip);
            Assert.True(vm.PrimaryEnabled);
        }

        [Fact]
        public async Task NoModelFits_WhenNoQuantisationFitsTheMemory()
        {
            var host = new FakeHost { Fit = _ => AutoSetupFit.TooBig };
            AutoSetupState result = await Make(host).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.NoModelFits, result.Phase);
            Assert.Equal(0, host.LicenceCalls);
        }

        [Fact]
        public async Task UserManagedRuntime_IsNeverOfferedAnythingOrChanged()
        {
            var host = new FakeHost { PlanInstalled = true };
            host.Runtime = new AutoSetupRuntimeInfo
            {
                Detected = true, ComponentId = "llama-server-cuda", Ownership = "USER_MANAGED",
                Offer = new AutoSetupRuntimeOffer { ComponentId = "llama-server-cuda", Title = "Newer CUDA build", Reason = "faster" }
            };

            AutoSetupState result = await Make(host).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Ready, result.Phase);
            Assert.Equal(0, host.RuntimeConfirmCalls);
            Assert.Equal(0, host.RuntimeInstallCalls);
            Assert.Contains("user-managed", result.RuntimeNote, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task BetterBackend_IsInstalledOnlyAfterExplicitConfirmation()
        {
            var offer = new AutoSetupRuntimeOffer { ComponentId = "llama-server-cuda", Title = "CUDA runtime", Reason = "NVIDIA GPU" };

            var declined = new FakeHost { PlanInstalled = true, ConfirmRuntime = false };
            declined.Runtime = new AutoSetupRuntimeInfo { Detected = true, Ownership = "PARUX_MANAGED", Offer = offer };
            AutoSetupState keep = await Make(declined).RunAsync(CancellationToken.None);
            Assert.Equal(AutoSetupPhase.Ready, keep.Phase);
            Assert.Equal(1, declined.RuntimeConfirmCalls);
            Assert.Equal(0, declined.RuntimeInstallCalls);

            var accepted = new FakeHost { PlanInstalled = true, ConfirmRuntime = true };
            accepted.Runtime = new AutoSetupRuntimeInfo { Detected = true, Ownership = "PARUX_MANAGED", Offer = offer };
            await Make(accepted).RunAsync(CancellationToken.None);
            Assert.Equal(1, accepted.RuntimeInstallCalls);
        }

        [Fact]
        public async Task MissingRuntime_NotConfirmed_FailsHonestlyInsteadOfInstallingSilently()
        {
            var host = new FakeHost { ConfirmRuntime = false };
            host.Runtime = new AutoSetupRuntimeInfo
            {
                Detected = false,
                Offer = new AutoSetupRuntimeOffer { ComponentId = "llama-server-vulkan", Title = "Vulkan runtime", Required = true }
            };

            AutoSetupState result = await Make(host).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Failed, result.Phase);
            Assert.Equal(AutoSetupPhase.CheckingRuntime, result.FailedPhase);
            Assert.Equal(0, host.RuntimeInstallCalls);
        }

        private sealed class SnapshotStore : IAutoSetupStateStore
        {
            public AutoSetupState? AtDownloading;
            public AutoSetupState? Load() => null;
            public void Save(AutoSetupState state) { if (state.Phase == AutoSetupPhase.Downloading) AtDownloading = state.Clone(); }
            public void Clear() { }
        }

        [Fact]
        public async Task Restart_MidDownload_ResumesFromPersistedState_WithoutReplanning()
        {
            // First "process": runs until the download is under way; we keep what it had persisted then.
            var recorder = new SnapshotStore();
            var host = new FakeHost();
            host.Polls.Enqueue(new() { Dl("completed", 4 * Gb, 4 * Gb) });
            await new OnboardingAutoSetupService(host, recorder, delay: (_, _) => Task.CompletedTask).RunAsync(CancellationToken.None);
            Assert.NotNull(recorder.AtDownloading);
            Assert.Equal(new[] { "dl-1" }, recorder.AtDownloading!.DownloadIds);
            Assert.Equal(1, host.LicenceCalls);

            // Second process starts from exactly that persisted state.
            var store = new InMemoryAutoSetupStateStore();
            store.Save(recorder.AtDownloading);
            var host2 = new FakeHost();
            host2.Polls.Enqueue(new() { Dl("downloading", 2 * Gb, 4 * Gb) });
            host2.Polls.Enqueue(new() { Dl("completed", 4 * Gb, 4 * Gb) });
            var resumed = Make(host2, store);

            Assert.True(resumed.IsResumable);
            Assert.Equal(AutoSetupPhase.Downloading, resumed.State.Phase);
            AutoSetupState result = await resumed.RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Ready, result.Phase);
            Assert.Equal(0, host2.PlanCalls);
            Assert.Equal(0, host2.LicenceCalls);   // already reviewed and started before the restart
            Assert.Equal(1, host2.AssignCalls);
        }

        [Fact]
        public async Task Restart_WhenCoreForgotTheJob_ReplansAndFindsTheCompleteFile()
        {
            var store = new InMemoryAutoSetupStateStore();
            store.Save(new AutoSetupState
            {
                Phase = AutoSetupPhase.Downloading, ModelTitle = "Fixture 7B", TotalBytes = 4 * Gb,
                DownloadIds = new List<string> { "gone" }
            });
            var host = new FakeHost { PlanInstalled = true };   // file finished before Core restarted

            AutoSetupState result = await Make(host, store).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Ready, result.Phase);
            Assert.Equal(0, host.LicenceCalls);
        }

        [Fact]
        public async Task PersistedFailedState_SurvivesRestart_AndCanBeRetried()
        {
            var store = new InMemoryAutoSetupStateStore();
            var host = new FakeHost { PlanInstalled = true, VerifyFailure = "bad" };
            await Make(host, store).RunAsync(CancellationToken.None);

            var restarted = Make(new FakeHost { PlanInstalled = true }, store);
            Assert.Equal(AutoSetupPhase.Failed, restarted.State.Phase);
            Assert.True(restarted.TryPrepareRetry());
            Assert.Equal(AutoSetupPhase.Ready, (await restarted.RunAsync(CancellationToken.None)).Phase);
        }

        [Fact]
        public async Task Skip_LeavesManualPath_AndIsNotReady()
        {
            var service = Make(new FakeHost());
            service.Skip();

            Assert.Equal(AutoSetupPhase.Skipped, service.State.Phase);
            Assert.False(service.IsResumable);
            AutoSetupState after = await service.RunAsync(CancellationToken.None);
            Assert.Equal(AutoSetupPhase.Skipped, after.Phase);
        }

        [Fact]
        public async Task StalledDownload_FailsInsteadOfWaitingForever()
        {
            var host = new FakeHost();
            host.LastPoll = new() { Dl("downloading", Gb, 4 * Gb) };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var service = new OnboardingAutoSetupService(host, new InMemoryAutoSetupStateStore(),
                delay: (_, _) => { now = now.AddMinutes(10); return Task.CompletedTask; },
                now: () => now, stallTimeout: TimeSpan.FromMinutes(15));

            AutoSetupState result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Failed, result.Phase);
            Assert.Contains("no progress", result.Message);
        }

        [Fact]
        public async Task RoleAssignmentFailure_IsReported_AndNotReady()
        {
            var host = new FakeHost { PlanInstalled = true, Roles = new RoleAutoAssignResponse { Success = false, Summary = "no text model" } };
            AutoSetupState result = await Make(host).RunAsync(CancellationToken.None);

            Assert.Equal(AutoSetupPhase.Failed, result.Phase);
            Assert.Equal(AutoSetupPhase.AssigningRoles, result.FailedPhase);
            Assert.Equal(0, host.VerifyCalls);
        }

        [Fact]
        public void FileStore_RoundTripsState_AndIgnoresCorruptFiles()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autosetup-" + Guid.NewGuid().ToString("N"));
            string path = System.IO.Path.Combine(dir, "state.json");
            try
            {
                var store = new FileAutoSetupStateStore(path);
                Assert.Null(store.Load());
                store.Save(new AutoSetupState { Phase = AutoSetupPhase.Downloading, DownloadIds = new List<string> { "a" } });
                AutoSetupState? loaded = store.Load();
                Assert.Equal(AutoSetupPhase.Downloading, loaded!.Phase);
                Assert.Equal("a", loaded.DownloadIds.Single());
                System.IO.File.WriteAllText(path, "{not json");
                Assert.Null(store.Load());
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task Picker_PrefersHintedQuantThatFits_AndSkipsIncompleteSets()
        {
            var candidate = MakeAdvice("FitsOnGpu").Candidates[0];
            var choices = new List<ModelQuantChoice>
            {
                Choice("Q8_0", 8 * Gb),
                Choice("Q5_K_M", 5 * Gb),
                new() { Quantization = "Q4_K_M", SetName = "x", Complete = false, SizeBytes = 4 * Gb },
                Choice("Q4_K_S", 3 * Gb)
            };

            ModelQuantChoice? picked = await AutoSetupModelPicker.PickQuantAsync(choices, candidate,
                b => Task.FromResult(b <= 6 * Gb ? AutoSetupFit.Fits : AutoSetupFit.TooBig));

            Assert.Equal("Q4_K_S", picked!.Quantization);   // hinted Q4_K_M is incomplete, so next preferred
        }

        [Fact]
        public void Picker_ExcludesUncensoredAndRuntimeBlockedCandidates()
        {
            FirstModelAdvice advice = MakeAdvice("FitsOnGpu");
            advice.Candidates[0].Model.ContentProfileId = ModelContentProfileIds.Uncensored;
            Assert.Null(AutoSetupModelPicker.PickCandidate(advice));

            advice = MakeAdvice("FitsOnGpu");
            advice.Candidates[0].RuntimeRequirementState = "NeedsRuntimeUpdate";
            Assert.Null(AutoSetupModelPicker.PickCandidate(advice));
        }

        [Fact]
        public void Presenter_ShowsRetryAndSkipOnFailure_AndCancelWhileRunning()
        {
            AutoSetupViewModel failed = OnboardingAutoSetupPresenter.Present(
                new AutoSetupState { Phase = AutoSetupPhase.Failed, Message = "x" });
            Assert.True(failed.ShowRetry);
            Assert.True(failed.ShowSkip);

            AutoSetupViewModel running = OnboardingAutoSetupPresenter.Present(
                new AutoSetupState { Phase = AutoSetupPhase.Downloading, ModelTitle = "M" });
            Assert.True(running.ShowCancel);
            Assert.True(running.ShowProgress);
            Assert.False(running.PrimaryEnabled);
        }
    }
}
