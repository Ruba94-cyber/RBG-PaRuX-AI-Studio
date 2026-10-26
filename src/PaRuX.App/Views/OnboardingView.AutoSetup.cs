using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// "Set up automatically": the single primary action of first run. The orchestration lives in
    /// <see cref="OnboardingAutoSetupService"/> (pure, tested); this file only renders its state and
    /// adapts the existing clients, licence prompt and chat verification to <see cref="IAutoSetupHost"/>.
    /// </summary>
    public sealed partial class OnboardingView
    {
        private OnboardingAutoSetupService? _autoSetup;
        private CancellationTokenSource? _autoSetupCts;

        private async Task InitializeAutoSetupAsync()
        {
            if (_autoSetup != null || AutoSetupHeadlineText == null) return;

            IAutoSetupStateStore store = new FileAutoSetupStateStore(
                Path.Combine(AppPaths.PaRuXRoot, "onboarding-autosetup.json"));
#if DEBUG
            // UI-test fixtures must neither read nor write the real user's run state.
            if (Environment.GetEnvironmentVariable("PARUX_UI_TEST_FIRST_MODEL_STORAGE") != null)
                store = new InMemoryAutoSetupStateStore();
#endif
            _autoSetup = new OnboardingAutoSetupService(new AutoSetupHost(this), store);
            _autoSetup.Changed += snapshot => _dispatcher.TryEnqueue(() => ApplyAutoSetup(snapshot));
            ApplyAutoSetup(new AutoSetupSnapshot { State = _autoSetup.State });

            // A previous process stopped mid-run (restart, crash, sleep): carry on from the saved step.
            if (_autoSetup.IsResumable) await RunAutoSetupAsync();
        }

        private void ApplyAutoSetup(AutoSetupSnapshot snapshot)
        {
            if (AutoSetupHeadlineText == null) return;
            AutoSetupViewModel vm = OnboardingAutoSetupPresenter.Present(snapshot.State);
            AutoSetupHeadlineText.Text = vm.Headline;
            AutoSetupDetailText.Text = vm.Detail;
            AutoSetupStartButton.Content = vm.PrimaryLabel;
            AutoSetupStartButton.IsEnabled = vm.PrimaryEnabled;
            AutoSetupStartButton.Visibility = vm.ShowRetry ? Visibility.Collapsed : Visibility.Visible;
            AutoSetupCancelButton.Visibility = vm.ShowCancel ? Visibility.Visible : Visibility.Collapsed;
            AutoSetupRetryButton.Visibility = vm.ShowRetry ? Visibility.Visible : Visibility.Collapsed;
            AutoSetupSkipButton.Visibility = vm.ShowSkip ? Visibility.Visible : Visibility.Collapsed;

            AutoSetupProgress.Visibility = vm.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            AutoSetupProgressText.Visibility = vm.ShowProgress && snapshot.Progress != null
                ? Visibility.Visible : Visibility.Collapsed;
            if (snapshot.Progress is { } progress)
            {
                AutoSetupProgress.IsIndeterminate = progress.IsIndeterminate;
                if (progress.Fraction.HasValue) AutoSetupProgress.Value = progress.Fraction.Value;
                AutoSetupProgressText.Text = progress.FullText;
            }
            else
            {
                AutoSetupProgress.IsIndeterminate = vm.ShowProgress;
            }
        }

        private async void AutoSetupStart_Click(object sender, RoutedEventArgs e)
        {
            if (_autoSetup == null || _autoSetupCts != null) return;
            if (_autoSetup.State.Phase == AutoSetupPhase.Ready)
            {
                // Setup is verified: the card's button now takes the user into Chat instead of offering to redo it.
                GetStarted_Click(sender, e);
                return;
            }
            _autoSetup.Start();
            await RunAutoSetupAsync();
        }

        private async void AutoSetupRetry_Click(object sender, RoutedEventArgs e)
        {
            if (_autoSetup == null || _autoSetupCts != null) return;
            if (!_autoSetup.TryPrepareRetry())
            {
                ApplyAutoSetup(new AutoSetupSnapshot { State = _autoSetup.State });
                return;
            }

            await RunAutoSetupAsync();
        }

        private void AutoSetupCancel_Click(object sender, RoutedEventArgs e) => _autoSetupCts?.Cancel();

        private void AutoSetupSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_autoSetup == null || _autoSetupCts != null) return;
            _autoSetup.Skip();
            ApplyAutoSetup(new AutoSetupSnapshot { State = _autoSetup.State });
        }

        private async Task RunAutoSetupAsync()
        {
            if (_autoSetup == null || _autoSetupCts != null) return;
            var cts = new CancellationTokenSource();
            _autoSetupCts = cts;
            try
            {
                await _autoSetup.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding automatic setup crashed: " + ex);
            }
            finally
            {
                _autoSetupCts = null;
                cts.Dispose();
                ApplyAutoSetup(new AutoSetupSnapshot { State = _autoSetup.State });
                // Reflect whatever the run changed: components, roles, downloads and readiness.
                await RefreshComponentsAsync();
                await RefreshFirstModelAdviceAsync();
                await RefreshModelDownloadsAsync();
                await RefreshReadyCheckAsync();
            }
        }

        private Task<T> OnUiAsync<T>(Func<Task<T>> work)
        {
            if (_dispatcher.HasThreadAccess) return work();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(async () =>
                {
                    try { completion.SetResult(await work()); }
                    catch (Exception ex) { completion.SetException(ex); }
                }))
                completion.SetException(new InvalidOperationException("The setup window is no longer available."));
            return completion.Task;
        }

        private sealed class AutoSetupHost : IAutoSetupHost
        {
            private readonly OnboardingView _view;
            private RuntimeManagerHardwareStatus? _hardware;
            public AutoSetupHost(OnboardingView view) => _view = view;

            public Task<FirstModelAdvice> GetAdviceAsync(CancellationToken ct) =>
                ModelDownloadClient.Current.GetAdviceForThisPcAsync(ct);

            public async Task<AutoSetupRuntimeInfo> GetTextRuntimeAsync(CancellationToken ct)
            {
                List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(ct);
                var text = components
                    .Where(c => c.Id is "llama-server-vulkan" or "llama-server-cuda").ToList();
                // A runtime the user manages always wins: it is reported as-is and never changed.
                ComponentStatus? active = text.FirstOrDefault(c => c.Detected && c.Ownership == ComponentOwnership.USER_MANAGED)
                    ?? text.FirstOrDefault(c => c.Detected);
                var info = new AutoSetupRuntimeInfo
                {
                    Detected = active != null,
                    ComponentId = active?.Id,
                    Ownership = active?.Ownership.ToString() ?? string.Empty,
                    Version = active?.Version
                };
                if (info.IsUserManaged) return info;

                if (active == null)
                {
                    ComponentStatus? baseline = text.FirstOrDefault(c => c.Id == "llama-server-vulkan");
                    info.Offer = new AutoSetupRuntimeOffer
                    {
                        ComponentId = "llama-server-vulkan",
                        Title = baseline?.DisplayName ?? "llama.cpp Vulkan runtime",
                        Reason = "No text runtime was found. PaRuX can install its Vulkan runtime.",
                        Required = true
                    };
                    return info;
                }

                // Optional accelerator: only for an NVIDIA GPU with no CUDA runtime yet and never
                // when the user skipped it. It is offered, not installed, until the user confirms.
                ComponentStatus? cuda = text.FirstOrDefault(c => c.Id == "llama-server-cuda");
                bool nvidia = (await TryHardwareAsync(ct))?.GpuAdapters.Any(a =>
                    !a.IsSoftware && a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) == true;
                if (cuda is { Detected: false, Skipped: false } && nvidia)
                {
                    info.Offer = new AutoSetupRuntimeOffer
                    {
                        ComponentId = "llama-server-cuda",
                        Title = cuda.DisplayName,
                        Reason = "An NVIDIA GPU was found. The CUDA build is usually faster than the bundled Vulkan one. "
                            + "Your current runtime keeps working if you decline."
                    };
                }

                return info;
            }

            public Task<bool> ConfirmRuntimeInstallAsync(AutoSetupRuntimeOffer offer, CancellationToken ct) =>
                _view.OnUiAsync(async () =>
                {
                    if (_view.XamlRoot == null) return false;
                    var dialog = new ContentDialog
                    {
                        XamlRoot = _view.XamlRoot,
                        Title = "Install " + offer.Title + "?",
                        Content = offer.Reason + "\n\nPaRuX will manage this install. Software you installed yourself is never changed.",
                        PrimaryButtonText = "Install with PaRuX",
                        CloseButtonText = offer.Required ? "Cancel" : "Keep current runtime",
                        DefaultButton = ContentDialogButton.Primary
                    };
                    return await dialog.ShowAsync() == ContentDialogResult.Primary;
                });

            public async Task InstallRuntimeAsync(
                AutoSetupRuntimeOffer offer, IProgress<InstallProgressPresentation> progress, CancellationToken ct)
            {
                string backend = ComponentLifecyclePresenter.BackendForComponent(offer.ComponentId)
                    ?? throw new InvalidOperationException("This component has no PaRuX-managed installer.");
                List<RuntimeBuildOption> builds = await RuntimeManagerClient.Current.GetAvailableBuildsAsync(backend, ct);
                RuntimeBuildOption build = builds.FirstOrDefault(b => b.Recommended) ?? builds.FirstOrDefault()
                    ?? throw new InvalidOperationException("No compatible Windows build is currently published for " + offer.Title + ".");
                RuntimeInstallStatus started = await RuntimeManagerClient.Current.StartInstallAsync(backend, build.Tag, build.Variant, ct);
                RuntimeInstallStatus final = await RuntimeManagerClient.Current.WaitForInstallAsync(
                    started.Id, status => progress.Report(InstallProgressPresenter.Present(status, offer.Title)), ct);
                if (final.Stage != "completed")
                    throw new InvalidOperationException(final.Error ?? ("Install ended with state " + final.Stage + "."));
                try { await RuntimeManagerClient.Current.UnskipAsync(offer.ComponentId, CancellationToken.None); }
                catch { /* the install is complete; a stale skip flag is cosmetic */ }
            }

            public async Task<IReadOnlyList<ModelQuantChoice>> GetQuantChoicesAsync(string repoId, CancellationToken ct)
            {
                List<ModelSearchResult> results = await ModelDownloadClient.Current.SearchAsync(repoId, 8, ct);
                ModelSearchResult? repo = results.FirstOrDefault(r =>
                    string.Equals(r.RepoId, repoId, StringComparison.OrdinalIgnoreCase));
                if (repo == null || repo.QuantChoices.Count == 0)
                    throw new InvalidOperationException(repoId + " did not list any GGUF files. Choose a model in Model Hub instead.");
                // Search results carry no file sizes (Hugging Face never returns them from the list
                // endpoint), so every choice is unsized. Fill them from the repository details, which is
                // what the Model Hub does; without this the setup discards every choice and never picks a model.
                if (repo.QuantChoices.Any(choice => choice.SizeBytes <= 0))
                {
                    ModelRepoDetails details = await ModelDownloadClient.Current.GetDetailsAsync(repoId, ct);
                    ModelQuantSizeResolver.Fill(repo.QuantChoices, details.Files);
                }
                return repo.QuantChoices;
            }

            public async Task<AutoSetupFit> EstimateFitAsync(long totalBytes, CancellationToken ct)
            {
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(totalBytes, await TryHardwareAsync(ct));
                return fit.Verdict switch
                {
                    ModelFitVerdict.Fits => AutoSetupFit.Fits,
                    ModelFitVerdict.Tight => AutoSetupFit.Tight,
                    ModelFitVerdict.Big => AutoSetupFit.TooBig,
                    _ => AutoSetupFit.Unknown
                };
            }

            public Task<ModelDownloadPlan> GetPlanAsync(string repoId, ModelQuantChoice choice, CancellationToken ct) =>
                ModelDownloadClient.Current.GetDownloadPlanAsync(repoId,
                    string.IsNullOrWhiteSpace(choice.Quantization) || choice.Quantization == "unknown" ? null : choice.Quantization,
                    choice.SetName, choice.Directory, choice.PartCount, ct);

            public async Task<bool> IsPlanInstalledAsync(ModelDownloadPlan plan, CancellationToken ct)
            {
                List<ManagedModelInfo> installed = await ModelDownloadClient.Current.ListManagedModelsAsync(ct);
                return plan.Parts.Count > 0 && plan.Parts.All(part => installed.Any(m =>
                    string.Equals(m.FileName, part.FileName, StringComparison.OrdinalIgnoreCase)
                    && (part.SizeBytes <= 0 || m.SizeBytes == part.SizeBytes)));
            }

            public Task<IReadOnlyList<string>?> ReviewLicenceAndStartAsync(
                ModelDownloadPlan plan, ModelQuantChoice choice, CancellationToken ct) =>
                _view.OnUiAsync<IReadOnlyList<string>?>(async () =>
                {
                    if (_view.XamlRoot == null) throw new InvalidOperationException("The setup window is not visible.");
                    ModelRepoDetails? details = null;
                    bool retrieved = false;
                    try
                    {
                        details = await ModelDownloadClient.Current.GetDetailsAsync(plan.RepoId, ct);
                        retrieved = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Write("Auto setup licence metadata lookup failed for " + plan.RepoId + ": " + ex.Message);
                    }

                    if (ModelRevisionIdentity.IsValid(details?.Revision)
                        && !string.Equals(ModelRevisionIdentity.Normalize(details!.Revision), plan.Revision, StringComparison.Ordinal))
                        throw new InvalidOperationException("The repository changed while the download plan was prepared. Try again.");

                    ModelLicenseConsentPolicy.Decision decision = ModelLicenseConsentPolicy.Evaluate(details?.License, retrieved);
                    List<string> ids = new();
                    bool queued = await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                        _view.XamlRoot, plan.RepoId, plan.Parts.OrderBy(p => p.Index).First().FileName,
                        decision, plan.Revision,
                        async (_, evidence) =>
                        {
                            List<ModelDownloadStatus> started = await ModelDownloadClient.Current.StartDownloadPlanAsync(
                                new ModelDownloadPlanRequest
                                {
                                    RepoId = plan.RepoId,
                                    Quantization = choice.Quantization,
                                    SetName = choice.SetName,
                                    Directory = choice.Directory,
                                    PartCount = choice.PartCount,
                                    Revision = plan.Revision,
                                    LicenseEvidence = evidence
                                }, CancellationToken.None);
                            ids.AddRange(started.Select(s => s.Id));
                        });
                    return queued ? ids : null;
                });

            public async Task<IReadOnlyList<ModelDownloadStatus>> ListDownloadsAsync(CancellationToken ct) =>
                await ModelDownloadClient.Current.ListDownloadsAsync(ct);

            public async Task CancelDownloadsAsync(IReadOnlyList<string> ids)
            {
                foreach (string id in ids)
                    await ModelDownloadClient.Current.CancelDownloadAsync(id, CancellationToken.None);
            }

            public Task<RoleAutoAssignResponse> AutoAssignRolesAsync(CancellationToken ct) =>
                ModelDownloadClient.Current.AutoAssignRolesAsync(ct);

            public Task<string?> VerifyChatAsync(CancellationToken ct) =>
                _view.OnUiAsync(async () =>
                {
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    operation.CancelAfter(TimeSpan.FromMinutes(3));
                    _view._verifyLocalChatCts = operation;
                    try
                    {
                        return await _view.VerifyChatCoreAsync(operation.Token,
                            text => _view.VerifyLocalChatStatusText.Text = text);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return "Verification timed out. Check the runtime and retry.";
                    }
                    finally
                    {
                        if (ReferenceEquals(_view._verifyLocalChatCts, operation)) _view._verifyLocalChatCts = null;
                    }
                });

            private async Task<RuntimeManagerHardwareStatus?> TryHardwareAsync(CancellationToken ct)
            {
                if (_hardware != null) return _hardware;
                try { _hardware = await RuntimeManagerClient.Current.GetStatusAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Write("Auto setup hardware read failed: " + ex.Message);
                }

                return _hardware;
            }
        }
    }
}
