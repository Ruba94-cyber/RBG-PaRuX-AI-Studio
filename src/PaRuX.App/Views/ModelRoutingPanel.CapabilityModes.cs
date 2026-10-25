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
    /// Compact "Capability overrides" section of the Model Routing panel: per capability an Automatic / Prefer / Force
    /// selector, a model picker (enabled for Prefer and Force) and a trace line saying what the router will do and why.
    /// </summary>
    public sealed class CapabilityModesSection : UserControl
    {
        private sealed class Row
        {
            public string Capability = string.Empty;
            public ComboBox Mode = new ComboBox { MinWidth = 130 };
            public ComboBox Model = new ComboBox { MinWidth = 260, PlaceholderText = "Choose model…" };
            public TextBlock Trace = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 };
            public CapabilityRoutingStatusDto? Status;
            public bool Suppress;
        }

        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        private readonly TextBlock _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 };

        public CapabilityModesSection()
        {
#if DEBUG
            if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal))
                CapabilityModeClient.Current = new InMemoryCapabilityModeClient();
#endif
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Capability overrides",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Automatic lets PaRuX choose and explain. Prefer uses your model when it can run, otherwise falls back with a visible trace. Force uses only your model and refuses honestly if it cannot run.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72
            });
            foreach ((string capability, string label) in CapabilityModePresenter.Capabilities)
                panel.Children.Add(BuildRow(capability, label));
            panel.Children.Add(_summary);
            Content = panel;
            Loaded += async (_, _) => await RefreshAsync();
        }

        private UIElement BuildRow(string capability, string label)
        {
            var row = new Row { Capability = capability };
            _rows[capability] = row;
            AutomationProperties.SetAutomationId(row.Mode, CapabilityModePresenter.ModeAutomationId(capability));
            AutomationProperties.SetName(row.Mode, label + " mode");
            AutomationProperties.SetAutomationId(row.Model, CapabilityModePresenter.ModelAutomationId(capability));
            AutomationProperties.SetName(row.Model, label + " model");
            AutomationProperties.SetAutomationId(row.Trace, CapabilityModePresenter.TraceAutomationId(capability));

            foreach ((string mode, string text) in CapabilityModePresenter.ModeOptions)
                row.Mode.Items.Add(new ComboBoxItem { Content = text, Tag = mode });
            row.Mode.SelectedIndex = 0;
            row.Model.IsEnabled = false;
            row.Mode.SelectionChanged += async (_, _) => await OnChangedAsync(row, modeChanged: true);
            row.Model.SelectionChanged += async (_, _) => await OnChangedAsync(row, modeChanged: false);

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(new TextBlock { Text = label, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(row.Mode);
            line.Children.Add(row.Model);
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(line);
            block.Children.Add(row.Trace);
            return block;
        }

        private static string ModeOf(Row r) => (r.Mode.SelectedItem as ComboBoxItem)?.Tag as string ?? ScopedRouteMode.Automatic;
        private static string? ModelOf(Row r) => (r.Model.SelectedItem as ComboBoxItem)?.Tag as string;

        private async Task OnChangedAsync(Row row, bool modeChanged)
        {
            if (row.Suppress) return;
            string mode = ModeOf(row);
            string? model = ModelOf(row);
            CapabilityModeRowPresentation pres = CapabilityModePresenter.Present(row.Capability, mode, model, null);
            row.Model.IsEnabled = pres.ModelEnabled;
            row.Trace.Text = pres.TraceText;
            if (!pres.CanSave) return;
            try
            {
                await CapabilityModeClient.Current.SetModeAsync(row.Capability,
                    CapabilityModePresenter.BuildRequest(mode, model), CancellationToken.None);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                row.Trace.Text = "Could not save this override: " + ex.Message;
            }
        }

        public async Task RefreshAsync()
        {
            IReadOnlyList<CapabilityRoutingStatusDto> statuses;
            try { statuses = await CapabilityModeClient.Current.GetStatusAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                _summary.Text = "Capability overrides are unavailable: " + ex.Message;
                return;
            }
            _summary.Text = string.Empty;
            foreach (CapabilityRoutingStatusDto status in statuses)
            {
                if (!_rows.TryGetValue(status.Capability, out Row? row)) continue;
                row.Status = status;
                row.Suppress = true;
                try
                {
                    string mode = CapabilityModePresenter.NormalizeMode(status.Mode);
                    for (int i = 0; i < row.Mode.Items.Count; i++)
                        if (row.Mode.Items[i] is ComboBoxItem item && (item.Tag as string) == mode) row.Mode.SelectedIndex = i;

                    row.Model.Items.Clear();
                    var names = new List<string>(status.Candidates);
                    if (!string.IsNullOrWhiteSpace(status.ModelId)
                        && !names.Contains(status.ModelId, StringComparer.OrdinalIgnoreCase)) names.Insert(0, status.ModelId);
                    foreach (string name in names)
                        row.Model.Items.Add(new ComboBoxItem { Content = name, Tag = name });
                    if (!string.IsNullOrWhiteSpace(status.ModelId))
                        for (int i = 0; i < row.Model.Items.Count; i++)
                            if (row.Model.Items[i] is ComboBoxItem item
                                && string.Equals(item.Tag as string, status.ModelId, StringComparison.OrdinalIgnoreCase))
                                row.Model.SelectedIndex = i;

                    CapabilityModeRowPresentation pres = CapabilityModePresenter.Present(
                        status.Capability, mode, status.ModelId, status);
                    row.Model.IsEnabled = pres.ModelEnabled;
                    row.Trace.Text = pres.TraceText;
                }
                finally { row.Suppress = false; }
            }
        }
    }
}
