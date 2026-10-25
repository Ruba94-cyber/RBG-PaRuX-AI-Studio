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
    /// <summary>
    /// One coherent Dataset → Eval → Train → Adapter → AutoLab workspace over the existing PaRuX
    /// Core. Semantic mappings are always explicit. The only automatic Dataset→Training transform
    /// is Eval→SFT, after the user has already explicitly mapped prompt/expected behavior; DPO is
    /// never synthesized because Eval cases do not contain a rejected response.
    /// </summary>
    public sealed partial class LifecycleLabView : UserControl
    {
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };

        private readonly TextBox _datasetSearch = new() { PlaceholderText = "Search Hugging Face datasets" };
        private readonly StackPanel _datasetResults = new() { Spacing = 8 };
        private readonly StackPanel _datasetDownloads = new() { Spacing = 8 };
        private readonly TextBox _sourcePath = new() { PlaceholderText = "Downloaded CSV, JSON, JSONL or Parquet path" };
        private readonly TextBlock _inspectSummary = new() { TextWrapping = TextWrapping.Wrap };
        // IsEnabled starts false: confirmed live that clicking an empty ComboBox (zero items,
        // nothing to select before Inspect has run) can leave its ExpandCollapse automation state
        // stuck "Expanded" with no popup ever actually shown - since there is nothing to render in
        // the dropdown, the normal open/close transition that restores the placeholder-text visual
        // state never fires, so the field appears permanently blank until an unrelated page
        // refresh happens to reset it. Disabling until PopulateColumnMappings gives it real items
        // both fixes the stuck-blank rendering (a disabled control can't open a popup at all) and
        // is more honest UX: there is nothing to choose here until a dataset has been inspected.
        private readonly ComboBox _promptColumn = new() { PlaceholderText = "Prompt column", MinWidth = 220, IsEnabled = false };
        private readonly ComboBox _expectedColumn = new() { PlaceholderText = "Expected behavior column", MinWidth = 220, IsEnabled = false };
        private readonly ComboBox _systemColumn = new() { PlaceholderText = "Optional system-prompt column", MinWidth = 220, IsEnabled = false };

        private readonly ComboBox _evalDataset = new() { PlaceholderText = "Eval dataset", MinWidth = 260, IsEnabled = false };
        private readonly TextBox _newEvalName = new() { PlaceholderText = "New Eval dataset name", MinWidth = 260 };
        // Same closed, server-validated role set as the promote dialog: offer the real roles
        // rather than asking the user to remember and retype one. The first entry keeps the
        // documented default of running against whatever model is already loaded.
        private const string EvalRoleAutomatic = "Run against the loaded model";
        private readonly ComboBox _evalRole = new() { MinWidth = 260 };
        private readonly StackPanel _evalSummary = new() { Spacing = 6 };

        private readonly TextBlock _trainingEngine = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _trainingPath = new() { PlaceholderText = "SFT chat-format or DPO JSONL path" };
        private readonly TextBox _baseModel = new()
        {
            Text = "unsloth/Qwen2.5-0.5B-Instruct-bnb-4bit",
            PlaceholderText = "Unsloth base model id"
        };
        private readonly ComboBox _trainingMethod = new() { MinWidth = 140 };
        private readonly NumberBox _steps = new() { Value = 60, Minimum = 1, Maximum = 100000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        private readonly NumberBox _rank = new() { Value = 16, Minimum = 1, Maximum = 1024, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        private readonly StackPanel _trainingJobs = new() { Spacing = 6 };

        private readonly TextBlock _conversionStatus = new() { TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _adapters = new() { Spacing = 8 };

        private readonly TextBox _sweepRoles = new() { Text = "standard,reasoning,coding", PlaceholderText = "Comma-separated text roles" };
        private readonly StackPanel _experiments = new() { Spacing = 8 };
        private string? _recommendedExperimentId;
        private string? _lastEvalRunId;
        private string? _lastEvalRunDatasetId;
        private CancellationTokenSource? _refreshCts;

        public LifecycleLabView()
        {
            _trainingMethod.Items.Add(new ComboBoxItem { Content = "SFT", Tag = TrainingMethod.Sft });
            _trainingMethod.Items.Add(new ComboBoxItem { Content = "DPO", Tag = TrainingMethod.Dpo });
            _trainingMethod.SelectedIndex = 0;
            _evalRole.Items.Add(EvalRoleAutomatic);
            foreach (string known in ModelRoleCatalog.TextRoles) _evalRole.Items.Add(known);
            _evalRole.SelectedIndex = 0;
            AssignAutomationIds();
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAllAsync(); };
            Unloaded += (_, _) => _refreshCts?.Cancel();
        }

        private static void Id(DependencyObject element, string id, string? name = null)
        {
            AutomationProperties.SetAutomationId(element, id);
            if (name != null) AutomationProperties.SetName(element, name);
        }

        private void AssignAutomationIds()
        {
            Id(_status, "LifecycleLab_Status_Text", "Lifecycle Lab status");
            Id(_busy, "LifecycleLab_Busy_ProgressBar", "Lifecycle Lab loading indicator");
            Id(_datasetSearch, "LifecycleLab_DatasetSearch_TextBox", "Search Hugging Face datasets");
            Id(_datasetResults, "LifecycleLab_DatasetResults_List");
            Id(_datasetDownloads, "LifecycleLab_DatasetDownloads_List");
            Id(_sourcePath, "LifecycleLab_SourcePath_TextBox", "Downloaded dataset path");
            Id(_inspectSummary, "LifecycleLab_InspectSummary_Text", "Dataset inspection summary");
            Id(_promptColumn, "LifecycleLab_PromptColumn_ComboBox", "Prompt column");
            Id(_expectedColumn, "LifecycleLab_ExpectedColumn_ComboBox", "Expected behavior column");
            Id(_systemColumn, "LifecycleLab_SystemColumn_ComboBox", "Optional system-prompt column");
            Id(_evalDataset, "LifecycleLab_EvalDataset_ComboBox", "Eval dataset");
            Id(_newEvalName, "LifecycleLab_NewEvalName_TextBox", "New Eval dataset name");
            Id(_evalRole, "LifecycleLab_EvalRole_ComboBox", "Eval run role");
            Id(_evalSummary, "LifecycleLab_EvalSummary_List");
            Id(_trainingEngine, "LifecycleLab_TrainingEngine_Text", "Training runtime status");
            Id(_trainingPath, "LifecycleLab_TrainingPath_TextBox", "Training dataset path");
            Id(_baseModel, "LifecycleLab_BaseModel_TextBox", "Unsloth base model id");
            Id(_trainingMethod, "LifecycleLab_TrainingMethod_ComboBox", "Training method");
            Id(_steps, "LifecycleLab_Steps_NumberBox", "Training steps");
            Id(_rank, "LifecycleLab_LoraRank_NumberBox", "LoRA rank");
            Id(_trainingJobs, "LifecycleLab_TrainingJobs_List");
            Id(_conversionStatus, "LifecycleLab_ConversionStatus_Text", "Adapter conversion status");
            Id(_adapters, "LifecycleLab_Adapters_List");
            Id(_sweepRoles, "LifecycleLab_SweepRoles_TextBox", "Comparison sweep roles");
            Id(_experiments, "LifecycleLab_Experiments_List");
            Id(_planSummary, "LifecycleLab_PlanSummary_Text", "Training plan summary");
            Id(_planRecommendation, "LifecycleLab_PlanRecommendation_Text", "Training plan recommendation");
            Id(_planFindings, "LifecycleLab_PlanFindings_List");
            Id(_applyRecommended, "LifecycleLab_UseRecommended_Button");
            Id(_learningRate, "LifecycleLab_LearningRate_NumberBox", "Learning rate");
            Id(_maxSequenceLength, "LifecycleLab_MaxSequence_NumberBox", "Max sequence length");
            Id(_dpoBeta, "LifecycleLab_DpoBeta_NumberBox", "DPO beta");
            Id(_grpoReward, "LifecycleLab_GrpoReward_ComboBox", "GRPO reward");
            Id(_grpoGenerations, "LifecycleLab_GrpoGenerations_NumberBox", "GRPO generations");
            Id(_textField, "LifecycleLab_TextField_TextBox");
            Id(_imageField, "LifecycleLab_ImageField_TextBox");
            Id(_audioField, "LifecycleLab_AudioField_TextBox");
            Id(_methodRequirements, "LifecycleLab_MethodRequirements_Text", "Training method requirements");
        }

        public async void OnShown() => await RefreshAllAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(24),
                MaxWidth = 1100,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            var titleRow = new Grid { ColumnSpacing = 8 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = "Lifecycle Lab", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Id(title, "LifecycleLab_Title_Text");
            Grid.SetColumn(title, 0); titleRow.Children.Add(title);
            var refresh = new Button { Content = "Refresh all" };
            Id(refresh, "LifecycleLab_RefreshAll_Button");
            refresh.Click += async (_, _) => await RefreshAllAsync();
            Grid.SetColumn(refresh, 1); titleRow.Children.Add(refresh);
            body.Children.Add(titleRow);
            body.Children.Add(new TextBlock
            {
                Text = "Curate data, build reproducible Eval/SFT datasets, train locally, inspect adapters, compare real roles and explicitly promote evidence-backed results.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            body.Children.Add(_busy);
            body.Children.Add(_status);
            body.Children.Add(BuildDatasetSection());
            body.Children.Add(BuildEvalSection());
            body.Children.Add(BuildTrainingSection());
            body.Children.Add(BuildAdapterSection());
            body.Children.Add(BuildAutoLabSection());
            return scroll;
        }

        private Border BuildDatasetSection()
        {
            var content = new StackPanel { Spacing = 10 };
            var searchRow = Horizontal(_datasetSearch);
            var search = new Button { Content = "Search" };
            Id(search, "LifecycleLab_DatasetSearch_Button");
            search.Click += async (_, _) => await SearchDatasetsAsync();
            searchRow.Children.Add(search);
            content.Children.Add(searchRow);
            content.Children.Add(_datasetResults);
            content.Children.Add(Label("DOWNLOADS"));
            content.Children.Add(_datasetDownloads);
            content.Children.Add(Label("CURATE / MAP"));
            content.Children.Add(_sourcePath);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var inspect = new Button { Content = "Inspect" };
            Id(inspect, "LifecycleLab_Inspect_Button");
            inspect.Click += async (_, _) => await InspectDatasetAsync();
            var split = new Button { Content = "Deterministic 80/10/10 split" };
            Id(split, "LifecycleLab_Split_Button");
            split.Click += async (_, _) => await SplitDatasetAsync();
            actions.Children.Add(inspect);
            actions.Children.Add(split);
            content.Children.Add(actions);
            content.Children.Add(_inspectSummary);

            var mappings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            mappings.Children.Add(_promptColumn);
            mappings.Children.Add(_expectedColumn);
            mappings.Children.Add(_systemColumn);
            content.Children.Add(mappings);
            var import = new Button { Content = "Import mapped rows into selected Eval dataset" };
            Id(import, "LifecycleLab_ImportToEval_Button");
            import.Click += async (_, _) => await ImportIntoEvalAsync();
            content.Children.Add(import);
            content.Children.Add(new TextBlock
            {
                Text = "PaRuX never guesses semantic columns. Inspect first, then explicitly choose prompt and expected-behavior mappings.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
            return Section("1 · DATASET HUB & CURATION", content);
        }

        private Border BuildEvalSection()
        {
            var content = new StackPanel { Spacing = 10 };
            var select = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            select.Children.Add(_evalDataset);
            var refresh = new Button { Content = "Refresh datasets" };
            Id(refresh, "LifecycleLab_RefreshDatasets_Button");
            refresh.Click += async (_, _) => await RefreshEvalAsync(CancellationToken.None);
            select.Children.Add(refresh);
            content.Children.Add(select);

            var createRow = Horizontal(_newEvalName);
            var create = new Button { Content = "Create Eval dataset" };
            Id(create, "LifecycleLab_CreateEval_Button");
            create.Click += async (_, _) => await CreateEvalDatasetAsync();
            createRow.Children.Add(create);
            content.Children.Add(createRow);

            var runRow = Horizontal(_evalRole);
            var run = new Button { Content = "Run selected dataset" };
            Id(run, "LifecycleLab_RunEval_Button");
            run.Click += async (_, _) => await StartEvalRunAsync();
            var prepareSft = new Button { Content = "Prepare SFT JSONL from mapped Eval cases" };
            Id(prepareSft, "LifecycleLab_PrepareSft_Button");
            prepareSft.Click += async (_, _) => await PrepareSftFromEvalAsync();
            runRow.Children.Add(run);
            runRow.Children.Add(prepareSft);
            content.Children.Add(runRow);
            content.Children.Add(_evalSummary);
            return Section("2 · EVAL DATASET & REGRESSION LAB", content);
        }

        private Border BuildTrainingSection()
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(_trainingEngine);
            // Install and remove sit together: provisioning ~5 GB of managed Python should be as
            // reversible as it is optional, and a user deciding whether to install it deserves to
            // see the way back in the same place.
            var runtimeActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var install = new Button { Content = "Install/repair managed Training runtime" };
            Id(install, "LifecycleLab_InstallRuntime_Button");
            install.Click += async (_, _) => await InstallTrainingEngineAsync();
            runtimeActions.Children.Add(install);

            var remove = new Button { Content = "Remove runtime (frees ~5 GB)" };
            Id(remove, "LifecycleLab_RemoveRuntime_Button");
            remove.Click += async (_, _) => await RemoveTrainingEngineAsync();
            runtimeActions.Children.Add(remove);
            content.Children.Add(runtimeActions);
            content.Children.Add(_trainingPath);
            content.Children.Add(_baseModel);

            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            options.Children.Add(_trainingMethod);
            options.Children.Add(new TextBlock { Text = "Steps", VerticalAlignment = VerticalAlignment.Center });
            options.Children.Add(_steps);
            options.Children.Add(new TextBlock { Text = "LoRA rank", VerticalAlignment = VerticalAlignment.Center });
            options.Children.Add(_rank);
            content.Children.Add(options);

            content.Children.Add(BuildTrainingWizardControls());

            var start = new Button { Content = "Start local training" };
            Id(start, "LifecycleLab_StartTraining_Button");
            start.Click += async (_, _) => await StartTrainingAsync();
            content.Children.Add(start);
            content.Children.Add(new TextBlock
            {
                Text = "SFT requires messages[] JSONL; use the Eval→SFT handoff above after explicit dataset mapping. DPO requires a genuine prompt/chosen/rejected JSONL and is never synthesized from Eval cases.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
            content.Children.Add(Label("JOBS"));
            content.Children.Add(_trainingJobs);
            return Section("3 · TRAINING LAB", content);
        }

        private Border BuildAdapterSection()
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(_conversionStatus);
            content.Children.Add(new TextBlock
            {
                Text = "Completed PaRuX jobs auto-register adapters with source job/dataset provenance. GGUF conversion is explicit and only needed for runtime LoRA attachment.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            });
            content.Children.Add(_adapters);
            return Section("4 · ADAPTER MANAGER", content);
        }

        private Border BuildAutoLabSection()
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "AutoLab evaluates the selected dataset sequentially across real Smart Router roles, records exact model/adapter identity, compares completed runs and recommends without auto-promoting.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            content.Children.Add(_sweepRoles);
            var sweep = new Button { Content = "Run comparison sweep" };
            Id(sweep, "LifecycleLab_RunSweep_Button");
            sweep.Click += async (_, _) => await RunSweepAsync();
            content.Children.Add(sweep);
            content.Children.Add(_experiments);
            return Section("5 · AUTOLAB · COMPARE · PROMOTE", content);
        }

        private async Task RefreshAllAsync()
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
            CancellationToken ct = _refreshCts.Token;
            SetBusy(true, "Refreshing lifecycle state…");
            try
            {
                await RefreshEvalAsync(ct);
                await RefreshDatasetDownloadsAsync(ct);
                await RefreshTrainingAsync(ct);
                await RefreshAdaptersAsync(ct);
                await RefreshExperimentsAsync(ct);
                _status.Text = "Lifecycle state refreshed.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status.Text = "Refresh failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task SearchDatasetsAsync()
        {
            string query = (_datasetSearch.Text ?? string.Empty).Trim();
            if (query.Length == 0) return;
            SetBusy(true, "Searching datasets…");
            try
            {
                List<DatasetSearchResult> results = await DatasetHubClient.Current.SearchAsync(query, 15, CancellationToken.None);
                _datasetResults.Children.Clear();
                foreach (DatasetSearchResult repo in results)
                {
                    var row = ActionRow(repo.RepoId + " · ↓ " + repo.Downloads + " · ♥ " + repo.Likes);
                    var choose = new Button { Content = "Choose file" };
                    Id(choose, "LifecycleLab_ChooseFile_Button");
                    choose.Click += async (_, _) => await ChooseDatasetFileAsync(repo.RepoId);
                    Grid.SetColumn(choose, 1); row.Children.Add(choose);
                    _datasetResults.Children.Add(row);
                }
                if (results.Count == 0) _datasetResults.Children.Add(new TextBlock { Text = "No dataset repositories matched." });
                _status.Text = results.Count + " dataset result(s).";
            }
            catch (Exception ex) { _status.Text = "Dataset search failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task ChooseDatasetFileAsync(string repoId)
        {
            try
            {
                DatasetRepoDetails details = await DatasetHubClient.Current.GetDetailsAsync(repoId, CancellationToken.None);
                var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
                Id(combo, "LifecycleLab_DatasetFile_ComboBox", "Dataset file");
                foreach (DatasetFileOption file in details.Files
                    .Where(f => System.IO.Path.GetExtension(f.FileName).ToLowerInvariant() is ".csv" or ".json" or ".jsonl" or ".parquet")
                    .OrderBy(f => f.SizeBytes > 0 ? f.SizeBytes : long.MaxValue)
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = file.FileName + " · " + (file.SizeBytes > 0 ? FormatBytes(file.SizeBytes) : "size unknown"),
                        Tag = file
                    });
                if (combo.Items.Count == 0) throw new InvalidOperationException("This dataset has no supported CSV, JSON, JSONL or Parquet files.");
                combo.SelectedIndex = 0;
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock { Text = repoId, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "License: " + (details.License ?? "not declared by source"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
                panel.Children.Add(combo);
                var dialog = new ContentDialog
                {
                    Title = "Download dataset file",
                    Content = panel,
                    PrimaryButtonText = "Download",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not DatasetFileOption selected) return;
                await DatasetHubClient.Current.StartDownloadAsync(new DatasetDownloadRequest
                {
                    RepoId = repoId,
                    FileName = selected.FileName,
                    DownloadUrl = selected.DownloadUrl,
                    SizeBytes = selected.SizeBytes
                }, CancellationToken.None);
                _status.Text = "Queued dataset file " + selected.FileName + ".";
                await RefreshDatasetDownloadsAsync(CancellationToken.None);
            }
            catch (Exception ex) { _status.Text = "Dataset download setup failed: " + ex.Message; }
        }

        private async Task RefreshDatasetDownloadsAsync(CancellationToken ct)
        {
            List<DatasetDownloadStatus> jobs = await DatasetHubClient.Current.ListDownloadsAsync(ct);
            _datasetDownloads.Children.Clear();
            foreach (DatasetDownloadStatus job in jobs.OrderByDescending(j => j.CreatedUtc).Take(20))
            {
                string progress = job.TotalBytes > 0 ? " · " + FormatBytes(job.BytesDownloaded) + "/" + FormatBytes(job.TotalBytes) : string.Empty;
                var row = ActionRow(job.RepoId + " · " + job.FileName + " · " + job.Status + progress);
                if (job.Status is "queued" or "downloading")
                {
                    var cancel = new Button { Content = "Cancel" };
                    Id(cancel, "LifecycleLab_CancelDownload_Button");
                    cancel.Click += async (_, _) =>
                    {
                        try { await DatasetHubClient.Current.CancelDownloadAsync(job.Id, CancellationToken.None); await RefreshDatasetDownloadsAsync(CancellationToken.None); }
                        catch (Exception ex) { _status.Text = "Cancel failed: " + ex.Message; }
                    };
                    Grid.SetColumn(cancel, 1); row.Children.Add(cancel);
                }
                else if (job.Status == "completed" && !string.IsNullOrWhiteSpace(job.LocalPath))
                {
                    var curate = new Button { Content = "Curate" };
                    Id(curate, "LifecycleLab_Curate_Button");
                    curate.Click += (_, _) => _sourcePath.Text = job.LocalPath;
                    Grid.SetColumn(curate, 1); row.Children.Add(curate);
                }
                _datasetDownloads.Children.Add(row);
            }
            if (jobs.Count == 0) _datasetDownloads.Children.Add(new TextBlock { Text = "No dataset downloads yet." });
        }

        private async Task InspectDatasetAsync()
        {
            string path = (_sourcePath.Text ?? string.Empty).Trim();
            if (path.Length == 0) return;
            SetBusy(true, "Inspecting dataset…");
            try
            {
                DatasetCurationReport report = await DatasetHubClient.Current.InspectAsync(path, CancellationToken.None);
                _inspectSummary.Text = report.Error != null
                    ? report.DetectedFormat + " · " + report.Error
                    : report.DetectedFormat + " · " + report.RowCount + " rows · " + report.DuplicateRowCount + " duplicates · " + report.EmptyFieldCount + " empty fields";
                PopulateColumnMappings(report.Columns);
                _status.Text = report.Error == null ? "Dataset inspected; choose semantic mappings explicitly." : "Dataset inspection returned a parsing/format issue.";
            }
            catch (Exception ex) { _status.Text = "Inspection failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task SplitDatasetAsync()
        {
            string path = (_sourcePath.Text ?? string.Empty).Trim();
            if (path.Length == 0) return;
            SetBusy(true, "Creating deterministic split…");
            try
            {
                DatasetSplitResult split = await DatasetHubClient.Current.SplitAsync(new DatasetSplitRequest
                {
                    SourcePath = path,
                    TrainFraction = 0.8,
                    ValidationFraction = 0.1,
                    DeduplicateBeforeSplit = true,
                    Seed = 42
                }, CancellationToken.None);
                _sourcePath.Text = split.TrainPath;
                _status.Text = "Split created: " + split.TrainCount + " train / " + split.ValidationCount + " validation / " + split.TestCount + " test. The train split is now the curation source; inspect/map it before Training.";
                await InspectDatasetAsync();
            }
            catch (Exception ex) { _status.Text = "Split failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private void PopulateColumnMappings(IReadOnlyList<string> columns)
        {
            _promptColumn.Items.Clear();
            _expectedColumn.Items.Clear();
            _systemColumn.Items.Clear();
            _systemColumn.Items.Add(new ComboBoxItem { Content = "(none)", Tag = string.Empty });
            foreach (string column in columns)
            {
                _promptColumn.Items.Add(new ComboBoxItem { Content = column, Tag = column });
                _expectedColumn.Items.Add(new ComboBoxItem { Content = column, Tag = column });
                _systemColumn.Items.Add(new ComboBoxItem { Content = column, Tag = column });
            }
            _systemColumn.SelectedIndex = 0;
            _promptColumn.IsEnabled = _promptColumn.Items.Count > 0;
            _expectedColumn.IsEnabled = _expectedColumn.Items.Count > 0;
            _systemColumn.IsEnabled = true;
        }

        private async Task ImportIntoEvalAsync()
        {
            string source = (_sourcePath.Text ?? string.Empty).Trim();
            string? evalId = SelectedEvalDatasetId();
            string? prompt = SelectedTag(_promptColumn);
            string? expected = SelectedTag(_expectedColumn);
            string? system = SelectedTag(_systemColumn);
            if (source.Length == 0 || string.IsNullOrWhiteSpace(evalId) || string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(expected))
            {
                _status.Text = "Choose source file, Eval dataset, prompt column and expected-behavior column first.";
                return;
            }
            SetBusy(true, "Importing mapped Eval cases…");
            try
            {
                ImportDatasetAsEvalCasesResult result = await DatasetHubClient.Current.ImportIntoEvalAsync(new ImportDatasetAsEvalCasesRequest
                {
                    SourcePath = source,
                    TargetEvalDatasetId = evalId,
                    PromptColumn = prompt,
                    ExpectedBehaviorColumn = expected,
                    SystemPromptColumn = string.IsNullOrWhiteSpace(system) ? null : system
                }, CancellationToken.None);
                _status.Text = "Imported " + result.CasesCreated + " Eval case(s); skipped " + result.RowsSkipped + " incomplete row(s).";
                await RefreshEvalAsync(CancellationToken.None);
            }
            catch (Exception ex) { _status.Text = "Eval import failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task RefreshEvalAsync(CancellationToken ct)
        {
            EvalDatasetListResponse response = await EvalLabClient.Current.ListDatasetsAsync(ct);
            string? old = SelectedEvalDatasetId();
            _evalDataset.Items.Clear();
            foreach (EvalDatasetDto dataset in response.Datasets.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                _evalDataset.Items.Add(new ComboBoxItem { Content = dataset.Name, Tag = dataset });
            int index = FindEvalDatasetIndex(old);
            if (index < 0 && _evalDataset.Items.Count > 0) index = 0;
            _evalDataset.SelectedIndex = index;
            _evalDataset.IsEnabled = _evalDataset.Items.Count > 0;
            await RenderSelectedEvalSummaryAsync(ct);
        }

        private async Task CreateEvalDatasetAsync()
        {
            string name = (_newEvalName.Text ?? string.Empty).Trim();
            if (name.Length == 0) return;
            try
            {
                EvalDatasetDto created = await EvalLabClient.Current.CreateDatasetAsync(new CreateEvalDatasetRequest { Name = name }, CancellationToken.None);
                _newEvalName.Text = string.Empty;
                await RefreshEvalAsync(CancellationToken.None);
                SelectEvalDataset(created.Id);
                await RenderSelectedEvalSummaryAsync(CancellationToken.None);
                _status.Text = "Created Eval dataset " + created.Name + ".";
            }
            catch (Exception ex) { _status.Text = "Create Eval dataset failed: " + ex.Message; }
        }

        private async Task StartEvalRunAsync()
        {
            string? id = SelectedEvalDatasetId();
            if (string.IsNullOrWhiteSpace(id)) return;
            string selectedRole = _evalRole.SelectedItem as string ?? string.Empty;
            string role = selectedRole == EvalRoleAutomatic ? string.Empty : selectedRole.Trim();
            try
            {
                EvalRunDto run = await EvalLabClient.Current.StartRunAsync(id, role.Length == 0 ? null : role, CancellationToken.None);
                _lastEvalRunId = run.Id;
                _lastEvalRunDatasetId = id;
                _status.Text = "Eval run " + run.Id + " started" + (role.Length == 0 ? "." : " for role " + role + ".")
                    + " Waiting for the run to finish…";
                await AwaitEvalRunAsync(run.Id, CancellationToken.None);
            }
            catch (Exception ex) { _status.Text = "Eval start failed: " + ex.Message; }
        }

        /// <summary>
        /// The run is executed server-side and only its id comes back from StartRunAsync, so
        /// without this the page reported "started" forever and the real verdicts - which are
        /// stored and were reachable through GetRunAsync/GetResultsAsync all along - were simply
        /// never shown to anyone. Polls until the run leaves "running", then renders the outcome.
        /// Bounded so an unreachable or wedged run reports honestly instead of polling for ever.
        /// </summary>
        private async Task AwaitEvalRunAsync(string runId, CancellationToken ct)
        {
            const int maxAttempts = 600;
            for (int attempt = 0; attempt < maxAttempts && !ct.IsCancellationRequested; attempt++)
            {
                await Task.Delay(1000, ct);
                EvalRunDto run;
                try { run = await EvalLabClient.Current.GetRunAsync(runId, ct); }
                catch (Exception ex) { _status.Text = "Eval run status unavailable: " + ex.Message; return; }

                if (run.Status == EvalRunStatus.Running) continue;

                await RenderEvalRunAsync(run, ct);
                _status.Text = run.Status == EvalRunStatus.Completed
                    ? "Eval run finished: " + DescribeRun(run)
                    : "Eval run ended with status '" + run.Status + "': " + DescribeRun(run);
                return;
            }
            _status.Text = "Eval run " + runId + " is still running after "
                + maxAttempts + "s; use Refresh datasets to check it again later.";
        }

        private static string DescribeRun(EvalRunDto run)
        {
            int scored = run.PassCount + run.FailCount + run.InconclusiveCount;
            string summary = run.PassCount + " passed, " + run.FailCount + " failed, "
                + run.InconclusiveCount + " inconclusive";
            if (run.ErrorCount > 0) summary += ", " + run.ErrorCount + " errored";
            // A run where nothing could be judged is reported as such rather than as a 0% score:
            // an all-errored run (no reachable model) must never read like a genuine failure.
            if (scored == 0) return summary + " - nothing was scored.";
            return summary + " (" + ((double)run.PassCount / scored).ToString("P0") + " pass).";
        }

        private async Task RenderEvalRunAsync(EvalRunDto run, CancellationToken ct)
        {
            _evalSummary.Children.Add(new TextBlock
            {
                Text = "Run " + run.Id + " · " + run.Status + " · " + DescribeRun(run),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            EvalCaseResultListResponse results;
            try { results = await EvalLabClient.Current.GetResultsAsync(run.Id, ct); }
            catch (Exception ex)
            {
                _evalSummary.Children.Add(new TextBlock
                {
                    Text = "Per-case results unavailable: " + ex.Message,
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
                });
                return;
            }
            foreach (EvalCaseResultDto result in results.Results)
                _evalSummary.Children.Add(new TextBlock
                {
                    Text = result.Verdict + " · " + Shorten(result.ActualOutput)
                        + (string.IsNullOrWhiteSpace(result.VerdictReason) ? string.Empty : " · " + result.VerdictReason),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
                });
        }

        private static string Shorten(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(no output)";
            string single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return single.Length <= 160 ? single : single[..160] + "…";
        }

        private async Task PrepareSftFromEvalAsync()
        {
            if (_evalDataset.SelectedItem is not ComboBoxItem item || item.Tag is not EvalDatasetDto dataset)
            {
                _status.Text = "Select an Eval dataset first.";
                return;
            }
            SetBusy(true, "Preparing deterministic SFT JSONL…");
            try
            {
                EvalCaseListResponse cases = await EvalLabClient.Current.ListCasesAsync(dataset.Id, CancellationToken.None);
                string path = await EvalTrainingDatasetExporter.ExportSftAsync(dataset, cases.Cases, CancellationToken.None);
                _trainingPath.Text = path;
                SelectTrainingMethod(TrainingMethod.Sft);
                _status.Text = "SFT dataset prepared from " + cases.Cases.Count + " explicitly mapped Eval case(s). Training path is ready.";
            }
            catch (Exception ex) { _status.Text = "SFT preparation failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task RenderSelectedEvalSummaryAsync(CancellationToken ct)
        {
            _evalSummary.Children.Clear();
            string? id = SelectedEvalDatasetId();
            if (string.IsNullOrWhiteSpace(id))
            {
                _evalSummary.Children.Add(new TextBlock { Text = "Create or select an Eval dataset." });
                return;
            }
            EvalCaseListResponse cases = await EvalLabClient.Current.ListCasesAsync(id, ct);
            _evalSummary.Children.Add(new TextBlock { Text = cases.Cases.Count + " mapped case(s). These cases can be evaluated directly or exported as deterministic SFT chat JSONL." });

            // Keep the last run for this dataset visible across refreshes, so its verdicts survive
            // a Refresh instead of being replaced by the bare case count.
            if (_lastEvalRunId == null || _lastEvalRunDatasetId != id) return;
            try
            {
                EvalRunDto run = await EvalLabClient.Current.GetRunAsync(_lastEvalRunId, ct);
                await RenderEvalRunAsync(run, ct);
            }
            catch (Exception ex)
            {
                _evalSummary.Children.Add(new TextBlock
                {
                    Text = "Last run unavailable: " + ex.Message,
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
                });
            }
        }

        private async Task RefreshTrainingAsync(CancellationToken ct)
        {
            TrainingEngineStatus engine = await TrainingLabClient.Current.GetEngineStatusAsync(ct);
            _trainingEngine.Text = engine.Installed
                ? "Managed training runtime installed · CUDA " + (engine.CudaVerified ? "verified" : "not verified") + (string.IsNullOrWhiteSpace(engine.TorchVersion) ? string.Empty : " · torch " + engine.TorchVersion)
                : "Managed training runtime is not installed.";
            List<TrainingJobStatus> jobs = await TrainingLabClient.Current.ListJobsAsync(ct);
            _trainingJobs.Children.Clear();
            foreach (TrainingJobStatus job in jobs.OrderByDescending(j => j.CreatedUtc).Take(20))
            {
                string metrics = DescribeTrainingMetrics(job);
                var row = ActionRow(job.Method.ToUpperInvariant() + " · " + job.Status + " · " + job.Stage + " · " + job.CurrentStep + "/" + job.TotalSteps + metrics + (string.IsNullOrWhiteSpace(job.AdapterPath) ? string.Empty : " · adapter ready"));
                if (job.Status is "queued" or "running")
                {
                    var cancel = new Button { Content = "Cancel" };
                    Id(cancel, "LifecycleLab_CancelTraining_Button");
                    cancel.Click += async (_, _) =>
                    {
                        try { await TrainingLabClient.Current.CancelJobAsync(job.Id, CancellationToken.None); await RefreshTrainingAsync(CancellationToken.None); }
                        catch (Exception ex) { _status.Text = "Training cancel failed: " + ex.Message; }
                    };
                    Grid.SetColumn(cancel, 1); row.Children.Add(cancel);
                }
                _trainingJobs.Children.Add(row);
            }
            if (jobs.Count == 0) _trainingJobs.Children.Add(new TextBlock { Text = "No training jobs yet." });
        }

        private async Task InstallTrainingEngineAsync()
        {
            SetBusy(true, "Installing managed Training Lab runtime…");
            try
            {
                await TrainingLabClient.Current.InstallEngineAsync(CancellationToken.None);
                await RefreshTrainingAsync(CancellationToken.None);
                _status.Text = "Training runtime installation completed; status refreshed.";
            }
            catch (Exception ex) { _status.Text = "Training runtime install failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task RemoveTrainingEngineAsync()
        {
            var confirm = new ContentDialog
            {
                Title = "Remove the training runtime?",
                Content = "This deletes PaRuX's managed Python environment for local fine-tuning - "
                    + "roughly 5 GB, most of it PyTorch. Datasets, adapters and job history are kept, "
                    + "and the runtime can be installed again whenever you want to train.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            SetBusy(true, "Removing managed Training Lab runtime…");
            try
            {
                TrainingEngineRemovalResult result =
                    await TrainingLabClient.Current.UninstallEngineAsync(CancellationToken.None);
                await RefreshTrainingAsync(CancellationToken.None);
                _status.Text = result.Removed
                    ? "Training runtime removed; " + FormatBytes(result.FreedBytes) + " freed."
                    : result.Detail;
            }
            catch (Exception ex) { _status.Text = "Training runtime removal failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task StartTrainingAsync()
        {
            string path = (_trainingPath.Text ?? string.Empty).Trim();
            string model = (_baseModel.Text ?? string.Empty).Trim();
            string method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft;
            if (path.Length == 0 || model.Length == 0) return;
            int steps = SafeWholeNumber(_steps.Value, 60);
            int rank = SafeWholeNumber(_rank.Value, 16);
            try
            {
                TrainingJobStatus job = await TrainingLabClient.Current.CreateJobAsync(new TrainingJobRequest
                {
                    TrainJsonlPath = path,
                    BaseModel = model,
                    Method = method,
                    MaxSteps = steps,
                    LoraRank = rank
                }, CancellationToken.None);
                _status.Text = "Training job " + job.Id + " queued. Successful output auto-registers in Adapter Manager with provenance.";
                await RefreshTrainingAsync(CancellationToken.None);
            }
            catch (Exception ex) { _status.Text = "Training start failed: " + ex.Message; }
        }

        private async Task RefreshAdaptersAsync(CancellationToken ct)
        {
            LoraConversionEngineStatus conversion = await AdapterManagerClient.Current.GetConversionStatusAsync(ct);
            _conversionStatus.Text = "LoRA→GGUF: scripts " + (conversion.ScriptsAvailable ? "available" : "missing") + " · runtime " + (conversion.Installed ? "installed" : "not installed") + " · dependencies " + (conversion.DependenciesVerified ? "verified" : "not verified");
            List<AdapterDto> adapters = await AdapterManagerClient.Current.ListAsync(ct);
            _adapters.Children.Clear();
            foreach (AdapterDto adapter in adapters.OrderByDescending(a => a.CreatedUtc))
            {
                var card = new StackPanel { Spacing = 4 };
                card.Children.Add(new TextBlock { Text = adapter.Name + " · " + adapter.Method + " · " + (adapter.Enabled ? "enabled" : "disabled"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                card.Children.Add(new TextBlock
                {
                    Text = adapter.BaseModel + (string.IsNullOrWhiteSpace(adapter.SourceDatasetPath) ? string.Empty : " · dataset " + adapter.SourceDatasetPath) + (string.IsNullOrWhiteSpace(adapter.GgufPath) ? " · PEFT only" : " · GGUF ready"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.72
                });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var enabled = new Button { Content = adapter.Enabled ? "Disable" : "Enable" };
                Id(enabled, "LifecycleLab_AdapterEnabled_Button");
                enabled.Click += async (_, _) =>
                {
                    try { await AdapterManagerClient.Current.SetEnabledAsync(adapter.Id, !adapter.Enabled, CancellationToken.None); await RefreshAdaptersAsync(CancellationToken.None); }
                    catch (Exception ex) { _status.Text = "Adapter update failed: " + ex.Message; }
                };
                actions.Children.Add(enabled);
                if (string.IsNullOrWhiteSpace(adapter.GgufPath))
                {
                    var convert = new Button { Content = "Convert to GGUF" };
                    Id(convert, "LifecycleLab_ConvertGguf_Button");
                    convert.Click += async (_, _) =>
                    {
                        SetBusy(true, "Converting adapter to GGUF…");
                        try
                        {
                            await AdapterManagerClient.Current.ConvertAsync(adapter.Id, "f16", CancellationToken.None);
                            await RefreshAdaptersAsync(CancellationToken.None);
                            _status.Text = "Adapter converted to GGUF.";
                        }
                        catch (Exception ex) { _status.Text = "Adapter conversion failed: " + ex.Message; }
                        finally { SetBusy(false); }
                    };
                    actions.Children.Add(convert);
                }
                card.Children.Add(actions);
                _adapters.Children.Add(card);
            }
            if (adapters.Count == 0) _adapters.Children.Add(new TextBlock { Text = "No adapters registered yet." });
        }

        private async Task RunSweepAsync()
        {
            string? datasetId = SelectedEvalDatasetId();
            if (string.IsNullOrWhiteSpace(datasetId))
            {
                _status.Text = "Select an Eval dataset before running AutoLab.";
                return;
            }
            List<string> roles = (_sweepRoles.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (roles.Count == 0) return;
            SetBusy(true, "Running sequential AutoLab sweep…");
            try
            {
                ComparisonSweepResponse response = await AutoLabClient.Current.SweepAsync(new CreateComparisonSweepRequest { DatasetId = datasetId, Roles = roles }, CancellationToken.None);
                _recommendedExperimentId = response.RecommendedExperimentId;
                _status.Text = response.RecommendationReason ?? "Sweep completed without a promotable recommendation yet.";
                await RefreshExperimentsAsync(CancellationToken.None);
            }
            catch (Exception ex) { _status.Text = "AutoLab sweep failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task RefreshExperimentsAsync(CancellationToken ct)
        {
            List<ExperimentDto> experiments = await AutoLabClient.Current.ListAsync(ct);
            ExperimentComparisonResponse? comparison = experiments.Count == 0
                ? null
                : await AutoLabClient.Current.CompareAsync(experiments.Select(e => e.Id), ct);
            _recommendedExperimentId = comparison?.RecommendedExperimentId;
            _experiments.Children.Clear();
            IEnumerable<ExperimentComparisonEntry> entries = comparison?.Experiments.OrderByDescending(e => e.PassRate)
                ?? Enumerable.Empty<ExperimentComparisonEntry>();
            foreach (ExperimentComparisonEntry entry in entries)
            {
                int scored = entry.PassCount + entry.FailCount + entry.InconclusiveCount;
                string recommended = entry.ExperimentId == _recommendedExperimentId ? " · RECOMMENDED" : string.Empty;
                var row = ActionRow(entry.Name + " · " + entry.RunStatus + " · pass " + entry.PassRate.ToString("P0") + " (" + entry.PassCount + "/" + scored + ")" + (string.IsNullOrWhiteSpace(entry.AdapterPath) ? string.Empty : " · adapter") + recommended);
                if (entry.ExperimentId == _recommendedExperimentId)
                {
                    var promote = new Button { Content = "Promote…" };
                    Id(promote, "LifecycleLab_Promote_Button");
                    promote.Click += async (_, _) => await PromoteExperimentAsync(entry.ExperimentId);
                    Grid.SetColumn(promote, 1); row.Children.Add(promote);
                }
                _experiments.Children.Add(row);
            }
            if (experiments.Count == 0) _experiments.Children.Add(new TextBlock { Text = "No AutoLab experiments yet." });
        }

        private async Task PromoteExperimentAsync(string experimentId)
        {
            // The target role is a closed, server-validated set (AutoLabService.Promote rejects
            // anything LocalAiPaths.NormalizeTextRole doesn't know), so offer exactly those roles
            // instead of a free-text box that can only be typed wrong. Same source of truth the
            // Model Routing panel already lists from.
            var role = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
            Id(role, "LifecycleLab_PromoteRole_ComboBox", "Promotion role");
            foreach (string known in ModelRoleCatalog.TextRoles) role.Items.Add(known);
            role.SelectedItem = ModelRoleCatalog.TextRoles.Contains("standard") ? "standard" : ModelRoleCatalog.TextRoles[0];
            var dialog = new ContentDialog
            {
                Title = "Promote evidence-backed experiment",
                Content = role,
                PrimaryButtonText = "Promote",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string target = (role.SelectedItem as string ?? string.Empty).Trim();
            if (target.Length == 0) return;
            try
            {
                await AutoLabClient.Current.PromoteAsync(experimentId, target, CancellationToken.None);
                _status.Text = "Promoted recommended experiment to role " + target + ". AutoLab never promotes automatically.";
            }
            catch (Exception ex) { _status.Text = "Promotion failed: " + ex.Message; }
        }

        private string? SelectedEvalDatasetId() =>
            _evalDataset.SelectedItem is ComboBoxItem item && item.Tag is EvalDatasetDto dataset ? dataset.Id : null;

        private int FindEvalDatasetIndex(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return -1;
            for (int i = 0; i < _evalDataset.Items.Count; i++)
                if ((_evalDataset.Items[i] as ComboBoxItem)?.Tag is EvalDatasetDto d && d.Id == id) return i;
            return -1;
        }

        private void SelectEvalDataset(string id)
        {
            int index = FindEvalDatasetIndex(id);
            if (index >= 0) _evalDataset.SelectedIndex = index;
        }

        private void SelectTrainingMethod(string method)
        {
            for (int i = 0; i < _trainingMethod.Items.Count; i++)
                if ((_trainingMethod.Items[i] as ComboBoxItem)?.Tag is string value && value == method)
                {
                    _trainingMethod.SelectedIndex = i;
                    return;
                }
        }

        private static int SafeWholeNumber(double value, int fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Max(1, (int)Math.Round(value));

        private static string? SelectedTag(ComboBox combo) =>
            combo.SelectedItem is ComboBoxItem item ? item.Tag as string : null;

        private static StackPanel Horizontal(UIElement leading)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(leading);
            return row;
        }

        private static Grid ActionRow(string text)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(label, 0); row.Children.Add(label);
            return row;
        }

        private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7 };

        private static Border Section(string title, UIElement content)
        {
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            body.Children.Add(content);
            return new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = body };
        }

        private void SetBusy(bool busy, string? message = null)
        {
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(message)) _status.Text = message;
        }

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
