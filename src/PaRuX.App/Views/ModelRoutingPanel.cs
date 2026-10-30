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
    /// Model-intelligence/routing surface. It makes capability, throughput and fit separate,
    /// exposes both one-shot and persistent user overrides, and can collect empirical Capability
    /// Profiler evidence for the exact selected candidate. Backend Race is read-only against an
    /// already-running loopback USER_MANAGED OpenAI-compatible backend.
    /// </summary>
    public sealed class ModelRoutingPanel : UserControl
    {
        private readonly ComboBox _role = new ComboBox { MinWidth = 150 };
        private readonly ComboBox _mode = new ComboBox { MinWidth = 190 };
        private readonly ComboBox _installed = new ComboBox { MinWidth = 260, PlaceholderText = "Choose a model…" };
        private readonly Button _addInstalled = new Button { Content = "Add installed model to role", IsEnabled = false };
        private readonly TextBlock _installedStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        private readonly ListView _candidates = new ListView { SelectionMode = ListViewSelectionMode.Single, MinHeight = 180, MaxHeight = 420 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly TextBox _externalEndpoint = new TextBox { PlaceholderText = "http://127.0.0.1:11434", MinWidth = 260 };
        private readonly TextBox _externalModel = new TextBox { PlaceholderText = "External model id", MinWidth = 240 };
        private readonly CheckBox _sameWeights = new CheckBox { Content = "Same weights + quantization confirmed" };
        private readonly CheckBox _sustainedRace = new CheckBox { Content = "Sustained validation" };
        private readonly TextBlock _backendRaceResult = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        private readonly CheckBox _codingAuto = new CheckBox { Content = "Choose the coding model automatically from measured local results" };
        private readonly TextBlock _codingSelection = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        private readonly Button _codingDetailsButton = new Button { Content = "Comparison details", IsEnabled = false };
        private string? _codingDetailsText;
        private bool _codingSelectionLoading;
        private readonly ComboBox _overrideMode = new ComboBox { MinWidth = 240 };
        private readonly ComboBox _overrideModel = new ComboBox { MinWidth = 260, PlaceholderText = "Choose model for override…" };
        private readonly TextBlock _overrideStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        private readonly Button _saveOverride = new Button { Content = "Save override" };
        private bool _overrideEventsSuppressed;
        private List<CandidateEvidenceDto> _current = new List<CandidateEvidenceDto>();
        private List<ManagedModelInfo> _installedModels = new List<ManagedModelInfo>();
        private bool _candidateEvidenceAvailable;
        private string? _candidateLoadError;

        public ModelRoutingPanel()
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_role, "ModelRouting_Role_ComboBox");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_role, "Model role");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_mode, "ModelRouting_Policy_ComboBox");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_mode, "Routing policy");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_installed, "ModelRouting_InstalledModel_ComboBox");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_installed, "Select a managed model for this role");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_candidates, "ModelRouting_Candidates_List");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_status, "ModelRouting_Status_Text");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_installedStatus, "ModelRouting_InstalledModelStatus_Text");
            foreach (string role in ModelRoleCatalog.TextRoles) _role.Items.Add(role);
            _role.SelectedIndex = 0;
            _role.SelectionChanged += async (_, _) => await RefreshAsync();
            _installed.SelectionChanged += (_, _) => UpdateInstalledStatus(null);

            AddMode("Smart · role-aware", RouteMode.Smart);
            AddMode("Quality · capability first", RouteMode.Quality);
            AddMode("Performance · tok/s first", RouteMode.Performance);
            AddMode("Low VRAM · smallest compatible", RouteMode.LowVram);
            AddMode("Manual · configured assignment", RouteMode.Manual);
            _mode.SelectedIndex = 0;

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_codingAuto, "ModelRouting_CodingAuto_CheckBox");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_codingSelection, "ModelRouting_CodingSelection_Text");
            _codingAuto.Checked += async (_, _) => await SetCodingModeAsync(CodingModelSelectionMode.Auto);
            _codingAuto.Unchecked += async (_, _) => await SetCodingModeAsync(CodingModelSelectionMode.Assigned);

#if DEBUG
            if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal))
            {
                CapabilityOverrideClient.Current = new StubCapabilityOverrideClient();
            }
            else
            {
                CapabilityOverrideClient.Current = new HttpCapabilityOverrideClient();
            }
#else
            CapabilityOverrideClient.Current = new HttpCapabilityOverrideClient();
#endif

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_overrideMode, CapabilityOverridePresenter.OverrideComboBoxAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_overrideMode, CapabilityOverridePresenter.OverrideComboBoxLabel);

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_overrideModel, CapabilityOverridePresenter.OverrideModelComboBoxAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_overrideModel, CapabilityOverridePresenter.OverrideModelComboBoxLabel);

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_overrideStatus, CapabilityOverridePresenter.OverrideStatusTextAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_overrideStatus, CapabilityOverridePresenter.OverrideStatusLabel);

            foreach (CapabilityOverrideOptionPresentation opt in CapabilityOverridePresenter.GetStateOptions())
            {
                _overrideMode.Items.Add(new ComboBoxItem
                {
                    Content = opt.Label,
                    Tag = opt.Mode
                });
            }
            _overrideMode.SelectedIndex = 0;
            _overrideMode.SelectionChanged += (_, _) => OnOverrideModeSelectionChanged();
            _overrideModel.SelectionChanged += (_, _) => OnOverrideModelSelectionChanged();
            _saveOverride.Click += async (_, _) => await SaveOverrideAsync();

            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility != Visibility.Visible) return; await RefreshAsync(); await RefreshCodingSelectionAsync(); };
        }

        private UIElement BuildCodingSelectionUi()
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock
            {
                Text = "Coding agent model",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Automatic selection uses measured coding results when enough evidence is available.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.76
            });
            body.Children.Add(_codingAuto);
            body.Children.Add(_codingSelection);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                _codingDetailsButton, "ModelRouting_CodingComparisonDetails_Button");
            _codingDetailsButton.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_codingDetailsText)) return;
                var dialog = new ContentDialog
                {
                    Title = "Coding model comparison",
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = "Measured results: verified-task quality 50%, reliability 30%, speed 20%. "
                                + "Only fitting models with enough local evidence are compared.\n\n"
                                + _codingDetailsText,
                            TextWrapping = TextWrapping.Wrap
                        },
                        MaxHeight = 440
                    },
                    CloseButtonText = "Close",
                    XamlRoot = XamlRoot,
                    MaxWidth = 700
                };
                await dialog.ShowAsync();
            };
            body.Children.Add(_codingDetailsButton);
            return body;
        }

        private async Task RefreshCodingSelectionAsync()
        {
            try
            {
                CodingModelSelectionDto decision = await ModelRoutingClient.Current.GetCodingModelSelectionAsync(CancellationToken.None);
                _codingSelectionLoading = true;
                _codingAuto.IsChecked = decision.Mode == CodingModelSelectionMode.Auto;
                _codingSelectionLoading = false;
                _codingDetailsText = DescribeCodingSelection(decision);
                _codingDetailsButton.IsEnabled = true;
                _codingSelection.Text = "In use for /code: " + (decision.SelectedFileName ?? "(no coding model assigned)")
                    + (decision.EffectiveSource == CodingModelSelectionMode.Auto
                        ? " · automatic measured choice" : " · assigned model")
                    + "\n" + ModelHubRowPresentation.CompactDescription(decision.Reason, 150);
            }
            catch (Exception ex)
            {
                _codingSelectionLoading = false;
                _codingDetailsText = null;
                _codingDetailsButton.IsEnabled = false;
                _codingSelection.Text = "Coding model selection is unavailable: " + ex.Message;
            }
        }

        private async Task SetCodingModeAsync(string mode)
        {
            if (_codingSelectionLoading) return;
            try
            {
                await ModelRoutingClient.Current.SetCodingModelSelectionModeAsync(mode, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _codingSelection.Text = "Could not change coding model selection: " + ex.Message;
                return;
            }
            await RefreshCodingSelectionAsync();
        }

        internal static string DescribeCodingSelection(CodingModelSelectionDto decision)
        {
            var text = new System.Text.StringBuilder();
            text.Append("In use for /code: ").Append(decision.SelectedFileName ?? "(no coding model assigned)")
                .Append(decision.EffectiveSource == CodingModelSelectionMode.Auto ? " · measured choice" : " · assigned model")
                .Append('\n').Append(decision.Reason);
            foreach (CodingModelScoreDto score in decision.Candidates)
            {
                text.Append('\n').Append("• ").Append(score.FileName).Append(": ")
                    .Append(score.Verified).Append('/').Append(score.Attempts).Append(" verified");
                if (score.MedianVerifiedSeconds.HasValue)
                    text.Append(", median ").Append(score.MedianVerifiedSeconds.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" s");
                if (score.Utility.HasValue)
                    text.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        ", utility {0:0.000} (quality {1:0.00}, reliability {2:0.00}, speed {3:0.00})",
                        score.Utility.Value, score.Quality, score.Reliability, score.Speed));
                text.Append(" — ").Append(score.Status);
            }
            return text.ToString();
        }

        public async void OnShown()
        {
            // Hidden at startup, so the Loaded-time refresh is skipped; shown-time must load both parts.
            await RefreshAsync();
            await RefreshCodingSelectionAsync();
        }

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(24),
                MaxWidth = 1050,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;
            body.Children.Add(new TextBlock
            {
                Text = "Model Intelligence & Routing",
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Choose which models PaRuX uses for each task. Keep automatic routing or customize individual roles.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });
            body.Children.Add(new Border
            {
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = BuildCodingSelectionUi()
            });

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(new TextBlock { Text = "Role", VerticalAlignment = VerticalAlignment.Center });
            controls.Children.Add(_role);
            controls.Children.Add(new TextBlock { Text = "Policy", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            controls.Children.Add(_mode);
            var refresh = new Button { Content = "Refresh evidence" };
            refresh.Click += async (_, _) => await RefreshAsync();
            controls.Children.Add(refresh);
            body.Children.Add(controls);

            body.Children.Add(new Border
            {
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = BuildCapabilityOverrideUi()
            });

            body.Children.Add(new Border
            {
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = new CapabilityModesSection()
            });

            body.Children.Add(new Border
            {
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = new ModelArenaSection()
            });

            var registration = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            registration.Children.Add(_installed);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_addInstalled, "ModelRouting_AddInstalledModel_Button");
            _addInstalled.Click += async (_, _) => await AddInstalledCandidateAsync();
            registration.Children.Add(_addInstalled);
            var remove = new Button { Content = "Remove selected candidate" };
            remove.Click += async (_, _) => await RemoveSelectedCandidateAsync();
            registration.Children.Add(remove);
            body.Children.Add(registration);
            body.Children.Add(_installedStatus);
            body.Children.Add(new TextBlock
            {
                Text = "Candidate registration is explicit. Downloading a model does not silently change routing; add it to one or more roles here when you actually want PaRuX to consider it.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.68
            });

            body.Children.Add(_busy);
            body.Children.Add(_candidates);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var auto = new Button { Content = "Use selected policy now" };
            auto.Click += async (_, _) => await ApplyPolicyAsync();
            actions.Children.Add(auto);
            var force = new Button { Content = "Force selected model now" };
            force.Click += async (_, _) => await ForceSelectedAsync();
            actions.Children.Add(force);
            var makeDefault = new Button { Content = "Set as role default" };
            makeDefault.Click += async (_, _) => await SetSelectedAsRoleDefaultAsync();
            actions.Children.Add(makeDefault);
            var resetDefault = new Button { Content = "Reset role default" };
            resetDefault.Click += async (_, _) => await ResetRoleDefaultAsync();
            actions.Children.Add(resetDefault);
            body.Children.Add(actions);

            var evidenceActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var profile = new Button { Content = "Profile selected model" };
            profile.Click += async (_, _) => await ProfileSelectedAsync();
            evidenceActions.Children.Add(profile);
            body.Children.Add(evidenceActions);
            body.Children.Add(new TextBlock
            {
                Text = "Force is one-shot and never substitutes on failure. Set as role default is persistent until reset; for the standard role this also controls ordinary interactive Chat. Profile selected model runs the fixed local correctness battery and replaces the size-only prior with empirical capability evidence.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.70
            });
            body.Children.Add(_status);

            body.Children.Add(new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(16),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = BuildBackendRaceUi()
            });
            return scroll;
        }

        private UIElement BuildBackendRaceUi()
        {
            var body = new StackPanel { Spacing = 9 };
            body.Children.Add(new TextBlock
            {
                Text = "Backend Race · PaRuX vs local OpenAI-compatible runtime",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Select a registered PaRuX candidate above, then compare it with an already-running loopback backend such as Ollama or LM Studio. PaRuX only sends benchmark requests; it never starts, stops, updates or rewrites USER_MANAGED software. A winner is declared only when you confirm the exact same weights and quantization.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_externalEndpoint);
            row.Children.Add(_externalModel);
            body.Children.Add(row);
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            options.Children.Add(_sameWeights);
            options.Children.Add(_sustainedRace);
            var run = new Button { Content = "Run read-only Backend Race" };
            run.Click += async (_, _) => await RunBackendRaceAsync();
            options.Children.Add(run);
            body.Children.Add(options);
            body.Children.Add(_backendRaceResult);
            return body;
        }

        private void AddMode(string label, string tag) =>
            _mode.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

        private async Task RefreshAsync()
        {
            string role = SelectedRole();
            if (string.IsNullOrWhiteSpace(role)) return;
            _candidateEvidenceAvailable = false;
            _addInstalled.IsEnabled = false;
            _installed.IsEnabled = false;
            SetBusy(true, "Loading real candidate evidence…");

#if DEBUG
            if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                && Environment.GetEnvironmentVariable("PARUX_UI_TEST_MODEL_ROUTING") is string fixture
                && fixture.Length > 0)
            {
                _current = new List<CandidateEvidenceDto>();
                _candidateEvidenceAvailable = fixture != "candidate-error";
                _candidateLoadError = _candidateEvidenceAvailable ? null : "Candidate evidence request failed (fixture).";
                _installedModels = fixture is "models" or "candidate-error"
                    ? new List<ManagedModelInfo>
                    {
                        new ManagedModelInfo
                        {
                            FileName = "managed-fixture.gguf",
                            FilePath = @"C:\PaRuX-UiTest\managed-fixture.gguf",
                            Alias = "Routing fixture"
                        }
                    }
                    : new List<ManagedModelInfo>();
                RenderInstalledPicker();
                RenderCandidates();
                _status.Text = "Test fixture loaded.";
                UpdateInstalledStatus(null);
                await RefreshOverrideAsync(role);
                SetBusy(false, _status.Text);
                return;
            }
#endif

            string? candidateError = null;
            string? installedError = null;
            try
            {
                Task<(CandidatePoolResponse? Value, string? Error)> candidatesTask = LoadCandidatesAsync(role);
                Task<(List<ManagedModelInfo>? Value, string? Error)> installedTask = LoadInstalledModelsAsync();
                await Task.WhenAll(candidatesTask, installedTask);
                var candidates = await candidatesTask;
                candidateError = candidates.Error;
                _candidateEvidenceAvailable = candidateError == null;
                _candidateLoadError = candidateError;
                _current = candidates.Value?.Candidates ?? new List<CandidateEvidenceDto>();
                var installed = await installedTask;
                installedError = installed.Error;
                _installedModels = installed.Value ?? new List<ManagedModelInfo>();

                RenderInstalledPicker();
                RenderCandidates();
                UpdateInstalledStatus(installedError);
                await RefreshOverrideAsync(role);
                _status.Text = candidateError != null
                    ? candidateError.Contains("401", StringComparison.Ordinal)
                        ? "PaRuX Core rejected its saved local credentials (HTTP 401). " + candidateError
                        : "Candidate evidence could not be loaded: " + candidateError
                    : _current.Count == 0
                        ? "No registered candidates for " + role + ". Choose an installed GGUF above and add it to this role."
                        : _current.Count + " candidate(s). Capability and throughput are shown independently.";
            }
            catch (Exception ex)
            {
                _candidateEvidenceAvailable = false;
                _addInstalled.IsEnabled = false;
                _installed.IsEnabled = false;
                _status.Text = "Model routing refresh failed: " + ex.Message;
            }
            finally { SetBusy(false, _status.Text); }
        }

        private static async Task<(CandidatePoolResponse? Value, string? Error)> LoadCandidatesAsync(string role)
        {
            try
            {
                return (await ModelRoutingClient.Current.GetCandidatesAsync(role, CancellationToken.None), null);
            }
            catch (Exception ex) { return (null, ex.Message); }
        }

        private static async Task<(List<ManagedModelInfo>? Value, string? Error)> LoadInstalledModelsAsync()
        {
            try
            {
                return (await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None), null);
            }
            catch (Exception ex) { return (null, ex.Message); }
        }

        private void UpdateInstalledStatus(string? error)
        {
            if (!_candidateEvidenceAvailable)
            {
                _installedStatus.Text = "Model selection is disabled until candidate evidence loads. "
                    + (_candidateLoadError ?? error ?? "Refresh this view to retry.");
                return;
            }
            if (error != null)
            {
                _installedStatus.Text = error.Contains("401", StringComparison.Ordinal)
                    ? "PaRuX Core rejected its saved local credentials (HTTP 401). " + error
                    : "Installed models could not be loaded: " + error;
                return;
            }

            _installedStatus.Text = _installed.Items.Count == 0
                ? "No unassigned managed GGUF models are available for this role. Import or install a model in Model Hub, or choose another role."
                : _installed.Items.Count + " eligible managed model(s) available."
                    + ((_installed.SelectedItem as ComboBoxItem)?.Tag is string selected ? " Selected: " + selected + "." : string.Empty);
        }

        private void RenderInstalledPicker()
        {
            string? previous = (_installed.SelectedItem as ComboBoxItem)?.Tag as string;
            _installed.Items.Clear();
            HashSet<string> registered = _current.Select(c => c.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (ManagedModelInfo model in _installedModels
                .Where(model => !registered.Contains(model.FileName))
                .OrderBy(model => model.Alias ?? model.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias,
                    Tag = model.FileName
                };
                ToolTipService.SetToolTip(item, model.FileName + " · " + model.Quantization
                    + (model.LastBenchmarkTokensPerSecond.HasValue
                        ? " · " + model.LastBenchmarkTokensPerSecond.Value.ToString("0.0") + " tok/s" : string.Empty));
                string key = string.Concat(model.FileName.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "ModelRouting_InstalledModel_" + key + "_Item");
                _installed.Items.Add(item);
                if (string.Equals(previous, model.FileName, StringComparison.OrdinalIgnoreCase))
                    _installed.SelectedItem = item;
            }
            if (_installed.SelectedIndex < 0 && _installed.Items.Count > 0) _installed.SelectedIndex = 0;
            _installed.IsEnabled = _candidateEvidenceAvailable && _installed.Items.Count > 0;
            _addInstalled.IsEnabled = _installed.IsEnabled;
        }

        private void RenderCandidates()
        {
            string? previous = (_candidates.SelectedItem as ListViewItem)?.Tag is CandidateEvidenceDto prior
                ? prior.FileName : null;
            _candidates.Items.Clear();
            foreach (CandidateEvidenceDto candidate in _current
                .OrderByDescending(c => c.IntelligenceScore ?? -1)
                .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase))
            {
                string scale = candidate.ParameterBillions.HasValue
                    ? candidate.ParameterBillions.Value.ToString("0.#") + "B" : "scale ?";
                string capability = candidate.CapabilityScore.HasValue
                    ? "measured capability " + (candidate.CapabilityScore.Value * 100).ToString("0.0") + "%"
                    : candidate.IntelligenceScore.HasValue
                        ? "capability estimate " + (candidate.IntelligenceScore.Value * 100).ToString("0.0") + "%"
                        : "capability unmeasured";
                string speed = candidate.BenchmarkTokensPerSecond.HasValue
                    ? candidate.BenchmarkTokensPerSecond.Value.ToString("0.0") + " tok/s" : "speed unmeasured";
                string fit = candidate.HardwareCompatible switch
                {
                    true => "hardware fit",
                    false => "hardware mismatch",
                    _ => "fit unknown"
                };
                string flags = (candidate.IsCurrentAssignment ? " · assigned" : string.Empty)
                    + (candidate.IsCurrentlyLoaded ? " · loaded" : string.Empty);

                var item = new ListViewItem { Tag = candidate };
                var stack = new StackPanel { Spacing = 2, Padding = new Thickness(2, 4, 2, 4) };
                stack.Children.Add(new TextBlock
                {
                    Text = candidate.FileName + " · " + scale + flags,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = capability + " · " + speed + " · " + fit,
                    FontSize = 12,
                    Opacity = 0.76,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = "Capability evidence: " + candidate.IntelligenceEvidence,
                    FontSize = 11,
                    Opacity = 0.62,
                    TextWrapping = TextWrapping.Wrap
                });
                item.Content = stack;
                _candidates.Items.Add(item);
                if (string.Equals(previous, candidate.FileName, StringComparison.OrdinalIgnoreCase))
                    _candidates.SelectedItem = item;
            }
            if (_candidates.SelectedIndex < 0 && _candidates.Items.Count > 0) _candidates.SelectedIndex = 0;
        }

        private CandidateEvidenceDto? SelectedCandidate() =>
            _candidates.SelectedItem is ListViewItem item && item.Tag is CandidateEvidenceDto selected ? selected : null;

        private async Task AddInstalledCandidateAsync()
        {
            if (!_candidateEvidenceAvailable)
            {
                _status.Text = "Candidate evidence is unavailable; refresh before adding a model to this role.";
                return;
            }
            if (_installed.SelectedItem is not ComboBoxItem item || item.Tag is not string fileName)
            {
                _status.Text = "No unregistered installed GGUF is selected.";
                return;
            }
            try
            {
                await ModelRoutingClient.Current.AddCandidateAsync(SelectedRole(), fileName, CancellationToken.None);
                _status.Text = fileName + " added to role " + SelectedRole() + ".";
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = "Could not add candidate: " + ex.Message; }
        }

        private async Task RemoveSelectedCandidateAsync()
        {
            CandidateEvidenceDto? selected = SelectedCandidate();
            if (selected == null) { _status.Text = "Select a candidate first."; return; }
            try
            {
                await ModelRoutingClient.Current.RemoveCandidateAsync(SelectedRole(), selected.FileName, CancellationToken.None);
                _status.Text = selected.FileName + " removed from role " + SelectedRole() + ".";
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = "Could not remove candidate: " + ex.Message; }
        }

        private async Task ApplyPolicyAsync()
        {
            string role = SelectedRole();
            string mode = SelectedMode();
            SetBusy(true, "Applying " + mode + " routing for " + role + "…");
            try
            {
                RouteDecisionDto result = await ModelRoutingClient.Current.ResolveAsync(role, mode, null, CancellationToken.None);
                _status.Text = Describe(result);
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = "Routing failed: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task ForceSelectedAsync()
        {
            CandidateEvidenceDto? selected = SelectedCandidate();
            if (selected == null) { _status.Text = "Select a registered candidate first."; return; }

            string role = SelectedRole();
            var dialog = new ContentDialog
            {
                Title = "Force this model?",
                Content = "The next attachment-free text message sent from Chat will use this local model once. If this conversation has earlier attachments, start a new chat first. Visual turns and external providers cannot use this forced route.\n\n"
                    + selected.FileName + "\n\nIf it does not fit or cannot start, PaRuX will fail instead of silently substituting another model.",
                PrimaryButtonText = "Force model",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            SetBusy(true, "Forcing " + selected.FileName + "…");
            try
            {
                ModelRoutingClient.Current.QueueForcedNextChat(role, selected.FileName);
                _status.Text = selected.FileName + " is queued for the next chat on the " + role + " role.";
            }
            catch (Exception ex) { _status.Text = "Forced model failed: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task SetSelectedAsRoleDefaultAsync()
        {
            CandidateEvidenceDto? selected = SelectedCandidate();
            if (selected == null) { _status.Text = "Select a registered candidate first."; return; }
            string role = SelectedRole();
            try
            {
                SetBusy(true, "Persisting " + selected.FileName + " as " + role + " default…");
                await ModelRolesClient.Current.SetRoleAsync(role, selected.FilePath, CancellationToken.None);
                RouteDecisionDto loaded = await ModelRoutingClient.Current.ResolveAsync(
                    role, RouteMode.Manual, selected.FileName, CancellationToken.None);
                if (!loaded.Success) throw new InvalidOperationException(loaded.Message);
                _status.Text = selected.FileName + " is now the persistent default for " + role
                    + (role == "standard" ? " and therefore ordinary Chat." : ".");
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = "Could not persist role default: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task ResetRoleDefaultAsync()
        {
            string role = SelectedRole();
            try
            {
                SetBusy(true, "Resetting " + role + " to its built-in/default assignment…");
                ModelRoleStatus reset = await ModelRolesClient.Current.ResetRoleAsync(role, CancellationToken.None);
                _status.Text = role + " reset to " + (string.IsNullOrWhiteSpace(reset.ModelPath) ? "its default engine" : Path.GetFileName(reset.ModelPath)) + ".";
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = "Could not reset role default: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task ProfileSelectedAsync()
        {
            CandidateEvidenceDto? selected = SelectedCandidate();
            if (selected == null) { _status.Text = "Select a registered candidate first."; return; }
            string role = SelectedRole();
            SetBusy(true, "Loading exact model before capability profiling…");
            try
            {
                RouteDecisionDto loaded = await ModelRoutingClient.Current.ResolveAsync(
                    role, RouteMode.Manual, selected.FileName, CancellationToken.None);
                if (!loaded.Success) throw new InvalidOperationException(loaded.Message);

                CapabilityProfileRunDto started = await CapabilityProfilerClient.Current.StartRunAsync(CancellationToken.None);
                CapabilityProfileRunDetailDto final = await CapabilityProfilerClient.Current.WaitForRunAsync(
                    started.Id,
                    detail =>
                    {
                        int passed = detail.Results.Count(result => result.Passed);
                        _ = DispatcherQueue.TryEnqueue(() =>
                            _status.Text = "Capability profiling " + selected.FileName + " · " + passed + "/" + detail.Results.Count + " passed so far…");
                    },
                    CancellationToken.None);

                if (final.Run.Status != CapabilityProfileRunStatus.Completed)
                    throw new InvalidOperationException(final.Run.Error ?? "Capability profiling did not complete.");
                if (!PathsEqual(final.Run.ModelPath, selected.FilePath))
                    throw new InvalidOperationException("The active model changed before the profiler captured it; PaRuX discarded this result for the selected candidate.");

                int total = final.Results.Count;
                int pass = final.Results.Count(result => result.Passed);
                string categories = string.Join(", ", final.Results.Select(result =>
                    result.Category + "=" + (result.Passed ? "pass" : "fail")));
                _status.Text = selected.FileName + " empirical capability: " + pass + "/" + total
                    + (total > 0 ? " (" + (100.0 * pass / total).ToString("0.0") + "%)" : string.Empty)
                    + " · " + categories;
                await RefreshAsync();
            }
            catch (Exception ex) { _status.Text = "Capability profiling failed: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task RunBackendRaceAsync()
        {
            CandidateEvidenceDto? selected = SelectedCandidate();
            if (selected == null) { _backendRaceResult.Text = "Select the PaRuX candidate to benchmark first."; return; }
            string endpoint = (_externalEndpoint.Text ?? string.Empty).Trim();
            string externalModel = (_externalModel.Text ?? string.Empty).Trim();
            if (endpoint.Length == 0 || externalModel.Length == 0)
            {
                _backendRaceResult.Text = "Enter the loopback endpoint and exact external model id.";
                return;
            }

            SetBusy(true, "Running read-only Backend Race…");
            _backendRaceResult.Text = "Measuring both serving paths…";
            try
            {
                RuntimeBackendRaceResult result = await ModelRoutingClient.Current.RunBackendRaceAsync(
                    new RuntimeBackendRaceRequest
                    {
                        ManagedModelPath = selected.FilePath,
                        Workload = WorkloadForRole(SelectedRole()),
                        ExternalEndpoint = endpoint,
                        ExternalModel = externalModel,
                        SameWeightsConfirmed = _sameWeights.IsChecked == true,
                        SustainedValidation = _sustainedRace.IsChecked == true
                    }, CancellationToken.None);
                _backendRaceResult.Text = RenderBackendRace(result);
            }
            catch (Exception ex) { _backendRaceResult.Text = "Backend Race failed: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private static string RenderBackendRace(RuntimeBackendRaceResult result)
        {
            var lines = new List<string>();
            foreach (RuntimeBackendRaceCandidateDto candidate in result.Candidates)
            {
                string metrics = candidate.Status == "completed"
                    ? (candidate.DecodeTokensPerSecond?.ToString("0.0") ?? "?") + " decode tok/s"
                        + (candidate.TimeToFirstTokenMilliseconds.HasValue ? " · TTFT " + candidate.TimeToFirstTokenMilliseconds.Value.ToString("0") + " ms" : string.Empty)
                        + (candidate.PromptTokensPerSecond.HasValue ? " · prompt " + candidate.PromptTokensPerSecond.Value.ToString("0") + " tok/s" : string.Empty)
                        + (candidate.SustainedDecodeTokensPerSecond.HasValue ? " · sustained " + candidate.SustainedDecodeTokensPerSecond.Value.ToString("0.0") : string.Empty)
                    : candidate.Status + (string.IsNullOrWhiteSpace(candidate.Error) ? string.Empty : " · " + candidate.Error);
                lines.Add(candidate.Backend + " · " + candidate.Model + " · " + metrics);
            }
            lines.Add(result.Winner == null ? "Winner: not declared" : "Winner: " + result.Winner);
            lines.Add(result.Explanation);
            return string.Join("\n", lines);
        }

        private static string WorkloadForRole(string role) => role switch
        {
            "coding" => "coding",
            "reasoning" or "planner" or "reviewer" => "long-context",
            _ => "chat"
        };

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static string Describe(RouteDecisionDto result)
        {
            string model = string.IsNullOrWhiteSpace(result.ModelPath)
                ? "no model" : Path.GetFileName(result.ModelPath);
            string quality = result.SelectedIntelligenceScore.HasValue
                ? " · capability " + (result.SelectedIntelligenceScore.Value * 100).ToString("0.0") + "%" : string.Empty;
            string speed = result.SelectedBenchmarkTokensPerSecond.HasValue
                ? " · " + result.SelectedBenchmarkTokensPerSecond.Value.ToString("0.0") + " tok/s" : string.Empty;
            return (result.Success ? "Selected " : "Selection failed for ") + model
                + quality + speed + "\n" + (result.SelectionReason ?? result.Message)
                + (result.SelectionReason == null ? string.Empty : "\n" + result.Message);
        }

        private string SelectedRole() => _role.SelectedItem as string ?? "standard";

        private string SelectedMode() =>
            _mode.SelectedItem is ComboBoxItem item && item.Tag is string value ? value : RouteMode.Smart;

        private void SetBusy(bool busy, string message)
        {
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(message)) _status.Text = message;
        }
    
        private UIElement BuildCapabilityOverrideUi()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Advanced capability overrides",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Configure a scoped routing override for this role: Automatic (PaRuX decides), Prefer (compatible model with fallback), or Force (strict model, fails if incompatible).",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });

            var controlsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controlsRow.Children.Add(new TextBlock { Text = CapabilityOverridePresenter.OverrideComboBoxLabel, VerticalAlignment = VerticalAlignment.Center });
            controlsRow.Children.Add(_overrideMode);
            controlsRow.Children.Add(new TextBlock { Text = CapabilityOverridePresenter.OverrideModelComboBoxLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            controlsRow.Children.Add(_overrideModel);
            controlsRow.Children.Add(_saveOverride);
            panel.Children.Add(controlsRow);

            panel.Children.Add(_overrideStatus);

            panel.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width < 680)
                {
                    controlsRow.Orientation = Orientation.Vertical;
                }
                else
                {
                    controlsRow.Orientation = Orientation.Horizontal;
                }
            };

            return panel;
        }

        private void OnOverrideModeSelectionChanged()
        {
            if (_overrideEventsSuppressed) return;
            string mode = SelectedOverrideMode();
            bool requiresModel = CapabilityOverridePresenter.RequiresModelSelection(mode);
            _overrideModel.IsEnabled = requiresModel;

            string? selectedModel = SelectedOverrideModel();
            CapabilityOverridePresentation pres = CapabilityOverridePresenter.Present(mode, selectedModel);
            _overrideStatus.Text = pres.StatusText;
        }

        private void OnOverrideModelSelectionChanged()
        {
            if (_overrideEventsSuppressed) return;
            string mode = SelectedOverrideMode();
            string? selectedModel = SelectedOverrideModel();
            CapabilityOverridePresentation pres = CapabilityOverridePresenter.Present(mode, selectedModel);
            _overrideStatus.Text = pres.StatusText;
        }

        private async Task SaveOverrideAsync()
        {
            string role = SelectedRole();
            string mode = SelectedOverrideMode();
            string? selectedModel = SelectedOverrideModel();

            CapabilityOverrideValidationResult validation = CapabilityOverridePresenter.Validate(mode, selectedModel);
            if (!validation.IsValid)
            {
                _overrideStatus.Text = validation.ErrorMessage ?? "Invalid override selection.";
                return;
            }

            if (CapabilityOverridePresenter.RequiresConfirmation(mode))
            {
                var dialog = new ContentDialog
                {
                    Title = CapabilityOverridePresenter.ForceConfirmationTitle,
                    Content = CapabilityOverridePresenter.ForceWarningText + "\n\n"
                        + "Requests for " + role + " will use only " + selectedModel
                        + " and will fail rather than use another model if it cannot run.\n\nAre you sure you want to force this model?",
                    PrimaryButtonText = "Force model",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    _overrideStatus.Text = "Force override cancelled.";
                    return;
                }
            }

            SetBusy(true, "Saving capability override for " + role + "…");
            try
            {
                var request = new SetScopedRoutingOverrideRequest
                {
                    Mode = mode,
                    ManagedFileName = CapabilityOverridePresenter.RequiresModelSelection(mode) ? selectedModel : null
                };

                await CapabilityOverrideClient.Current.SetOverrideAsync(
                    role, CapabilityOverridePresenter.DefaultProfile, request, CancellationToken.None);
                _overrideStatus.Text = CapabilityOverridePresenter.FormatSaveSuccess(role, mode, selectedModel);
            }
            catch (Exception ex)
            {
                _overrideStatus.Text = "Could not save override: " + ex.Message;
            }
            finally
            {
                SetBusy(false, _overrideStatus.Text);
            }
        }

        private async Task RefreshOverrideAsync(string role)
        {
            _overrideEventsSuppressed = true;
            try
            {
                PopulateOverrideModels();

                ScopedRoutingOverrideDto? existing = null;
                try
                {
                    existing = await CapabilityOverrideClient.Current.GetOverrideAsync(
                        role, CapabilityOverridePresenter.DefaultProfile, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _overrideStatus.Text = "Override settings could not be loaded: " + ex.Message;
                }

                if (existing != null)
                {
                    SelectOverrideModeByTag(existing.Mode);
                    if (!string.IsNullOrWhiteSpace(existing.ManagedFileName))
                    {
                        SelectOverrideModelByName(existing.ManagedFileName);
                    }
                    _overrideModel.IsEnabled = CapabilityOverridePresenter.RequiresModelSelection(existing.Mode);
                    string? model = SelectedOverrideModel();
                    CapabilityOverridePresentation pres = CapabilityOverridePresenter.Present(existing.Mode, model);
                    _overrideStatus.Text = pres.StatusText;
                }
                else
                {
                    SelectOverrideModeByTag(ScopedRouteMode.Automatic);
                    _overrideModel.IsEnabled = false;
                    _overrideStatus.Text = CapabilityOverridePresenter.AutomaticExplanation;
                }
            }
            finally
            {
                _overrideEventsSuppressed = false;
            }
        }

        private void PopulateOverrideModels()
        {
            string? previous = SelectedOverrideModel();
            _overrideModel.Items.Clear();
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CandidateEvidenceDto c in _current) all.Add(c.FileName);
            foreach (ManagedModelInfo m in _installedModels) all.Add(m.FileName);

            foreach (string fileName in all.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                _overrideModel.Items.Add(new ComboBoxItem
                {
                    Content = fileName,
                    Tag = fileName
                });
            }

            if (previous != null) SelectOverrideModelByName(previous);
            else if (_overrideModel.Items.Count > 0) _overrideModel.SelectedIndex = 0;
        }

        private string SelectedOverrideMode() =>
            (_overrideMode.SelectedItem as ComboBoxItem)?.Tag as string ?? ScopedRouteMode.Automatic;

        private string? SelectedOverrideModel() =>
            (_overrideModel.SelectedItem as ComboBoxItem)?.Tag as string;

        private void SelectOverrideModeByTag(string tag)
        {
            string normalized = CapabilityOverridePresenter.NormalizeMode(tag);
            for (int i = 0; i < _overrideMode.Items.Count; i++)
            {
                if (_overrideMode.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _overrideMode.SelectedIndex = i;
                    return;
                }
            }
            if (_overrideMode.Items.Count > 0) _overrideMode.SelectedIndex = 0;
        }

        private void SelectOverrideModelByName(string fileName)
        {
            for (int i = 0; i < _overrideModel.Items.Count; i++)
            {
                if (_overrideModel.Items[i] is ComboBoxItem item
                    && string.Equals(item.Tag as string, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    _overrideModel.SelectedIndex = i;
                    return;
                }
            }
            var missing = new ComboBoxItem { Content = fileName, Tag = fileName };
            _overrideModel.Items.Add(missing);
            _overrideModel.SelectedItem = missing;
        }
    }
}
