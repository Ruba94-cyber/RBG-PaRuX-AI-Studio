using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    public sealed partial class RuntimeManagerView : UserControl
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly StackPanel _components = new StackPanel { Spacing = 14 };
        private readonly TextBlock _performanceSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        private readonly TextBlock _advisorSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        private readonly TextBlock _selectedModelText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.68 };
        private readonly ComboBox _performanceMode = new ComboBox { MinWidth = 140 };
        private readonly ComboBox _workload = new ComboBox { MinWidth = 150 };
        private CancellationTokenSource? _operationCts;
        private CancellationTokenSource? _performanceCts;
        private string? _activeInstallId;
        private string? _activeOptimizeId;
        private string? _activeRaceId;
        private string? _selectedModelPath;
        private RuntimeManagerHardwareStatus? _lastRuntimeStatus;
        private List<ComponentStatus> _lastComponents = new List<ComponentStatus>();

        public RuntimeManagerView()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAsync(); };
            Unloaded += (_, _) =>
            {
                _operationCts?.Cancel();
                _performanceCts?.Cancel();
            };
        }

        public async void OnShown() => await RefreshAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 16,
                Margin = new Thickness(24),
                MaxWidth = 1000,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;
            body.Children.Add(new TextBlock { Text = "Runtime Manager", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            AutomationProperties.SetAutomationId(_status, "RuntimeManager_Status_Text");
            AutomationProperties.SetAutomationId(_progress, "RuntimeManager_Progress_Bar");
            AutomationProperties.SetAutomationId(_performanceMode, "RuntimeManager_PerformanceMode_ComboBox");
            AutomationProperties.SetName(_performanceMode, "Performance mode");
            AutomationProperties.SetAutomationId(_workload, "RuntimeManager_Workload_ComboBox");
            AutomationProperties.SetName(_workload, "Workload");
            body.Children.Add(new TextBlock
            {
                Text = "Manage local runtimes and optimize inference for this PC. User-managed tools are never changed automatically.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var refresh = new Button { Content = "Refresh" };
            AutomationProperties.SetAutomationId(refresh, "RuntimeManager_Refresh_Button");
            refresh.Click += async (_, _) => await RefreshAsync();
            row.Children.Add(refresh);
            var cancel = new Button { Content = "Cancel active install" };
            AutomationProperties.SetAutomationId(cancel, "RuntimeManager_CancelInstall_Button");
            cancel.Click += async (_, _) => await CancelActiveInstallAsync();
            row.Children.Add(cancel);
            body.Children.Add(row);
            body.Children.Add(_progress);
            body.Children.Add(_status);
            body.Children.Add(BuildSuggestionsCard());
            body.Children.Add(BuildPerformanceCard());
            var contextOptions = new Expander
            {
                Header = "Advanced context and memory tuning",
                Content = BuildContextCard(),
                IsExpanded = false
            };
            AutomationProperties.SetAutomationId(contextOptions, "RuntimeManager_ContextOptions_Expander");
            body.Children.Add(contextOptions);
            body.Children.Add(new TextBlock
            {
                Text = "Runtime components",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            body.Children.Add(_components);
            return scroll;
        }

        private UIElement BuildPerformanceCard()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;
            body.Children.Add(new TextBlock
            {
                Text = "Inference Performance Engine",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Compare measured speed and responsiveness, then apply an optimized local profile. PaRuX validates managed runtime updates before activating them.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            _performanceMode.Items.Add(new ComboBoxItem { Content = "Balanced", Tag = "balanced" });
            _performanceMode.Items.Add(new ComboBoxItem { Content = "Turbo", Tag = "turbo" });
            _performanceMode.Items.Add(new ComboBoxItem { Content = "Eco", Tag = "eco" });
            _performanceMode.SelectedIndex = 0;
            _workload.Items.Add(new ComboBoxItem { Content = "Chat", Tag = "chat" });
            _workload.Items.Add(new ComboBoxItem { Content = "Coding", Tag = "coding" });
            _workload.Items.Add(new ComboBoxItem { Content = "Long context / RAG", Tag = "long-context" });
            _workload.SelectedIndex = 0;

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(new TextBlock { Text = "Mode", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            controls.Children.Add(_performanceMode);
            var applyMode = new Button { Content = "Apply mode" };
            AutomationProperties.SetAutomationId(applyMode, "RuntimeManager_ApplyMode_Button");
            applyMode.Click += async (_, _) => await ApplyPerformanceModeAsync();
            controls.Children.Add(applyMode);
            controls.Children.Add(new TextBlock { Text = "Workload", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Opacity = 0.7 });
            controls.Children.Add(_workload);
            body.Children.Add(controls);

            var modelActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var choose = new Button { Content = "Choose GGUF…" };
            AutomationProperties.SetAutomationId(choose, "RuntimeManager_ChooseModel_Button");
            choose.Click += async (_, _) => await ChoosePerformanceModelAsync();
            modelActions.Children.Add(choose);
            var optimize = new Button { Content = "Optimize for this PC" };
            AutomationProperties.SetAutomationId(optimize, "RuntimeManager_Optimize_Button");
            optimize.Click += async (_, _) => await OptimizeAsync();
            modelActions.Children.Add(optimize);
            var race = new Button { Content = "Race installed runtimes…" };
            AutomationProperties.SetAutomationId(race, "RuntimeManager_Race_Button");
            race.Click += async (_, _) => await RaceRuntimesAsync();
            modelActions.Children.Add(race);
            var cancel = new Button { Content = "Cancel performance test" };
            AutomationProperties.SetAutomationId(cancel, "RuntimeManager_CancelPerformance_Button");
            cancel.Click += async (_, _) => await CancelPerformanceAsync();
            modelActions.Children.Add(cancel);
            body.Children.Add(modelActions);
            body.Children.Add(_selectedModelText);
            body.Children.Add(_performanceSummary);
            body.Children.Add(new TextBlock
            {
                Text = "PaRuX Optimization Advisor",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            });
            body.Children.Add(_advisorSummary);
            return card;
        }

        private Task? _refreshInFlight;

        /// <summary>Loaded and OnShown both request a refresh; overlapping runs would let the slower one overwrite
        /// newer status text (for example a cancel message), so concurrent requests share the running refresh.</summary>
        private Task RefreshAsync() => _refreshInFlight ??= RunRefreshAsync();

        private async Task RunRefreshAsync()
        {
            try { await RefreshCoreAsync(); }
            finally { _refreshInFlight = null; }
        }

        private async Task RefreshCoreAsync()
        {
            SetBusy(true, "Discovering runtime components and performance state…");
            try
            {
                List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
                RuntimeManagerHardwareStatus runtime = await RuntimeManagerClient.Current.GetStatusAsync(CancellationToken.None);
                RuntimePerformanceStatusDto performance = await RuntimeManagerClient.Current.GetPerformanceStatusAsync(CancellationToken.None);
                List<RuntimeRegressionEvidenceDto> regressions = await RuntimeManagerClient.Current.GetRegressionEvidenceAsync(CancellationToken.None);
                _lastRuntimeStatus = runtime;
                _lastComponents = components;
                try { _runtimeUpdate = await AutoMaintenanceClient.Current.GetRuntimeUpdateAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _runtimeUpdate = null; }
                Render(components);
                await RefreshSuggestionsAsync(CancellationToken.None);
                RenderPerformance(performance, runtime, regressions.FirstOrDefault());
                // After RenderPerformance, which is what settles _selectedModelPath. The offered
                // lengths depend on both the selected model and this machine's current free memory,
                // so a list built once at open would offer settings that no longer fit.
                await RefreshContextOptionsAsync(CancellationToken.None);
                int ready = components.Count(c => c.Detected);
                _status.Text = ready + " / " + components.Count + " component(s) detected or configured.";
            }
            catch (Exception ex) { _status.Text = "Runtime discovery failed: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private void RenderPerformance(
            RuntimePerformanceStatusDto performance,
            RuntimeManagerHardwareStatus runtime,
            RuntimeRegressionEvidenceDto? latestRegression)
        {
            SelectComboByTag(_performanceMode, performance.Mode);
            if (_selectedModelPath == null && !string.IsNullOrWhiteSpace(runtime.ActiveModelPath))
                _selectedModelPath = runtime.ActiveModelPath;
            if (_selectedModelPath == null)
                _selectedModelPath = performance.Profiles.FirstOrDefault()?.ModelPath;
            _selectedModelText.Text = _selectedModelPath == null
                ? "Model: none selected. Choose an installed/configured GGUF before optimizing."
                : "Model: " + _selectedModelPath;


            string gpu = performance.Gpus.Count == 0
                ? "GPU telemetry unavailable"
                : string.Join(" · ", performance.Gpus.Select(item =>
                    item.Name + " " + (item.MemoryUsedMib?.ToString() ?? "?") + "/" + (item.MemoryTotalMib?.ToString() ?? "?") + " MiB"
                    + (item.TemperatureC.HasValue ? " · " + item.TemperatureC.Value.ToString("0") + "°C" : string.Empty)
                    + (item.PowerWatts.HasValue ? " · " + item.PowerWatts.Value.ToString("0") + " W" : string.Empty)));

            RuntimePerformanceProfileSelection? latest = _selectedModelPath == null ? null : performance.Profiles
                .Where(profile => string.Equals(profile.ModelPath, _selectedModelPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(profile => profile.TunedUtc)
                .FirstOrDefault();
            string measured = latest == null
                ? "No measured winner for this model yet."
                : "Winner " + latest.Profile.DisplayName
                    + " · " + latest.DecodeTokensPerSecond.ToString("0.0") + " decode tok/s"
                    + " · " + latest.PromptTokensPerSecond.ToString("0") + " prompt tok/s"
                    + (latest.TimeToFirstTokenMilliseconds.HasValue ? " · TTFT " + latest.TimeToFirstTokenMilliseconds.Value.ToString("0") + " ms" : string.Empty)
                    + (latest.SustainedDecodeTokensPerSecond.HasValue ? " · sustained " + latest.SustainedDecodeTokensPerSecond.Value.ToString("0.0") + " tok/s" : string.Empty)
                    + (latest.AverageGpuPowerWatts.HasValue ? " · " + latest.AverageGpuPowerWatts.Value.ToString("0") + " W avg" : string.Empty)
                    + (latest.MaxGpuTemperatureC.HasValue ? " · " + latest.MaxGpuTemperatureC.Value.ToString("0") + "°C max" : string.Empty)
                    + (latest.Profile.SpeculativeType != "none" ? " · " + latest.Profile.SpeculativeType : string.Empty);
            string regression = latestRegression == null
                ? "No managed-runtime regression decision recorded yet."
                : "Last runtime gate: " + latestRegression.Decision + " · " + latestRegression.OriginalTag + " → "
                    + latestRegression.CandidateTag + " · active " + latestRegression.ActiveTag
                    + (latestRegression.ImprovementPercent.HasValue
                        ? " · " + latestRegression.ImprovementPercent.Value.ToString("+0.0;-0.0;0.0") + "%" : string.Empty);
            _performanceSummary.Text = "Mode " + performance.Mode + " · runtime " + (performance.ActiveRuntimeTag ?? "unknown")
                + "\n" + gpu + "\n" + measured + "\n" + regression;
            _advisorSummary.Text = BuildAdvisorSummary(runtime, latest);
        }

        private string BuildAdvisorSummary(RuntimeManagerHardwareStatus runtime, RuntimePerformanceProfileSelection? latest)
        {
            // GpuDetected/VramTotalMib both come from RuntimeManagerService's NVIDIA-only
            // nvidia-smi probe (LocalAiProcessManager.ProbeVramMibAsync) - they mean "an NVIDIA GPU
            // big enough for the CUDA flagship profile", not "a GPU is active", and are always 0 on
            // any non-NVIDIA machine by design. Reusing GpuDetected here as a blanket "no supported
            // GPU is active" claim was confirmed live to be actively wrong and confusing on this
            // AMD iGPU-only machine: a real Optimize for this PC run had just measured real
            // Vulkan-backend throughput (24.4 decode tok/s) on that same GPU, yet the advisor told
            // the operator to "configure a detected accelerator" - advice that made no sense since
            // the accelerator was already configured, detected, and actively serving inference.
            // GpuAdapters/GpuLargestDedicatedVideoMemoryBytes come from the separate DXGI-based
            // inventory (RuntimeHardwareEndpointExtensions.cs) that Model Hub's own hardware-fit
            // estimator already uses to show "AMD Radeon 780M Graphics 5.9 GB" correctly - that is
            // the real "is some GPU present" signal, vendor-agnostic. GpuDetected only decides
            // whether to additionally explain the NVIDIA-flagship-profile ceiling once a real
            // measurement already exists.
            bool anyGpuPresent = runtime.GpuAdapters.Count > 0 || runtime.GpuLargestDedicatedVideoMemoryBytes > 0;
            var recommendations = new List<string>();
            if (!anyGpuPresent)
                recommendations.Add("Speed: no GPU was detected at all; PaRuX is running on CPU. Expect it to be much slower - configure a detected accelerator or use a smaller quantized GGUF.");
            else if (latest == null)
                recommendations.Add("Speed: run Optimize for this PC. PaRuX has no same-model measurement yet, so changing batch, KV cache or GPU layout would be guesswork.");
            else
            {
                recommendations.Add("Speed: keep the measured winner for " + SelectedTag(_workload, "chat")
                    + " (" + latest.DecodeTokensPerSecond.ToString("0.0") + " decode tok/s). PaRuX keeps the selected text model resident until media, gaming or OFF needs the GPU.");
                if (!runtime.GpuDetected)
                {
                    string vramNote = runtime.GpuLargestDedicatedVideoMemoryBytes > 0
                        ? " (largest adapter: " + (runtime.GpuLargestDedicatedVideoMemoryBytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB)"
                        : string.Empty;
                    recommendations.Add("Speed ceiling: no NVIDIA GPU with at least 12GB VRAM was found" + vramNote
                        + ", so PaRuX's CUDA flagship profile isn't offered here - the measured winner above already reflects the best fit for this hardware.");
                }
                if (latest.Profile.SpeculativeType == "none")
                    recommendations.Add("Speculative decoding: no compatible measured winner is active. Add a compatible draft model only if a benchmark beats this baseline.");
                if (latest.TimeToFirstTokenMilliseconds is double ttft && ttft > 1500)
                    recommendations.Add("Latency: TTFT is " + ttft.ToString("0") + " ms. Re-run optimization after a runtime/driver/model change and compare prompt-cache cold versus warm results.");
            }

            recommendations.Add("Quality: do not train by default. First build a task-specific Eval Lab dataset from repeated failures; curate a dataset or adapter only when the same measured gap remains across prompts.");
            recommendations.Add("Model choice: Auto should remain the default; use Manual only when exact reproducibility or a fixed compliance profile matters.");
            return string.Join("\n", recommendations.Select((item, index) => (index + 1) + ". " + item));
        }

        private async Task ApplyPerformanceModeAsync()
        {
            string mode = SelectedTag(_performanceMode, "balanced");
            try
            {
                string applied = await RuntimeManagerClient.Current.SetPerformanceModeAsync(mode, CancellationToken.None);
                _performanceSummary.Text = "Performance mode set to " + applied + ". Existing measured profiles are retained; new optimization runs use this scoring policy.";
            }
            catch (Exception ex)
            {
                _performanceSummary.Text = "Applying performance mode failed: " + ex.Message;
            }
        }

        private async Task ChoosePerformanceModelAsync()
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add(".gguf");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                _selectedModelPath = file.Path;
                _selectedModelText.Text = "Model: " + file.Path;
            }
            catch (Exception ex)
            {
                _performanceSummary.Text = "Choosing a GGUF file failed: " + ex.Message;
            }
        }

        private async Task OptimizeAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedModelPath))
            {
                await ChoosePerformanceModelAsync();
                if (string.IsNullOrWhiteSpace(_selectedModelPath)) return;
            }

            var dialog = new ContentDialog
            {
                Title = "Optimize local inference for this PC?",
                Content = "PaRuX will temporarily stop/reload the text model, benchmark several safe llama.cpp configurations and may stress the GPU. Media generation and Gaming Mode must be idle. No clocks or driver power limits are changed.",
                PrimaryButtonText = "Optimize",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            ResetPerformanceCancellation();
            CancellationToken ct = _performanceCts!.Token;
            SetBusy(true, "Starting measured inference optimization…");
            try
            {
                RuntimeOptimizationRun started = await RuntimeManagerClient.Current.StartOptimizeAsync(new RuntimeOptimizationRequest
                {
                    ModelPath = _selectedModelPath!,
                    Label = System.IO.Path.GetFileNameWithoutExtension(_selectedModelPath),
                    Workload = SelectedTag(_workload, "chat"),
                    IncludeSpeculative = true,
                    IncludeMultiGpu = true,
                    SustainedValidation = true
                }, ct);
                _activeOptimizeId = started.Id;
                RuntimeOptimizationRun final = await RuntimeManagerClient.Current.WaitForOptimizeAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() => _performanceSummary.Text = progress.Stage);
                }, ct);
                if (final.Status != "completed")
                    throw new InvalidOperationException(final.Error ?? "Optimization ended as " + final.Status + ".");
                RuntimeOptimizationServerResult? winner = final.ServerCandidates
                    .FirstOrDefault(candidate => final.Winner != null
                        && RuntimeProfileSignature(candidate.Profile) == RuntimeProfileSignature(final.Winner));
                _performanceSummary.Text = "Optimized: " + (final.Winner?.DisplayName ?? "winner")
                    + (winner?.DecodeTokensPerSecond is double decode ? " · " + decode.ToString("0.0") + " decode tok/s" : string.Empty)
                    + (winner?.PromptTokensPerSecond is double prompt ? " · " + prompt.ToString("0") + " prompt tok/s" : string.Empty)
                    + (winner?.TimeToFirstTokenMilliseconds is double ttft ? " · TTFT " + ttft.ToString("0") + " ms" : string.Empty)
                    + (final.SpeculativeEnabled ? " · speculative enabled by measurement" : " · speculative not beneficial");
            }
            catch (Exception ex)
            {
                _performanceSummary.Text = "Optimize for this PC failed: " + ex.Message;
            }
            finally
            {
                _activeOptimizeId = null;
                SetBusy(false, _performanceSummary.Text);
            }
        }

        private async Task RaceRuntimesAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedModelPath))
            {
                await ChoosePerformanceModelAsync();
                if (string.IsNullOrWhiteSpace(_selectedModelPath)) return;
            }
            string backend = _lastRuntimeStatus?.GpuDetected == true ? "text-cuda" : "text-vulkan";
            var dialog = new ContentDialog
            {
                Title = "Race installed llama.cpp builds?",
                Content = "Advanced regression guard: PaRuX benchmarks already-installed managed builds against the same GGUF and promotes another build only if it beats the current build by at least 3%. The text/media runtime must be stopped for the race.",
                PrimaryButtonText = "Run race",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            ResetPerformanceCancellation();
            CancellationToken ct = _performanceCts!.Token;
            SetBusy(true, "Starting runtime build race…");
            try
            {
                RuntimeBuildRaceStatusDto started = await RuntimeManagerClient.Current.StartBuildRaceAsync(new RuntimeBuildRaceRequest
                {
                    Backend = backend,
                    ModelPath = _selectedModelPath!,
                    Workload = SelectedTag(_workload, "chat"),
                    MinimumImprovementPercent = 3
                }, ct);
                _activeRaceId = started.Id;
                RuntimeBuildRaceStatusDto final = await RuntimeManagerClient.Current.WaitForBuildRaceAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() => _performanceSummary.Text = progress.Stage);
                }, ct);
                if (final.Status != "completed") throw new InvalidOperationException(final.Error ?? final.Stage);
                _performanceSummary.Text = final.Stage + " Winner measured: " + (final.WinnerTag ?? "none") + ".";
            }
            catch (Exception ex)
            {
                _performanceSummary.Text = "Runtime build race failed: " + ex.Message;
            }
            finally
            {
                _activeRaceId = null;
                SetBusy(false, _performanceSummary.Text);
            }
        }

        private async Task CancelPerformanceAsync()
        {
            try
            {
                if (_activeOptimizeId != null)
                    await RuntimeManagerClient.Current.CancelOptimizeAsync(_activeOptimizeId, CancellationToken.None);
                if (_activeRaceId != null)
                    await RuntimeManagerClient.Current.CancelBuildRaceAsync(_activeRaceId, CancellationToken.None);
                _performanceCts?.Cancel();
                _performanceSummary.Text = "Performance cancellation requested.";
            }
            catch (Exception ex) { _performanceSummary.Text = "Performance cancellation failed: " + ex.Message; }
        }

        private void ResetPerformanceCancellation()
        {
            _performanceCts?.Cancel();
            _performanceCts?.Dispose();
            _performanceCts = new CancellationTokenSource();
        }

        private RuntimeUpdateSummaryDto? _runtimeUpdate;

        /// <summary>The managed text-runtime build pair the auto-updater tracks for a component (null for other components).</summary>
        private RuntimeUpdateBackendDto? UpdaterBackend(ComponentStatus component)
        {
            string? backend = Backend(component.Id);
            return backend is null or "media-comfyui"
                ? null
                : _runtimeUpdate?.Backends.FirstOrDefault(b => b.Backend == backend && b.Ownership == nameof(ComponentOwnership.PARUX_MANAGED));
        }

        private void Render(IReadOnlyList<ComponentStatus> components)
        {
            _components.Children.Clear();
            foreach (ComponentStatus component in components)
            {
                var card = new Border { Padding = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
                var body = new StackPanel { Spacing = 6 };
                card.Child = body;
                AutomationProperties.SetAutomationId(card, "RuntimeManager_Component_" + component.Id + "_Card");
                AutomationProperties.SetName(card, component.DisplayName + ", " + Ownership(component.Ownership));

                var header = new Grid { ColumnSpacing = 8 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = component.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                header.Children.Add(title);
                var ownership = new TextBlock { Text = Ownership(component.Ownership), FontSize = 11, Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(ownership, 1); header.Children.Add(ownership);
                body.Children.Add(header);

                string state = component.Detected ? component.Health : component.Skipped ? "skipped" : "not configured";
                body.Children.Add(new TextBlock
                {
                    Text = state + (string.IsNullOrWhiteSpace(component.Version) ? string.Empty : " · " + component.Version),
                    FontSize = 12,
                    Opacity = 0.76,
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(component.Path))
                    body.Children.Add(new TextBlock { Text = component.Path, FontSize = 11, Opacity = 0.66, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrWhiteSpace(component.ValidationMessage))
                    body.Children.Add(new TextBlock { Text = component.ValidationMessage, FontSize = 11, Opacity = 0.66, TextWrapping = TextWrapping.Wrap });

                RuntimeUpdateBackendDto? updater = UpdaterBackend(component);
                if (updater != null)
                {
                    string line = "Active build: " + (updater.Active ?? "unknown")
                        + " · Previous build kept: " + (updater.Previous ?? "none")
                        + (updater.Phase is "probation" ? " · new build on probation (rolls back by itself if its first launches fail)" : string.Empty)
                        + (updater.Phase is "pending_activation" ? " · update ready, waiting for the model to be idle" : string.Empty);
                    var buildLine = new TextBlock { Text = line, FontSize = 11, Opacity = 0.76, TextWrapping = TextWrapping.Wrap };
                    AutomationProperties.SetAutomationId(buildLine, "RuntimeManager_Component_" + component.Id + "_Builds_Text");
                    body.Children.Add(buildLine);
                }

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                if (component.Actions.Contains("install"))
                    actions.Children.Add(ActionButton(component.Detected ? "Install / update…" : "Install with PaRuX…", () => InstallAsync(component), "RuntimeManager_Component_" + component.Id + "_Install_Button"));
                if (component.Actions.Contains("rollback") && updater?.PreviousTag is { Length: > 0 } previousTag)
                    actions.Children.Add(ActionButton("Roll back to " + updater.Previous, async () =>
                    {
                        string backend = Backend(component.Id)!;
                        await RuntimeManagerClient.Current.RollbackAsync(backend, previousTag, null, CancellationToken.None, updater.PreviousToolkit);
                        _status.Text = component.DisplayName + " rolled back to " + updater.Previous + ".";
                        await RefreshAsync();
                    }, "RuntimeManager_Component_" + component.Id + "_RollbackPrevious_Button"));
                if (component.Actions.Contains("rollback"))
                    actions.Children.Add(ActionButton("Rollback…", () => RollbackAsync(component), "RuntimeManager_Component_" + component.Id + "_Rollback_Button"));
                if (component.Actions.Contains("locate") || component.Actions.Contains("relocate"))
                    actions.Children.Add(ActionButton(component.Actions.Contains("relocate") ? "Change location…" : "Locate existing…", () => LocateAsync(component), "RuntimeManager_Component_" + component.Id + "_Locate_Button"));
                if (component.Actions.Contains("remove"))
                    actions.Children.Add(ActionButton("Forget location", async () =>
                    {
                        await RuntimeManagerClient.Current.RemoveLocationAsync(component.Id, CancellationToken.None);
                        await RefreshAsync();
                    }, "RuntimeManager_Component_" + component.Id + "_Forget_Button"));
                if (component.Actions.Contains("skip"))
                    actions.Children.Add(ActionButton("Skip for now", async () =>
                    {
                        await RuntimeManagerClient.Current.SkipAsync(component.Id, CancellationToken.None);
                        await RefreshAsync();
                    }, "RuntimeManager_Component_" + component.Id + "_Skip_Button"));
                if (component.Actions.Contains("unskip"))
                    actions.Children.Add(ActionButton("Configure now", async () =>
                    {
                        await RuntimeManagerClient.Current.UnskipAsync(component.Id, CancellationToken.None);
                        await RefreshAsync();
                    }, "RuntimeManager_Component_" + component.Id + "_Unskip_Button"));
                if (actions.Children.Count > 0) body.Children.Add(actions);
                _components.Children.Add(card);
            }
        }

        private Button ActionButton(string label, Func<Task> action, string? automationId = null)
        {
            var button = new Button { Content = label, FontSize = 12 };
            if (automationId != null) AutomationProperties.SetAutomationId(button, automationId);
            button.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception ex) { _status.Text = ex.Message; SetBusy(false, _status.Text); }
            };
            return button;
        }

        private async Task InstallAsync(ComponentStatus component)
        {
            string backend = Backend(component.Id) ?? throw new InvalidOperationException("This component has no PaRuX-managed installer.");
            SetBusy(true, "Loading available builds for " + component.DisplayName + "…");
            List<RuntimeBuildOption> options = await RuntimeManagerClient.Current.GetAvailableBuildsAsync(backend, CancellationToken.None);
            if (options.Count == 0) throw new InvalidOperationException("No compatible Windows builds are currently available.");

            RuntimeBuildOption? selected = await PickBuildAsync("Install / update " + component.DisplayName, options, preferRecommended: true);
            if (selected == null) { SetBusy(false, "Install cancelled."); return; }

            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            CancellationToken ct = _operationCts.Token;
            RuntimeInstallStatus started = await RuntimeManagerClient.Current.StartInstallAsync(backend, selected.Tag, selected.Variant, ct);
            _activeInstallId = started.Id;
            try
            {
                RuntimeInstallStatus final = await RuntimeManagerClient.Current.WaitForInstallAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        var presentation = InstallProgressPresenter.Present(progress, component.DisplayName);
                        _progress.IsIndeterminate = presentation.IsIndeterminate;
                        if (presentation.Fraction.HasValue)
                        {
                            _progress.Minimum = 0;
                            _progress.Maximum = 1;
                            _progress.Value = presentation.Fraction.Value;
                        }
                        _status.Text = presentation.FullText;
                    });
                }, ct);
                if (final.Stage != "completed")
                    throw new InvalidOperationException(final.Error ?? "Install ended as " + final.Stage + ".");

                string variant = string.IsNullOrWhiteSpace(final.Variant) ? string.Empty : " / " + final.Variant;
                if (backend == "media-comfyui" || string.IsNullOrWhiteSpace(final.PerformanceDecision))
                {
                    _status.Text = component.DisplayName + " installed and active: " + final.Tag + variant + ".";
                }
                else
                {
                    string active = string.IsNullOrWhiteSpace(final.ActiveTag) ? "unknown" : final.ActiveTag;
                    _status.Text = component.DisplayName + " candidate installed: " + final.Tag
                        + " · active: " + active
                        + " · performance gate: " + final.PerformanceDecision
                        + (string.IsNullOrWhiteSpace(final.PerformanceMessage) ? string.Empty : " · " + final.PerformanceMessage);
                }
            }
            finally
            {
                _activeInstallId = null;
                SetBusy(false, _status.Text);
                await RefreshAsync();
            }
        }

        private async Task RollbackAsync(ComponentStatus component)
        {
            string backend = Backend(component.Id) ?? throw new InvalidOperationException("This component has no managed rollback surface.");
            List<RuntimeBuildOption> installed = await RuntimeManagerClient.Current.GetInstalledBuildsAsync(backend, CancellationToken.None);
            RuntimeBuildOption? target = await PickBuildAsync("Rollback " + component.DisplayName, installed.Where(x => !x.IsActive).ToList(), preferRecommended: false);
            if (target == null) return;
            await RuntimeManagerClient.Current.RollbackAsync(backend, target.Tag, target.Variant, CancellationToken.None);
            _status.Text = component.DisplayName + " rolled back to " + target.Tag + (string.IsNullOrWhiteSpace(target.Variant) ? string.Empty : " / " + target.Variant) + ".";
            await RefreshAsync();
        }

        private async Task<RuntimeBuildOption?> PickBuildAsync(string title, IReadOnlyList<RuntimeBuildOption> options, bool preferRecommended)
        {
            if (options.Count == 0) { _status.Text = "No alternate installed build is available."; return null; }
            var combo = new ComboBox { MinWidth = 520 };
            foreach (RuntimeBuildOption option in options)
            {
                string flags = (option.IsActive ? " · active" : string.Empty)
                    + (option.InstalledLocally ? " · installed" : string.Empty)
                    + (option.Recommended ? " · recommended" : string.Empty);
                combo.Items.Add(new ComboBoxItem
                {
                    Content = option.Tag
                        + (string.IsNullOrWhiteSpace(option.Variant) ? string.Empty : " / " + option.Variant)
                        + (option.AssetSizeBytes > 0 ? " · " + FormatBytes(option.AssetSizeBytes) : string.Empty)
                        + flags,
                    Tag = option
                });
            }
            int recommendedIndex = preferRecommended ? options.ToList().FindIndex(option => option.Recommended) : -1;
            combo.SelectedIndex = recommendedIndex >= 0 ? recommendedIndex : 0;
            var dialog = new ContentDialog
            {
                Title = title,
                Content = combo,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            return combo.SelectedItem is ComboBoxItem item ? item.Tag as RuntimeBuildOption : null;
        }

        private async Task LocateAsync(ComponentStatus component)
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
            await RuntimeManagerClient.Current.LocateAsync(component.Id, path, CancellationToken.None);
            await RefreshAsync();
        }

        private async Task CancelActiveInstallAsync()
        {
            string? id = _activeInstallId;
            if (id == null) { _status.Text = "No Runtime Manager install is active in this view."; return; }
            try
            {
                await RuntimeManagerClient.Current.CancelInstallAsync(id, CancellationToken.None);
                _operationCts?.Cancel();
                _status.Text = "Cancellation requested.";
            }
            catch (Exception ex) { _status.Text = "Cancellation failed: " + ex.Message; }
        }

        private void SetBusy(bool busy, string message)
        {
            _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy) _progress.IsIndeterminate = true;
            if (!string.IsNullOrWhiteSpace(message)) _status.Text = message;
        }

        private static string SelectedTag(ComboBox combo, string fallback) =>
            combo.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrWhiteSpace(tag) ? tag : fallback;

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Tag is string value
                    && string.Equals(value, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string RuntimeProfileSignature(LlamaPerformanceProfileDto profile) =>
            profile.Id + "|" + profile.BatchSize + "|" + profile.UBatchSize + "|" + profile.ContextSize
            + "|" + profile.CacheTypeK + "|" + profile.SplitMode + "|" + profile.MainGpu
            + "|" + profile.SpeculativeType + "|" + profile.SpeculativeDraftMax;

        private static string? Backend(string componentId) => componentId switch
        {
            "llama-server-cuda" => "text-cuda",
            "llama-server-vulkan" => "text-vulkan",
            "comfyui" => "media-comfyui",
            _ => null
        };

        private static string Ownership(ComponentOwnership ownership) => ownership switch
        {
            ComponentOwnership.PARUX_MANAGED => "PaRuX managed",
            ComponentOwnership.USER_MANAGED => "User managed",
            ComponentOwnership.SYSTEM_DETECTED => "System detected",
            _ => ownership.ToString()
        };

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
    }
}
