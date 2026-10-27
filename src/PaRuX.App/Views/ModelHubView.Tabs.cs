using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Model Hub tabs (Text models / Media / Downloads), the Media engine cards, and "Add a checkpoint from Hugging Face".
    /// The Text panel is the page that existed before; Media and Downloads receive their existing sections unchanged.</summary>
    public sealed partial class ModelHubView
    {
        private readonly Dictionary<ModelHubTab, UIElement> _tabPanels = new();
        private readonly Dictionary<ModelHubTab, SelectorBarItem> _tabItems = new();
        private SelectorBar? _tabBar;
        private ModelHubTab _activeTab = ModelHubTab.Text;

        private readonly StackPanel _engines = new StackPanel { Spacing = 10 };
        private readonly Dictionary<string, MediaEngineHubCard> _engineCards = new(StringComparer.Ordinal);
        private DispatcherTimer? _enginePoll;
        private bool _enginePollInFlight;

        private readonly TextBox _hfRepo = new TextBox { PlaceholderText = "owner/name (Hugging Face repository)", MinWidth = 260 };
        private readonly TextBox _hfFile = new TextBox { PlaceholderText = "path/inside/repo/model.safetensors", MinWidth = 320 };
        private readonly ComboBox _hfType = new ComboBox { MinWidth = 130 };
        private readonly Button _hfButton = new Button { Content = "Check and download" };
        private readonly TextBlock _hfStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly StackPanel _hfRows = new StackPanel { Spacing = 8 };
        // File name -> workflow type of the checkpoints queued from this form, so their download state and Install buttons can be shown.
        private readonly Dictionary<string, string> _hfQueued = new(StringComparer.OrdinalIgnoreCase);

        private void BuildHubTabs(StackPanel outer, StackPanel textPanel)
        {
            _tabBar = new SelectorBar();
            AutomationProperties.SetAutomationId(_tabBar, "ModelHub_Tabs");
            foreach (ModelHubTab tab in ModelHubTabs.All)
            {
                var item = new SelectorBarItem { Text = ModelHubTabs.Label(tab) };
                AutomationProperties.SetAutomationId(item, ModelHubTabs.AutomationId(tab));
                _tabBar.Items.Add(item);
                _tabItems[tab] = item;
            }

            var mediaPanel = new StackPanel { Spacing = 18 };
            mediaPanel.Children.Add(Section("ENGINES · COMFYUI (IMAGE / VIDEO / EDIT), SPEECH, IMAGE-TO-3D, MUSIC", _engines));
            mediaPanel.Children.Add(_mediaSection!);
            mediaPanel.Children.Add(BuildHuggingFaceSection());
            var downloadsPanel = new StackPanel { Spacing = 18 };
            downloadsPanel.Children.Add(_downloadsSection!);

            _tabPanels[ModelHubTab.Text] = textPanel;
            _tabPanels[ModelHubTab.Media] = mediaPanel;
            _tabPanels[ModelHubTab.Downloads] = downloadsPanel;

            outer.Children.Add(_tabBar);
            outer.Children.Add(_busy);
            outer.Children.Add(_status);
            outer.Children.Add(textPanel);
            outer.Children.Add(mediaPanel);
            outer.Children.Add(downloadsPanel);

            _tabBar.SelectedItem = _tabItems[ModelHubTab.Text];
            _tabBar.SelectionChanged += (_, _) =>
            {
                ModelHubTab selected = _tabItems.FirstOrDefault(p => ReferenceEquals(p.Value, _tabBar.SelectedItem)).Key;
                SelectHubTab(selected);
            };
            SelectHubTab(ModelHubTab.Text);

            foreach (MediaEngineDefinition engine in MediaEngineHubPresenter.Engines)
                _engines.Children.Add(BuildEngineCardShell(engine));
            Unloaded += (_, _) => _enginePoll?.Stop();
        }

        private void SelectHubTab(ModelHubTab tab)
        {
            _activeTab = tab;
            foreach (KeyValuePair<ModelHubTab, UIElement> pair in _tabPanels)
                pair.Value.Visibility = pair.Key == tab ? Visibility.Visible : Visibility.Collapsed;
            if (tab == ModelHubTab.Media) StartEnginePolling();
            else _enginePoll?.Stop();
        }

        // ---- Engines -------------------------------------------------------------------------------------------------

        private Border BuildEngineCardShell(MediaEngineDefinition engine)
        {
            var card = new Border
            {
                Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Tag = engine.Type
            };
            AutomationProperties.SetAutomationId(card, MediaEngineHubPresenter.AutomationIdFor(engine.Type));
            AutomationProperties.SetName(card, engine.Title);
            RenderEngineCard(card, MediaEngineHubPresenter.Unavailable(engine, "Checking…") with { ButtonEnabled = false, StatusLine = "Checking" });
            return card;
        }

        private void StartEnginePolling()
        {
            _ = RefreshEnginesAsync();
            _enginePoll ??= new DispatcherTimer();
            _enginePoll.Tick -= EnginePollTick;
            _enginePoll.Tick += EnginePollTick;
            bool anyRunning = _engineCards.Values.Any(c => c.IsCancel);
            _enginePoll.Interval = TimeSpan.FromSeconds(anyRunning ? 2 : 6);
            _enginePoll.Start();
        }

        private async void EnginePollTick(object? sender, object e)
        {
            if (_activeTab != ModelHubTab.Media) { _enginePoll?.Stop(); return; }
            await RefreshEnginesAsync();
            if (_enginePoll != null) _enginePoll.Interval = TimeSpan.FromSeconds(_engineCards.Values.Any(c => c.IsCancel) ? 2 : 6);
        }

        private async Task RefreshEnginesAsync()
        {
            if (_enginePollInFlight) return;
            _enginePollInFlight = true;
            try
            {
                foreach (Border shell in _engines.Children.OfType<Border>().ToList())
                {
                    if (_activeTab != ModelHubTab.Media) return;
                    MediaEngineDefinition engine = MediaEngineHubPresenter.Engines.First(e => e.Type == (string)shell.Tag);
                    MediaEngineHubCard model;
                    try { model = MediaEngineHubPresenter.Present(engine, await MediaSetupClient.Current.GetAsync(engine.Type, CancellationToken.None)); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { model = MediaEngineHubPresenter.Unavailable(engine, CustomCheckpointPresenter.ExtractServerError(ex.Message)); }
                    RenderEngineCard(shell, model);
                }
            }
            finally { _enginePollInFlight = false; }
        }

        private void RenderEngineCard(Border shell, MediaEngineHubCard model)
        {
            _engineCards[model.Type] = model;
            string id = model.AutomationId;
            var panel = new StackPanel { Spacing = 4 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(new TextBlock { Text = model.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var state = new TextBlock { Text = (model.Installed ? "Installed" : model.StatusLine), FontSize = 12, Opacity = model.Installed ? 1.0 : 0.8 };
            AutomationProperties.SetAutomationId(state, id + "_Status");
            head.Children.Add(state);
            panel.Children.Add(head);
            if (model.SizeLine.Length > 0) panel.Children.Add(new TextBlock { Text = model.SizeLine, FontSize = 12, Opacity = 0.8 });
            if (model.LicenseLine.Length > 0) panel.Children.Add(new TextBlock { Text = model.LicenseLine, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
            if (model.Detail.Length > 0) panel.Children.Add(new TextBlock { Text = model.Detail, FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
            if (model.ShowProgress)
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = model.Indeterminate };
                if (!model.Indeterminate) bar.Value = model.Progress;
                AutomationProperties.SetAutomationId(bar, id + "_Progress");
                panel.Children.Add(bar);
            }
            var button = new Button { Content = model.ButtonLabel, IsEnabled = model.ButtonEnabled };
            AutomationProperties.SetAutomationId(button, id + "_Button");
            AutomationProperties.SetName(button, model.ButtonLabel + " " + model.Title);
            string type = model.Type;
            bool cancel = model.IsCancel;
            button.Click += async (_, _) => await EngineButtonAsync(type, cancel, button);
            panel.Children.Add(button);
            shell.Child = panel;
        }

        private async Task EngineButtonAsync(string type, bool cancel, Button button)
        {
            button.IsEnabled = false;
            try
            {
                MediaSetupStatus next = cancel
                    ? await MediaSetupClient.Current.CancelAsync(type, CancellationToken.None)
                    : await MediaSetupClient.Current.StartAsync(type, CancellationToken.None);
                MediaEngineDefinition engine = MediaEngineHubPresenter.Engines.First(e => e.Type == type);
                Border? shell = _engines.Children.OfType<Border>().FirstOrDefault(b => (string)b.Tag == type);
                if (shell != null) RenderEngineCard(shell, MediaEngineHubPresenter.Present(engine, next));
                _enginePoll?.Stop();
                StartEnginePolling();
            }
            catch (Exception ex)
            {
                _status.Text = "Engine request failed: " + CustomCheckpointPresenter.ExtractServerError(ex.Message);
                button.IsEnabled = true;
            }
        }

        // ---- Add a checkpoint from Hugging Face ----------------------------------------------------------------------

        private Border BuildHuggingFaceSection()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Add a fine-tune (including uncensored ones) of a model family PaRuX can run. Enter the repository and the exact file path inside it. "
                    + "PaRuX reads the repository listing itself, pins the commit, asks you to review the licence, downloads it and checks the model family when it finishes. "
                    + "A file from a family the workflow cannot run is refused and deleted.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
            });
            foreach (string type in HuggingFaceCheckpointForm.Types) _hfType.Items.Add(type);
            _hfType.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_hfRepo, "ModelHub_HfCheckpoint_Repo");
            AutomationProperties.SetName(_hfRepo, "Hugging Face repository");
            AutomationProperties.SetAutomationId(_hfFile, "ModelHub_HfCheckpoint_File");
            AutomationProperties.SetName(_hfFile, "File path inside the repository");
            AutomationProperties.SetAutomationId(_hfType, "ModelHub_HfCheckpoint_Type");
            AutomationProperties.SetName(_hfType, "Capability type");
            AutomationProperties.SetAutomationId(_hfButton, "ModelHub_HfCheckpoint_Button");
            AutomationProperties.SetAutomationId(_hfStatus, "ModelHub_HfCheckpoint_Status");
            _hfButton.Click += async (_, _) => await AddHuggingFaceCheckpointAsync();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_hfRepo);
            row.Children.Add(_hfFile);
            row.Children.Add(_hfType);
            row.Children.Add(_hfButton);
            panel.Children.Add(row);
            panel.Children.Add(_hfStatus);
            panel.Children.Add(_hfRows);
            return Section("ADD A CHECKPOINT FROM HUGGING FACE", panel);
        }

        private async Task AddHuggingFaceCheckpointAsync()
        {
            string repo = _hfRepo.Text.Trim();
            string file = _hfFile.Text.Trim();
            string type = _hfType.SelectedItem as string ?? string.Empty;
            string? invalid = HuggingFaceCheckpointForm.Validate(repo, file, type);
            if (invalid != null) { _hfStatus.Text = invalid; return; }

            _hfButton.IsEnabled = false;
            _hfStatus.Text = "Reading the repository listing…";
            try
            {
                HuggingFaceCheckpointResolution resolved = await MediaCheckpointsClient.Current.ResolveHuggingFaceAsync(repo, file, CancellationToken.None);
                bool queued = await ModelLicenseConsentPrompt.ConfirmAndStartRepositoryDownloadAsync(
                    XamlRoot, resolved.RepoId, resolved.FileName,
                    async (revision, licenseEvidence) =>
                    {
                        if (!string.Equals(revision, resolved.Revision, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The repository changed while you were reviewing it. Try again.");
                        var download = new ModelDownloadRequest
                        {
                            RepoId = resolved.RepoId,
                            FileName = resolved.FileName,
                            DownloadUrl = resolved.DownloadUrl,
                            SizeBytes = resolved.SizeBytes,
                            MediaCheckpointType = type,
                            LicenseEvidence = licenseEvidence
                        };
                        ModelDownloadClient.PinRequestToRevision(download, resolved.Revision);
                        await ModelDownloadClient.Current.StartDownloadAsync(download, CancellationToken.None);
                    });
                if (!queued) { _hfStatus.Text = "Cancelled. Nothing was downloaded."; return; }
                _hfQueued[resolved.FileName] = type;
                _hfStatus.Text = "Queued " + resolved.FileName + " (" + MediaHubPresenter.FormatBytes(resolved.SizeBytes) + ", commit " + resolved.Revision[..8] + "). Progress is shown below and in Downloads.";
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _hfStatus.Text = "Could not add the checkpoint: " + CustomCheckpointPresenter.ExtractServerError(ex.Message);
            }
            finally { _hfButton.IsEnabled = true; }
        }

        private void RenderHuggingFaceRows(IReadOnlyList<ModelDownloadStatus> downloads)
        {
            _hfRows.Children.Clear();
            foreach (KeyValuePair<string, string> queued in _hfQueued.ToList())
            {
                ModelDownloadStatus? job = downloads.Where(d => string.Equals(d.FileName, queued.Key, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.CreatedUtc).FirstOrDefault();
                CustomCheckpointRow row = CustomCheckpointPresenter.Present(queued.Key, queued.Value, job);
                var item = new StackPanel { Spacing = 4 };
                AutomationProperties.SetAutomationId(item, "ModelHub_HfCheckpoint_Row_" + queued.Key);
                item.Children.Add(new TextBlock { Text = queued.Key + " · " + HuggingFaceCheckpointForm.TypeLabel(queued.Value), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                item.Children.Add(new TextBlock { Text = row.StatusText, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });
                if (row.CanInstall)
                {
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    string name = queued.Key, type = queued.Value;
                    var auto = new Button { Content = "Install for automatic use" };
                    AutomationProperties.SetName(auto, "Install " + name + " for automatic use");
                    auto.Click += async (_, _) => await InstallCustomCheckpointAsync(name, type, pin: false);
                    var use = new Button { Content = "Install and use this model" };
                    AutomationProperties.SetName(use, "Install " + name + " and use it");
                    use.Click += async (_, _) => await InstallCustomCheckpointAsync(name, type, pin: true);
                    buttons.Children.Add(auto);
                    buttons.Children.Add(use);
                    item.Children.Add(buttons);
                }
                else if (row.Failed)
                {
                    var dismiss = new Button { Content = "Dismiss" };
                    string name = queued.Key;
                    dismiss.Click += (_, _) => { _hfQueued.Remove(name); RenderHuggingFaceRows(_downloadJobs); };
                    item.Children.Add(dismiss);
                }
                _hfRows.Children.Add(item);
            }
        }

        private async Task InstallCustomCheckpointAsync(string fileName, string type, bool pin)
        {
            try
            {
                SetBusy(true, "Installing " + fileName + "…");
                if (pin) await MediaCheckpointsClient.Current.ActivateAsync(type, fileName, CancellationToken.None);
                else await MediaCheckpointsClient.Current.InstallForAutomaticAsync(type, fileName, CancellationToken.None);
                _hfQueued.Remove(fileName);
                _status.Text = fileName + (pin ? " is installed and in use for " : " is installed for automatic use for ") + type + ".";
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "Install failed: " + CustomCheckpointPresenter.ExtractServerError(ex.Message);
                SetBusy(false, _status.Text);
            }
        }
    }
}
