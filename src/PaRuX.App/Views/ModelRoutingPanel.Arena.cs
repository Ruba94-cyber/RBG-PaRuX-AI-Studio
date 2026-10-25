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
    /// "Compare models" section of the Model Routing panel: run the same prompt(s) against 2-3 installed models one after another,
    /// see answers with locally measured timings, and optionally pick the better answer. Results are evidence only.
    /// </summary>
    public sealed class ModelArenaSection : UserControl
    {
        private readonly ComboBox[] _models = { new ComboBox(), new ComboBox(), new ComboBox() };
        private readonly Dictionary<string, CheckBox> _tasks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly StackPanel _taskPanel = new StackPanel { Spacing = 2 };
        private readonly TextBox _custom = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, PlaceholderText = "Optional: your own prompt" };
        private readonly NumberBox _maxTokens = new NumberBox { Minimum = 16, Maximum = 1024, Value = 256, Header = "Maximum answer length (tokens)", Width = 260 };
        private readonly Button _start = new Button { Content = "Compare" };
        private readonly Button _cancel = new Button { Content = "Stop", IsEnabled = false };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
        private readonly StackPanel _results = new StackPanel { Spacing = 10 };
        private CancellationTokenSource? _poll;
        private string? _runId;
        private bool _busy;

        public ModelArenaSection()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Compare models", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = ModelArenaPresenter.EvidenceNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72
            });

            var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            for (int i = 0; i < _models.Length; i++)
            {
                _models[i].MinWidth = 240;
                _models[i].PlaceholderText = i < 2 ? "Choose model " + (i + 1) : "Third model (optional)";
                AutomationProperties.SetAutomationId(_models[i], ModelArenaPresenter.ModelAutomationId(i));
                AutomationProperties.SetName(_models[i], "Compare model " + (i + 1));
                pickers.Children.Add(_models[i]);
            }
            panel.Children.Add(pickers);
            panel.Children.Add(_taskPanel);

            var advanced = new Expander { Header = "Advanced options", IsExpanded = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(advanced, "Arena_Advanced_Expander");
            AutomationProperties.SetAutomationId(_custom, "Arena_CustomPrompt");
            AutomationProperties.SetAutomationId(_maxTokens, "Arena_MaxTokens");
            var advBody = new StackPanel { Spacing = 8 };
            advBody.Children.Add(_custom);
            advBody.Children.Add(_maxTokens);
            advanced.Content = advBody;
            panel.Children.Add(advanced);

            AutomationProperties.SetAutomationId(_start, "Arena_Start_Button");
            AutomationProperties.SetAutomationId(_cancel, "Arena_Cancel_Button");
            AutomationProperties.SetAutomationId(_status, "Arena_Status");
            AutomationProperties.SetAutomationId(_results, "Arena_Results");
            _start.Click += async (_, _) => await StartAsync();
            _cancel.Click += async (_, _) => await CancelAsync();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(_start);
            buttons.Children.Add(_cancel);
            panel.Children.Add(buttons);
            panel.Children.Add(_status);
            panel.Children.Add(_results);
            Content = panel;
            Loaded += async (_, _) => await LoadAsync();
            Unloaded += (_, _) => _poll?.Cancel();
        }

        private async Task LoadAsync()
        {
            try
            {
                List<ManagedModelInfo> installed = await ModelArenaClient.Current.GetInstalledModelsAsync(CancellationToken.None);
                List<ArenaTaskDto> tasks = await ModelArenaClient.Current.GetTasksAsync(CancellationToken.None);
                foreach (ComboBox box in _models)
                {
                    box.Items.Clear();
                    foreach (ManagedModelInfo m in installed.Where(m => !string.IsNullOrEmpty(m.FileName)))
                        box.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(m.Alias) ? m.FileName : m.Alias + " (" + m.FileName + ")", Tag = m.FileName });
                }
                _taskPanel.Children.Clear();
                _tasks.Clear();
                foreach (ArenaTaskDto t in tasks)
                {
                    var box = new CheckBox { Content = t.Title + " (" + t.Category + (t.HasAutomaticCheck ? ", auto-checked" : "") + ")", IsChecked = t.Category == "chat" && _tasks.Count == 0 };
                    AutomationProperties.SetAutomationId(box, ModelArenaPresenter.TaskAutomationId(t.Id));
                    _tasks[t.Id] = box;
                    _taskPanel.Children.Add(box);
                }
                _status.Text = installed.Count < 2 ? "Install at least two models to compare them." : string.Empty;
            }
            catch (Exception ex)
            {
                _status.Text = "Model comparison is unavailable: " + ex.Message;
            }
        }

        private List<string> SelectedModels() =>
            _models.Select(m => (m.SelectedItem as ComboBoxItem)?.Tag as string).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

        private async Task StartAsync()
        {
            List<string> models = SelectedModels();
            List<string> taskIds = _tasks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            var (ok, message) = ModelArenaPresenter.ValidateStart(models, taskIds.Count, _custom.Text, _busy);
            if (!ok) { _status.Text = message; return; }
            try
            {
                _busy = true; _start.IsEnabled = false; _cancel.IsEnabled = true;
                _results.Children.Clear();
                ArenaRunDto run = await ModelArenaClient.Current.StartAsync(new ArenaStartRequest
                {
                    ModelFileNames = models, TaskIds = taskIds,
                    CustomPrompt = string.IsNullOrWhiteSpace(_custom.Text) ? null : _custom.Text,
                    MaxTokens = (int)_maxTokens.Value
                }, CancellationToken.None);
                _runId = run.Id;
                _poll?.Cancel();
                _poll = new CancellationTokenSource();
                await PollAsync(run.Id, _poll.Token);
            }
            catch (Exception ex)
            {
                _status.Text = "Could not start the comparison: " + ex.Message;
                _busy = false; _start.IsEnabled = true; _cancel.IsEnabled = false;
            }
        }

        private async Task PollAsync(string id, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    ArenaRunDto run = await ModelArenaClient.Current.GetRunAsync(id, ct);
                    Render(run);
                    if (run.Status != "running") break;
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status.Text = "Lost contact with the comparison: " + ex.Message; }
            finally { _busy = false; _start.IsEnabled = true; _cancel.IsEnabled = false; }
        }

        private async Task CancelAsync()
        {
            if (_runId is null) return;
            try { await ModelArenaClient.Current.CancelAsync(_runId, CancellationToken.None); }
            catch (Exception ex) { _status.Text = "Could not stop the comparison: " + ex.Message; }
        }

        private void Render(ArenaRunDto run)
        {
            _status.Text = ModelArenaPresenter.RunStatusText(run);
            _results.Children.Clear();
            for (int p = 0; p < run.Prompts.Count; p++)
            {
                ArenaPromptResultDto prompt = run.Prompts[p];
                var card = new StackPanel { Spacing = 4 };
                card.Children.Add(new TextBlock { Text = prompt.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                card.Children.Add(new TextBlock { Text = prompt.Prompt, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
                for (int m = 0; m < prompt.Results.Count; m++)
                {
                    ArenaModelResultDto r = prompt.Results[m];
                    card.Children.Add(new TextBlock { Text = r.ModelFileName + " - " + ModelArenaPresenter.FormatTiming(r), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    if (r.Status == "completed")
                    {
                        card.Children.Add(new TextBox { Text = r.Output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 160 });
                        string check = ModelArenaPresenter.CheckText(r.Check);
                        if (check.Length > 0) card.Children.Add(new TextBlock { Text = check, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
                        if (run.Status != "running")
                        {
                            int promptIndex = p; string model = r.ModelFileName;
                            var pick = new Button { Content = prompt.UserPick == model ? "Your pick" : "This one is better" };
                            AutomationProperties.SetAutomationId(pick, ModelArenaPresenter.JudgeAutomationId(p, m));
                            pick.Click += async (_, _) => await JudgeAsync(run.Id, promptIndex, model);
                            card.Children.Add(pick);
                        }
                    }
                }
                card.Children.Add(new TextBlock { Text = ModelArenaPresenter.PickText(prompt), FontSize = 12, Opacity = 0.8 });
                _results.Children.Add(new Border
                {
                    Padding = new Thickness(10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = card
                });
            }
        }

        private async Task JudgeAsync(string runId, int promptIndex, string model)
        {
            try
            {
                ArenaRunDto run = await ModelArenaClient.Current.JudgeAsync(runId,
                    new ArenaJudgeRequest { PromptIndex = promptIndex, ModelFileName = model }, CancellationToken.None);
                Render(run);
            }
            catch (Exception ex) { _status.Text = "Could not save your pick: " + ex.Message; }
        }
    }
}
