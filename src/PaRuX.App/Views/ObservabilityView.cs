using System;
using System.Collections.Generic;
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
    /// Read-only Studio dashboard over Core-owned operational evidence. It combines a current
    /// cross-surface snapshot and native model benchmark evidence with the existing unified timeline;
    /// no telemetry uploader or duplicate analytics database is introduced by this view.
    /// </summary>
    public sealed class ObservabilityView : UserControl
    {
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _summary = new TextBlock { FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel _metrics = new StackPanel { Spacing = 8 };
        private readonly StackPanel _benchmarks = new StackPanel { Spacing = 6 };
        private readonly StackPanel _entries = new StackPanel { Spacing = 8 };
        private readonly ComboBox _category = new ComboBox { MinWidth = 220 };
        private readonly NumberBox _limit = new NumberBox { Minimum = 25, Maximum = 200, Value = 100, SmallChange = 25, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private List<TimelineEntry> _snapshot = new();
        private ObservabilityDashboardResponse? _dashboard;

        public ObservabilityView()
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_category, "Event category");
            _category.Items.Add(new ComboBoxItem { Content = "All categories", Tag = string.Empty });
            _category.SelectedIndex = 0;
            _category.SelectionChanged += (_, _) => RenderTimeline();
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAsync(); };
        }

        public async void OnShown() => await RefreshAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 16,
                Margin = new Thickness(24),
                MaxWidth = 1180,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            body.Children.Add(new TextBlock
            {
                Text = "Observability & Benchmarks",
                FontSize = 28,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Cross-surface operational evidence from the stores and benchmark runners PaRuX already owns. Values stay local and are refreshed from Core on demand.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(_category);
            controls.Children.Add(new TextBlock { Text = "Recent events", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.72 });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_limit, "Recent events limit");
            controls.Children.Add(_limit);
            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (_, _) => await RefreshAsync();
            controls.Children.Add(refresh);
            body.Children.Add(controls);
            body.Children.Add(_busy);
            body.Children.Add(_status);

            body.Children.Add(SectionHeading("CURRENT CORE SNAPSHOT"));
            body.Children.Add(_metrics);
            body.Children.Add(SectionHeading("MODEL BENCHMARK LEADERBOARD"));
            body.Children.Add(new TextBlock
            {
                Text = "Latest completed evidence per model + backend + hardware key. Throughput from different hardware keys is shown but never merged into one average.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.70, FontSize = 12
            });
            body.Children.Add(_benchmarks);
            body.Children.Add(SectionHeading("ACTIVITY TIMELINE"));
            body.Children.Add(_summary);
            body.Children.Add(_entries);
            return scroll;
        }

        private static TextBlock SectionHeading(string text) => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.82
        };

        private async Task RefreshAsync()
        {
            _busy.Visibility = Visibility.Visible;
            _status.Text = "Refreshing local operational and benchmark evidence…";
            try
            {
                int limit = Math.Clamp((int)(_limit.Value > 0 ? _limit.Value : 100), 25, 200);
                _dashboard = await ObservabilityClient.Current.GetDashboardAsync(limit, 12, CancellationToken.None);
                _snapshot = _dashboard.RecentActivity.OrderByDescending(e => e.Timestamp).ToList();
                RebuildCategories();
                RenderDashboard();
                RenderTimeline();
                _status.Text = "Dashboard refreshed from PaRuX Core · " + _dashboard.CapturedUtc.ToLocalTime().ToString("g") + ".";
            }
            catch (Exception ex)
            {
                _status.Text = "Observability refresh failed: " + ex.Message;
            }
            finally { _busy.Visibility = Visibility.Collapsed; }
        }

        private void RenderDashboard()
        {
            _metrics.Children.Clear();
            _benchmarks.Children.Clear();
            if (_dashboard == null) return;

            int metricIndex = 0;
            foreach (ObservabilityMetric metric in _dashboard.Metrics)
            {
                // Label and value are visible at a glance; explanatory diagnostics stay optional.
                var header = new Grid { ColumnSpacing = 12 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock
                {
                    Text = metric.Label,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var value = new TextBlock
                {
                    Text = metric.Value, FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(value, 1);
                header.Children.Add(value);
                var card = new Expander
                {
                    Header = header,
                    Content = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(metric.Detail) ? "No further details." : metric.Detail,
                        TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.76
                    },
                    IsExpanded = false
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card,
                    "Observability_Metric_" + metricIndex++ + "_Expander");
                _metrics.Children.Add(card);
            }

            string taskStates = string.Join(" · ", _dashboard.TaskStatuses.Select(item => item.Name + " " + item.Count));
            string trainingStates = string.Join(" · ", _dashboard.TrainingStatuses.Select(item => item.Name + " " + item.Count));
            var stateDetails = new Expander
            {
                Header = "Task and training states",
                Content = new TextBlock
                {
                    Text = "Task states: " + (taskStates.Length == 0 ? "none" : taskStates)
                        + "\nTraining states: " + (trainingStates.Length == 0 ? "none" : trainingStates),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12
                },
                IsExpanded = false
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(stateDetails, "Observability_TaskStates_Expander");
            _metrics.Children.Add(stateDetails);

            if (_dashboard.ModelBenchmarks.Count == 0)
            {
                _benchmarks.Children.Add(new TextBlock { Text = "No completed native model benchmark evidence yet.", Opacity = 0.70 });
                return;
            }

            int rank = 1;
            foreach (ModelBenchmarkLeaderboardEntry benchmark in _dashboard.ModelBenchmarks)
            {
                var header = new Grid { ColumnSpacing = 12 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.Children.Add(new TextBlock
                {
                    Text = "#" + rank + " · " + benchmark.Label,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var speed = new TextBlock
                {
                    Text = benchmark.TokensPerSecond.ToString("0.0") + " tok/s",
                    FontSize = 17,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(speed, 1);
                header.Children.Add(speed);
                var detail = new TextBlock
                {
                    Text = System.IO.Path.GetFileName(benchmark.ModelPath) + " · "
                        + (benchmark.Backend.Length == 0 ? "unknown backend" : benchmark.Backend) + " · "
                        + (benchmark.HardwareKey.Length == 0 ? "unknown hardware" : benchmark.HardwareKey)
                        + " · " + benchmark.Repetitions + " repetition(s) · " + benchmark.MeasuredUtc.ToLocalTime().ToString("g"),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.74
                };
                var card = new Expander { Header = header, Content = detail, IsExpanded = false };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card,
                    "Observability_Benchmark_" + rank++ + "_Expander");
                _benchmarks.Children.Add(card);
            }
        }

        private void RebuildCategories()
        {
            string selected = (_category.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            _category.Items.Clear();
            _category.Items.Add(new ComboBoxItem { Content = "All categories", Tag = string.Empty });
            foreach (string category in _snapshot.Select(e => e.Category).Where(c => !string.IsNullOrWhiteSpace(c))
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                _category.Items.Add(new ComboBoxItem { Content = Friendly(category), Tag = category });

            int match = 0;
            for (int i = 1; i < _category.Items.Count; i++)
            {
                if ((_category.Items[i] as ComboBoxItem)?.Tag is string value
                    && string.Equals(value, selected, StringComparison.OrdinalIgnoreCase))
                {
                    match = i;
                    break;
                }
            }
            _category.SelectedIndex = match;
        }

        private void RenderTimeline()
        {
            string selected = (_category.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            List<TimelineEntry> visible = _snapshot
                .Where(e => selected.Length == 0 || string.Equals(e.Category, selected, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Timestamp).ToList();

            _entries.Children.Clear();
            foreach (TimelineEntry entry in visible)
            {
                var card = new Border
                {
                    Padding = new Thickness(12),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(9)
                };
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                card.Child = row;

                var meta = new StackPanel { Spacing = 2 };
                meta.Children.Add(new TextBlock { Text = Friendly(entry.Category), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
                meta.Children.Add(new TextBlock { Text = entry.Timestamp.ToLocalTime().ToString("g"), FontSize = 11, Opacity = 0.66 });
                Grid.SetColumn(meta, 0); row.Children.Add(meta);

                var text = new StackPanel { Spacing = 3 };
                text.Children.Add(new TextBlock { Text = entry.Title, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (!string.IsNullOrWhiteSpace(entry.Detail))
                    text.Children.Add(new TextBlock { Text = entry.Detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.76 });
                if (!string.IsNullOrWhiteSpace(entry.RelatedId))
                    text.Children.Add(new TextBlock { Text = "id: " + entry.RelatedId, FontSize = 10, Opacity = 0.55, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(text, 1); row.Children.Add(text);
                _entries.Children.Add(card);
            }

            var counts = visible.GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => Friendly(g.Key) + ": " + g.Count());
            _summary.Text = visible.Count + " visible event(s) · " + string.Join(" · ", counts);
            if (visible.Count == 0) _entries.Children.Add(new TextBlock { Text = "No timeline records match this filter.", Opacity = 0.7 });
        }

        private static string Friendly(string value) => value switch
        {
            "task" => "Agent task",
            "model_download" => "Model download",
            "dataset_download" => "Dataset download",
            "training_job" => "Training",
            "adapter" => "Adapter",
            "experiment" => "AutoLab",
            "router_decision" => "Router",
            "runtime_regression" => "Runtime regression",
            "artifact" => "Artifact",
            "automation" => "Automation",
            "workflow" => "Workflow",
            _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Replace('_', ' ')
        };
    }
}
