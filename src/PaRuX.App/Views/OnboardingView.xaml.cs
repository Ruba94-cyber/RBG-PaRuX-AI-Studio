using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// First-run setup for the real Runtime Manager component lifecycle. Discovery is read-only;
    /// every mutation below is tied to an explicit button click and goes through PaRuX Core's
    /// ownership-aware API (PARUX_MANAGED / USER_MANAGED / SYSTEM_DETECTED).
    /// </summary>
    public sealed partial class OnboardingView : UserControl
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly OnboardingStateMachine _stateMachine = new();
        private CancellationTokenSource? _operationCts;
        private DispatcherTimer? _modelDownloadPoll;
        private bool _componentsBusy;
        private bool _modelDownloadRefreshBusy;
        private bool _modelDownloadViewLoaded;
        private bool _unprovenWarningShown;
        private CancellationTokenSource? _verifyLocalChatCts;
        private IReadOnlyList<ComponentStatus>? _lastComponents;

        /// <summary>The local-AI root PaRuX Core is actually using, as last reported. The text box can
        /// hold an edit the user has not applied yet; comparing the two is what tells them apart.</summary>
        private string _effectiveRoot = string.Empty;

        public OnboardingStateMachine StateMachine => _stateMachine;

        public event Action? Completed;

        public OnboardingView()
        {
            InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            RenderBundledOverview(BundledRuntimeOverviewPresenter.BuildOverview(null, null));
            Loaded += (s, e) =>
            {
                _modelDownloadViewLoaded = true;
                _modelDownloadPoll ??= CreateModelDownloadPoll();
                _ = RefreshModelDownloadsAsync();
                _ = RefreshLocalAiRootAsync();
                _ = RefreshComponentsAsync();
                _ = RefreshFirstModelAdviceAsync();
                _ = RefreshReadyCheckAsync();
                _ = InitializeAutoSetupAsync();
            };
            Unloaded += (s, e) =>
            {
                _modelDownloadViewLoaded = false;
                _modelDownloadPoll?.Stop();
                _operationCts?.Cancel();
                _verifyLocalChatCts?.Cancel();
                _autoSetupCts?.Cancel();
            };
        }

        private DispatcherTimer CreateModelDownloadPoll()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += async (s, e) => await RefreshModelDownloadsAsync();
            return timer;
        }

        private async Task RefreshModelDownloadsAsync()
        {
            if (!_modelDownloadViewLoaded || _modelDownloadRefreshBusy || FirstModelDownloadRows == null) return;
            _modelDownloadRefreshBusy = true;
            try
            {
                List<ModelDownloadStatus> jobs = await ModelDownloadClient.Current.ListDownloadsAsync(CancellationToken.None);
                if (!_modelDownloadViewLoaded) return;
                RenderModelDownloads(jobs);
                FirstModelDownloadStatusText.Text = jobs.Count == 0
                    ? "No model downloads yet. Start one from Model Hub to see its progress here."
                    : jobs.Count + " download(s) · updates every two seconds while this page is open.";
                if (jobs.Any(job => ModelDownloadStatusPolicy.IsActive(job.Status))) _modelDownloadPoll?.Start();
                else _modelDownloadPoll?.Stop();
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding download progress refresh failed: " + ex.Message);
                if (_modelDownloadViewLoaded)
                    FirstModelDownloadStatusText.Text = "Couldn't refresh model download progress: " + ex.Message;
            }
            finally { _modelDownloadRefreshBusy = false; }
        }

        private void RenderModelDownloads(IReadOnlyList<ModelDownloadStatus> jobs)
        {
            FirstModelDownloadRows.Children.Clear();
            foreach (ModelDownloadStatus job in jobs.OrderByDescending(j => j.CreatedUtc).Take(10))
            {
                var row = new StackPanel { Spacing = 4 };
                string state = ModelDownloadPresentationPolicy.DescribeStatus(job);
                string progress = job.TotalBytes > 0
                    ? " · " + FormatBytes(job.BytesDownloaded) + " / " + FormatBytes(job.TotalBytes)
                    : " · " + FormatBytes(job.BytesDownloaded) + " downloaded";
                row.Children.Add(new TextBlock
                {
                    Text = job.FileName + " · " + state + progress,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });

                string details = ModelDownloadPresentationPolicy.FormatDetails(job);
                if (details.Length > 0)
                    row.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78 });

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (ModelDownloadStatusPolicy.CanPause(job.Status))
                {
                    var pause = new Button { Content = "Pause", FontSize = 12, Padding = new Thickness(9, 3, 9, 3) };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(pause,
                        ModelDownloadPresentationPolicy.GetActionAutomationId("Onboarding", job.Id, "Pause"));
                    pause.Click += async (s, e) =>
                    {
                        try
                        {
                            await ModelDownloadClient.Current.PauseDownloadAsync(job.Id, CancellationToken.None);
                            FirstModelDownloadStatusText.Text = "Paused " + job.FileName + "; downloaded bytes are kept for resume.";
                            await RefreshModelDownloadsAsync();
                        }
                        catch (Exception ex) { FirstModelDownloadStatusText.Text = "Pause failed: " + ex.Message; }
                    };
                    actions.Children.Add(pause);
                }
                else if (ModelDownloadStatusPolicy.CanResume(job.Status))
                {
                    var resume = new Button { Content = "Resume", FontSize = 12, Padding = new Thickness(9, 3, 9, 3) };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(resume,
                        ModelDownloadPresentationPolicy.GetActionAutomationId("Onboarding", job.Id, "Resume"));
                    resume.Click += async (s, e) =>
                    {
                        try
                        {
                            await ModelDownloadClient.Current.StartDownloadAsync(
                                new ModelDownloadRequest { ResumeFromDownloadId = job.Id }, CancellationToken.None);
                            FirstModelDownloadStatusText.Text = "Re-queued " + job.FileName + " from its preserved partial download.";
                            await RefreshModelDownloadsAsync();
                        }
                        catch (Exception ex) { FirstModelDownloadStatusText.Text = "Resume failed: " + ex.Message; }
                    };
                    actions.Children.Add(resume);

                    if (ModelDownloadStatusPolicy.CanRemove(job.Status, job.Id))
                    {
                        var remove = new Button { Content = "Remove", FontSize = 12, Padding = new Thickness(9, 3, 9, 3) };
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(remove,
                            ModelDownloadPresentationPolicy.GetActionAutomationId("Onboarding", job.Id, "Remove"));
                        remove.Click += async (s, e) =>
                        {
                            var confirm = new ContentDialog
                            {
                                Title = "Remove this download?",
                                Content = job.FileName + " and its partially downloaded bytes will be deleted. This cannot be undone.",
                                PrimaryButtonText = "Remove",
                                CloseButtonText = "Cancel",
                                DefaultButton = ContentDialogButton.Close,
                                XamlRoot = XamlRoot
                            };
                            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                            try
                            {
                                await ModelDownloadClient.Current.RemoveDownloadAsync(job.Id, CancellationToken.None);
                                FirstModelDownloadStatusText.Text = "Removed " + job.FileName + ".";
                                await RefreshModelDownloadsAsync();
                            }
                            catch (Exception ex) { FirstModelDownloadStatusText.Text = "Remove failed: " + ex.Message; }
                        };
                        actions.Children.Add(remove);
                    }
                }

                if (actions.Children.Count > 0) row.Children.Add(actions);
                FirstModelDownloadRows.Children.Add(row);
            }
            if (jobs.Count == 0)
                FirstModelDownloadRows.Children.Add(new TextBlock { Text = "No downloads yet.", Opacity = 0.75 });
        }

        private async Task RefreshLocalAiRootAsync()
        {
            if (LocalAiRootText == null) return;
            try
            {
                LocalAiRootStatus status = await LocalAiRootClient.Current.GetAsync(CancellationToken.None);
                LocalAiRootBox.Text = status.Root;
                _effectiveRoot = status.Root ?? string.Empty;
                if (_effectiveRoot.Length > 0) _stateMachine.ConfigureRoot(_effectiveRoot);
                LocalAiRootText.Text = (status.Configured ? "Explicitly configured. " : "Auto-detected. ")
                    + (status.LooksConfigured
                        ? "An existing local-AI layout was found."
                        : "No complete runtime layout was found yet; configure the components below.");
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding RefreshLocalAiRootAsync failed: " + ex.Message);
                LocalAiRootText.Text = UserFailureRecoveryMapper.Map(ex, "Couldn't reach PaRuX Core").FormatStatusLine();
            }
        }

        private async void LocalAiRootBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                LocalAiRootBox.Text = folder.Path;
            }
            catch (Exception ex) { Log.Write("Onboarding LocalAiRootBrowse failed: " + ex.Message); }
        }

        private async void LocalAiRootSave_Click(object sender, RoutedEventArgs e)
        {
            string path = (LocalAiRootBox?.Text ?? string.Empty).Trim();
            if (path.Length == 0) return;
            try
            {
                await ApplyLocalAiRootAsync(path);
                await RefreshLocalAiRootAsync();
                await RefreshComponentsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding LocalAiRootSave failed: " + ex.Message);
                LocalAiRootText.Text = UserFailureRecoveryMapper.Map(ex, "Couldn't save").FormatStatusLine();
            }
        }

        private async Task ApplyLocalAiRootAsync(string path)
        {
            LocalAiRootStatus status = await LocalAiRootClient.Current.SetAsync(path, CancellationToken.None);
            SettingsStore.Current.Data.LocalAiRootPath = status.Root;
            SettingsStore.Current.Save();
            _effectiveRoot = status.Root ?? string.Empty;
            if (_effectiveRoot.Length > 0) _stateMachine.ConfigureRoot(_effectiveRoot);
        }

        private async void RefreshComponents_Click(object sender, RoutedEventArgs e) => await RefreshComponentsAsync();

        private async Task RefreshComponentsAsync()
        {
            if (_componentsBusy || ComponentsList == null) return;
            SetComponentsBusy(true, "Detecting installed components…");
            try
            {
                List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
                _lastComponents = components;
                RenderComponents(components);
                UpdateReadySummary(components);
                RenderBundledOverview(BundledRuntimeOverviewPresenter.BuildOverview(_lastComponents, _modelAdvice));
                ComponentsStatusText.Text = components.Count + " component(s) checked. Detection never installs or changes software.";
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding RefreshComponents failed: " + ex.Message);
                ComponentsList.Children.Clear();
                ComponentsStatusText.Text = UserFailureRecoveryMapper.Map(ex, "Component discovery failed").FormatStatusLine();
            }
            finally { SetComponentsBusy(false, ComponentsStatusText.Text); }
        }

        private void RenderComponents(IReadOnlyList<ComponentStatus> components)
        {
            ComponentsList.Children.Clear();
            foreach (ComponentStatus component in components)
            {
                var card = new StackPanel { Spacing = 6 };
                var header = new Grid { ColumnSpacing = 8 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = new TextBlock
                {
                    Text = component.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(title, 0);
                header.Children.Add(title);

                var ownership = new TextBlock
                {
                    Text = OwnershipLabel(component.Ownership),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Opacity = 0.75
                };
                Grid.SetColumn(ownership, 1);
                header.Children.Add(ownership);
                card.Children.Add(header);

                string state = component.Detected ? component.Health : component.Skipped ? "skipped for now" : "not configured";
                var detail = new TextBlock
                {
                    Text = state + (string.IsNullOrWhiteSpace(component.Version) ? string.Empty : " · " + component.Version),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.82
                };
                card.Children.Add(detail);

                if (!string.IsNullOrWhiteSpace(component.Path))
                    card.Children.Add(new TextBlock { Text = component.Path, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.68 });
                if (!string.IsNullOrWhiteSpace(component.ValidationMessage))
                    card.Children.Add(new TextBlock { Text = component.ValidationMessage, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.68 });

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                string id = component.Id;

                if (component.Actions.Contains("install"))
                    actions.Children.Add(ActionButton("Install with PaRuX", async () => await InstallManagedComponentAsync(component)));
                if (component.Actions.Contains("locate") || component.Actions.Contains("relocate"))
                    actions.Children.Add(ActionButton(component.Actions.Contains("relocate") ? "Change location…" : "Locate existing…",
                        async () => await LocateComponentAsync(component)));
                if (component.Actions.Contains("remove"))
                    actions.Children.Add(ActionButton("Forget location", async () =>
                    {
                        await RunComponentActionAsync("Forgetting user-managed location…",
                            token => RuntimeManagerClient.Current.RemoveLocationAsync(id, token));
                    }));
                if (component.Actions.Contains("skip"))
                    actions.Children.Add(ActionButton("Skip for now", async () =>
                    {
                        await RunComponentActionAsync("Skipping " + component.DisplayName + "…",
                            token => RuntimeManagerClient.Current.SkipAsync(id, token));
                    }));
                if (component.Actions.Contains("unskip"))
                    actions.Children.Add(ActionButton("Configure now", async () =>
                    {
                        await RunComponentActionAsync("Re-enabling setup for " + component.DisplayName + "…",
                            token => RuntimeManagerClient.Current.UnskipAsync(id, token));
                    }));

                if (actions.Children.Count > 0) card.Children.Add(actions);
                ComponentsList.Children.Add(card);
            }
        }

        private Button ActionButton(string text, Func<Task> action)
        {
            var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(9, 3, 9, 3) };
            button.Click += async (s, e) =>
            {
                if (_componentsBusy) return;
                try { await action(); }
                catch (Exception ex)
                {
                    Log.Write("Onboarding component action failed: " + ex.Message);
                    ComponentsStatusText.Text = UserFailureRecoveryMapper.Map(ex, "Component action failed").FormatStatusLine();
                    SetComponentsBusy(false, ComponentsStatusText.Text);
                }
            };
            return button;
        }

        private async Task LocateComponentAsync(ComponentStatus component)
        {
            string path;
            if (component.Id == "comfyui")
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                path = folder.Path;
            }
            else
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                path = file.Path;
            }

            await RunComponentActionAsync("Registering existing " + component.DisplayName + "…",
                token => RuntimeManagerClient.Current.LocateAsync(component.Id, path, token));
        }

        private async Task InstallManagedComponentAsync(ComponentStatus component)
        {
            string? backend = BackendForComponent(component.Id);
            if (backend == null) throw new InvalidOperationException("This component has no PaRuX-managed installer.");

            SetComponentsBusy(true, "Checking available builds for " + component.DisplayName + "…");
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            CancellationToken token = _operationCts.Token;

            try
            {
                List<RuntimeBuildOption> available = await RuntimeManagerClient.Current.GetAvailableBuildsAsync(backend, token);
                RuntimeBuildOption? selected = available.FirstOrDefault(x => x.Recommended) ?? available.FirstOrDefault();
                if (selected == null)
                    throw new InvalidOperationException("No compatible Windows build is currently published for " + component.DisplayName + ".");

                ComponentsStatusText.Text = "Installing " + component.DisplayName + " " + selected.Tag
                    + (string.IsNullOrWhiteSpace(selected.Variant) ? string.Empty : " (" + selected.Variant + ")") + "…";
                RuntimeInstallStatus started = await RuntimeManagerClient.Current.StartInstallAsync(
                    backend, selected.Tag, selected.Variant, token);

                RuntimeInstallStatus final = await RuntimeManagerClient.Current.WaitForInstallAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        var presentation = InstallProgressPresenter.Present(progress, component.DisplayName);
                        if (ComponentsProgress != null)
                        {
                            ComponentsProgress.IsIndeterminate = presentation.IsIndeterminate;
                            if (presentation.Fraction.HasValue)
                            {
                                ComponentsProgress.Minimum = 0;
                                ComponentsProgress.Maximum = 1;
                                ComponentsProgress.Value = presentation.Fraction.Value;
                            }
                        }
                        ComponentsStatusText.Text = presentation.FullText;
                    });
                }, token);

                if (final.Stage != "completed")
                    throw new InvalidOperationException(final.Error ?? ("Install ended with state " + final.Stage + "."));

                try { await RuntimeManagerClient.Current.UnskipAsync(component.Id, CancellationToken.None); }
                catch { /* install is complete; stale skip state is cosmetic and discovery hides it while installed */ }
                ComponentsStatusText.Text = component.DisplayName + " installed successfully.";
            }
            finally
            {
                SetComponentsBusy(false, ComponentsStatusText.Text);
                await RefreshComponentsAsync();
            }
        }

        private async Task RunComponentActionAsync(string message, Func<CancellationToken, Task<ComponentStatus>> action)
        {
            SetComponentsBusy(true, message);
            try
            {
                await action(CancellationToken.None);
                await RefreshComponentsCoreAsync();
            }
            finally { SetComponentsBusy(false, ComponentsStatusText.Text); }
        }

        private async Task RefreshComponentsCoreAsync()
        {
            List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
            _lastComponents = components;
            RenderComponents(components);
            UpdateReadySummary(components);
            RenderBundledOverview(BundledRuntimeOverviewPresenter.BuildOverview(_lastComponents, _modelAdvice));
            ComponentsStatusText.Text = "Component state updated.";
        }

        private void SetComponentsBusy(bool busy, string message)
        {
            _componentsBusy = busy;
            if (ComponentsProgress != null)
            {
                ComponentsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                if (!busy) ComponentsProgress.IsIndeterminate = true;
            }
            if (ComponentsStatusText != null && !string.IsNullOrWhiteSpace(message)) ComponentsStatusText.Text = message;
            if (GetStartedButton != null) GetStartedButton.IsEnabled = !busy;
        }

        private void UpdateReadySummary(IReadOnlyList<ComponentStatus> components)
        {
            _ = RefreshReadyCheckAsync(components);
        }

        public async Task RefreshReadyCheckAsync(IReadOnlyList<ComponentStatus>? knownComponents = null)
        {
            if (SetupSummaryText == null) return;
            try
            {
                IReadOnlyList<ComponentStatus> components = knownComponents ?? await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
                var componentStates = components.Select(c => new ComponentState
                {
                    Id = c.Id,
                    DisplayName = c.DisplayName,
                    Ownership = OwnershipLabel(c.Ownership),
                    Detected = c.Detected,
                    Skipped = c.Skipped,
                    Version = c.Version,
                    Path = c.Path,
                    Health = c.Health
                }).ToList();
                _stateMachine.SetComponents(componentStates);

                bool runtimeHealthy = components.Any(c =>
                    (c.Id == "llama-server-cuda" || c.Id == "llama-server-vulkan") && c.Detected);

                List<ModelRoleStatus>? roles = null;
                try
                {
                    roles = await ModelRolesClient.Current.GetRolesAsync(CancellationToken.None);
                }
                catch
                {
                    // Core might not have model-roles endpoint active yet
                }

                // Monitor the configured model assignments, not transient active/loaded status:
                // the first chat on a clean install can legitimately start the runtime.
                _stateMachine.ObserveTextModelConfiguration(roles?
                    .Where(r => string.Equals(r.Kind, "text", StringComparison.OrdinalIgnoreCase))
                    .Select(r => (r.Role, r.ModelPath, r.FileExists, r.IsOverridden)));

                bool modelAvailable = roles != null && roles.Any(r =>
                    string.Equals(r.Kind, "text", StringComparison.OrdinalIgnoreCase)
                    && (r.FileExists || !string.IsNullOrWhiteSpace(r.LoadedModelPath)));
                string? modelName = roles?.FirstOrDefault(r =>
                    string.Equals(r.Kind, "text", StringComparison.OrdinalIgnoreCase)
                    && (!string.IsNullOrWhiteSpace(r.LoadedModelPath) || r.FileExists))?.ModelPath;
                if (string.IsNullOrWhiteSpace(modelName))
                    modelName = roles?.FirstOrDefault(r =>
                        string.Equals(r.Kind, "text", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(r.LoadedModelPath))?.LoadedModelPath;

                bool probeSucceeded = false;
                string? probeError = null;
                try
                {
                    RuntimeManagerStatus runtimeStatus = await ModelRolesClient.Current.GetRuntimeStatusAsync(CancellationToken.None);
                    // A detected binary or selected role does not prove a live, healthy backend.
                    probeSucceeded = runtimeStatus.Backends.Any(b =>
                        (b.Backend == "text-cuda" || b.Backend == "text-vulkan") && (b.Running || b.Healthy));
                }
                catch (Exception ex)
                {
                    probeError = ex.Message;
                    probeSucceeded = false; // A failed status check must never count as readiness.
                }

                var evidence = ChatReadinessEvidence.Create(
                    runtimeHealthy: runtimeHealthy,
                    runtimeSummary: runtimeHealthy ? "Text runtime detected" : "No text runtime detected",
                    modelAvailable: modelAvailable,
                    modelSummary: !string.IsNullOrWhiteSpace(modelName) ? System.IO.Path.GetFileName(modelName) : null,
                    probeSucceeded: probeSucceeded,
                    probeSummary: probeSucceeded ? "Text backend reported healthy" : "Text backend health is unverified",
                    errorMessage: probeError);

                ReadinessAssessment assessment = _stateMachine.EvaluateReadiness(evidence);
                ApplyReadinessAssessment(assessment);
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding RefreshReadyCheckAsync failed: " + ex.Message);
                SetupSummaryText.Text = "Could not check chat readiness: " + ex.Message;
            }
        }

        private void ApplyReadinessAssessment(ReadinessAssessment assessment)
        {
            if (SetupSummaryText == null) return;
            if (assessment.IsReady)
            {
                SetupSummaryText.Text = assessment.Summary;
                if (GetStartedButton != null)
                {
                    GetStartedButton.Content = "Start Chatting";
                    GetStartedButton.IsEnabled = !_componentsBusy && _verifyLocalChatCts == null;
                }
            }
            else
            {
                SetupSummaryText.Text = assessment.Summary
                    + (string.IsNullOrWhiteSpace(assessment.NextAction) ? string.Empty : "\n\nNext step: " + assessment.NextAction);
                if (GetStartedButton != null)
                {
                    GetStartedButton.Content = "Get Started";
                    GetStartedButton.IsEnabled = !_componentsBusy && _verifyLocalChatCts == null;
                }
            }
        }


        /// <summary>The verified-chat challenge shared by the manual button and automatic setup.
        /// Returns null once a local model repeated a one-time code; otherwise the honest reason.
        /// Must run on the UI thread. Cancellation propagates as OperationCanceledException.</summary>
        private async Task<string?> VerifyChatCoreAsync(CancellationToken token, Action<string> report)
        {
            await RefreshReadyCheckAsync();
            int revision = _stateMachine.ConfigurationRevision;
            ChatReadinessEvidence? initial = _stateMachine.LastEvidence;
            // A cold-started runtime may not be running until the first chat request.
            // Do not require a healthy running process before exercising Core's managed
            // start-on-demand path; the post-request check below still requires it.
            if (initial == null || !initial.RuntimeHealthy || !initial.ModelAvailable)
                return "Install a local text runtime and a chat model first. Check Components and Model Hub.";

            token.ThrowIfCancellationRequested();
            string challenge = "PRX-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var answer = new StringBuilder();
            bool localSeen = false;
            bool remoteSeen = false;
            report("Waiting for the local model to repeat a one-time code…");

            await ChatClient.Current.StreamAsync(
                history: new[] { new PaRuX.Models.ChatMessage
                {
                    Role = "user", Content = "Repeat this one-time verification code exactly: " + challenge
                } },
                systemPrompt: "You are verifying a local model installation. Reply with only the exact requested code.",
                onDelta: delta =>
                {
                    if (answer.Length < 4096 && delta != null)
                        answer.Append(delta.AsSpan(0, Math.Min(delta.Length, 4096 - answer.Length)));
                },
                onSwitchedToUncensored: null,
                onRagSources: null,
                onSkillSelected: null,
                forcedSkillId: null,
                onReasoningDelta: null,
                conversationId: null,
                preferLowLatency: true,
                continueFinalMessage: false,
                structuredOutput: null,
                ct: token,
                forcedModelRoute: new RouteRequest { Role = "standard", Mode = RouteMode.Manual },
                onProvenance: provenance =>
                {
                    if (provenance.Locality == ChatProvenance.LocalLocality) localSeen = true;
                    if (provenance.Locality == ChatProvenance.RemoteLocality) remoteSeen = true;
                });

            token.ThrowIfCancellationRequested();
            string? failed = OnboardingChatVerificationPolicy.FailureReason(
                challenge, answer.ToString(), localSeen, remoteSeen);
            if (failed != null) return failed;

            // A model or runtime changed while streaming: never accept an obsolete success.
            await RefreshReadyCheckAsync();
            token.ThrowIfCancellationRequested();
            ChatReadinessEvidence? current = _stateMachine.LastEvidence;
            if (_stateMachine.ConfigurationRevision != revision
                || current == null || !current.RuntimeHealthy || !current.ModelAvailable || !current.ProbeSucceeded)
                return "Your model or runtime changed during verification. Check the current setup and retry.";

            _stateMachine.RecordVerifiedChatAnswer();
            ApplyReadinessAssessment(_stateMachine.EvaluateReadiness(current));
            return null;
        }

        /// <summary>Actual authenticated local chat roundtrip with a time bound and cancellation.</summary>
        private async void VerifyLocalChat_Click(object sender, RoutedEventArgs e)
        {
            if (_verifyLocalChatCts != null)
            {
                _verifyLocalChatCts.Cancel();
                return;
            }

            using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _verifyLocalChatCts = operation;
            VerifyLocalChatButton.Content = "Cancel verification";
            VerifyLocalChatStatusText.Text = "Checking the local runtime and downloaded model…";
            if (GetStartedButton != null) GetStartedButton.IsEnabled = false;

            try
            {
                string? failure = await VerifyChatCoreAsync(operation.Token, text => VerifyLocalChatStatusText.Text = text);
                if (failure != null)
                {
                    VerifyLocalChatStatusText.Text = failure;
                    return;
                }

                VerifyLocalChatStatusText.Text = "Verified: a local model answered the one-time code. You can start chatting.";
            }
            catch (OperationCanceledException)
            {
                VerifyLocalChatStatusText.Text = "Verification cancelled or timed out. Check the runtime and retry.";
            }
            catch (Exception ex)
            {
                Log.Write("Onboarding local chat verification failed: " + ex);
                VerifyLocalChatStatusText.Text =
                    UserFailureRecoveryMapper.Map(ex, "Local chat verification failed").FormatStatusLine();
            }
            finally
            {
                if (ReferenceEquals(_verifyLocalChatCts, operation))
                    _verifyLocalChatCts = null;
                VerifyLocalChatButton.Content = "Verify local chat";
                if (GetStartedButton != null) GetStartedButton.IsEnabled = !_componentsBusy;
            }
        }

        private void RenderBundledOverview(FirstInstallEffortOverview overview)
        {
            if (BundledRuntimeHeadlineText != null)
                BundledRuntimeHeadlineText.Text = overview.Headline;

            if (BundledRuntimeSubheadText != null)
                BundledRuntimeSubheadText.Text = overview.Subhead;

            if (InstallerHashGuidanceText != null)
                InstallerHashGuidanceText.Text = overview.HashCheckGuidance;

            if (BundledItemsList != null)
            {
                BundledItemsList.Children.Clear();
                foreach (BundledComponentItem item in overview.BundledItems)
                {
                    var itemPanel = new Grid { ColumnSpacing = 8 };
                    itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var textStack = new StackPanel { Spacing = 2 };
                    var titleBlock = new TextBlock
                    {
                        Text = item.DisplayName + " · " + item.BundledSizeText,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    };
                    textStack.Children.Add(titleBlock);

                    var descBlock = new TextBlock
                    {
                        Text = item.Description,
                        FontSize = 11,
                        Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap
                    };
                    textStack.Children.Add(descBlock);
                    Grid.SetColumn(textStack, 0);
                    itemPanel.Children.Add(textStack);

                    string badgeText = item.IsStaged ? "✓ Staged & Ready (0 B)" : "Bundled (0 B)";
                    var badge = new TextBlock
                    {
                        Text = badgeText,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Opacity = 0.9
                    };
                    Grid.SetColumn(badge, 1);
                    itemPanel.Children.Add(badge);

                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(itemPanel, "Onboarding_BundledItem_" + item.Id);
                    BundledItemsList.Children.Add(itemPanel);
                }
            }

            if (DownloadableItemsList != null)
            {
                DownloadableItemsList.Children.Clear();
                foreach (DownloadableComponentItem item in overview.DownloadableItems)
                {
                    var itemPanel = new Grid { ColumnSpacing = 8 };
                    itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var textStack = new StackPanel { Spacing = 2 };
                    string reqPrefix = item.IsRequired ? "Required for chat: " : "Optional: ";
                    var titleBlock = new TextBlock
                    {
                        Text = reqPrefix + item.DisplayName,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    };
                    textStack.Children.Add(titleBlock);

                    var descBlock = new TextBlock
                    {
                        Text = item.WhenDownloaded,
                        FontSize = 11,
                        Opacity = 0.78,
                        TextWrapping = TextWrapping.Wrap
                    };
                    textStack.Children.Add(descBlock);
                    Grid.SetColumn(textStack, 0);
                    itemPanel.Children.Add(textStack);

                    var sizeBadge = new TextBlock
                    {
                        Text = item.EstimatedSizeText,
                        FontSize = 11,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Opacity = 0.9
                    };
                    Grid.SetColumn(sizeBadge, 1);
                    itemPanel.Children.Add(sizeBadge);

                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(itemPanel, "Onboarding_DownloadableItem_" + item.Id);
                    DownloadableItemsList.Children.Add(itemPanel);
                }
            }
        }

        public async void OnReturnedFromModelHub()
        {
            _unprovenWarningShown = false;
            _verifyLocalChatCts?.Cancel();
            _stateMachine.ReturnFromModelHub();
            await RefreshComponentsAsync();
            await RefreshFirstModelAdviceAsync();
            await RefreshReadyCheckAsync();
        }

        private static string OwnershipLabel(ComponentOwnership ownership) =>
            ComponentLifecyclePresenter.FormatOwnership(ownership);

        private static string? BackendForComponent(string id) =>
            ComponentLifecyclePresenter.BackendForComponent(id);

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        


        

        private async void GetStarted_Click(object sender, RoutedEventArgs e)
        {
            if (_verifyLocalChatCts != null) return;
            // Browsing to a folder only fills the box; applying it is a separate button. Someone who
            // picks a folder and then presses Get Started has plainly chosen that folder, so finish
            // the job rather than dropping the choice on the floor and starting on a different root
            // than the one still displayed on screen.
            string shown = (LocalAiRootBox?.Text ?? string.Empty).Trim();
            if (shown.Length > 0
                && !string.Equals(shown, _effectiveRoot, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await ApplyLocalAiRootAsync(shown);
                }
                catch (Exception ex)
                {
                    // Say so and stay put: continuing would silently use a different folder.
                    Log.Write("Onboarding GetStarted could not apply the shown local-AI root: " + ex.Message);
                    if (LocalAiRootText != null)
                        LocalAiRootText.Text = UserFailureRecoveryMapper.Map(ex, "Couldn't use that folder").FormatStatusLine();
                    return;
                }
            }

            if (_stateMachine.CanComplete())
            {
                _stateMachine.Complete();
                SettingsStore.Current.Data.OnboardingCompleted = true;
                SettingsStore.Current.Save();
                Completed?.Invoke();
                return;
            }

            // Readiness is unproven. It must not complete silently.
            IReadOnlyList<string> unprovenItems = _stateMachine.GetUnprovenItems();
            string unprovenList = string.Join(Environment.NewLine, unprovenItems.Select(item => "• " + item));

            if (UnprovenReadinessText != null)
                UnprovenReadinessText.Text = unprovenList;
            if (UnprovenReadinessCard != null)
                UnprovenReadinessCard.Visibility = Visibility.Visible;
            if (SetupSummaryText != null)
                SetupSummaryText.Text = "Readiness is unproven: " + string.Join("; ", unprovenItems);

            Log.Write("Onboarding completion attempted with unproven readiness: " + string.Join("; ", unprovenItems));

            if (XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Unproven Chat Readiness",
                    Content = "The following readiness requirements were not verified:"
                        + Environment.NewLine + Environment.NewLine
                        + unprovenList
                        + Environment.NewLine + Environment.NewLine
                        + "If you proceed, chat may not work until these components are configured.",
                    PrimaryButtonText = "Proceed anyway",
                    CloseButtonText = "Go back",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };

                ContentDialogResult result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }
            }
            else
            {
                if (!_unprovenWarningShown)
                {
                    _unprovenWarningShown = true;
                    return;
                }
            }

            _stateMachine.ForceCompleteWithWarning(unprovenItems);
            SettingsStore.Current.Data.OnboardingCompleted = true;
            SettingsStore.Current.Save();
            Completed?.Invoke();
        }
    }
}
