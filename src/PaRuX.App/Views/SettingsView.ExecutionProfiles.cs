using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Desktop control plane for Core-owned cross-surface Execution Profiles. This UI
    /// chooses a durable quality envelope and shows/removes benchmark-derived model overrides; it
    /// deliberately does not reimplement budget validation or invent manual tuning values.</summary>
    public sealed partial class SettingsView
    {
        private bool _executionProfileSettingsAdded;
        private bool _executionProfileUiLoading;
        private ComboBox? _executionQualityCombo;
        private TextBlock? _executionProfileSummary;
        private TextBlock? _executionProfileStatus;
        private StackPanel? _executionOverrideList;

        private void AddExecutionProfileSettingsSection()
        {
            if (_executionProfileSettingsAdded || SettingsContentPanel == null) return;
            _executionProfileSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "PaRuX Platform",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "PaRuX Platform", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };
            section.Children.Add(new TextBlock
            {
                Text = "EXECUTION PROFILES",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var card = new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1)
            };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke) && stroke is Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "One Core-owned quality/resource envelope for Chat, Agents, Training, Media and the scheduler. Adaptive routing remains authoritative inside that envelope, so Maximum Quality does not make trivial chat waste hidden reasoning.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _executionQualityCombo = new ComboBox { MinWidth = 190 };
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = "Fast", Tag = ExecutionQualityProfile.Fast });
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = "Balanced", Tag = ExecutionQualityProfile.Balanced });
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = "Maximum quality", Tag = ExecutionQualityProfile.MaximumQuality });
            _executionQualityCombo.Items.Add(new ComboBoxItem { Content = "Overnight", Tag = ExecutionQualityProfile.Overnight });
            _executionQualityCombo.SelectionChanged += async (_, _) =>
            {
                if (_executionProfileUiLoading || _executionQualityCombo.SelectedItem is not ComboBoxItem item) return;
                string quality = item.Tag?.ToString() ?? ExecutionQualityProfile.Balanced;
                await SetExecutionQualityAsync(quality);
            };
            row.Children.Add(_executionQualityCombo);

            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (_, _) => await RefreshExecutionProfilesAsync();
            row.Children.Add(refresh);
            content.Children.Add(row);

            _executionProfileSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
            content.Children.Add(_executionProfileSummary);
            _executionProfileStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
            content.Children.Add(_executionProfileStatus);

            content.Children.Add(new TextBlock
            {
                Text = "MODEL-SPECIFIC BENCHMARK OVERRIDES",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            _executionOverrideList = new StackPanel { Spacing = 6 };
            content.Children.Add(_executionOverrideList);

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
            _ = RefreshExecutionProfilesAsync();
        }

        private async Task SetExecutionQualityAsync(string quality)
        {
            try
            {
                if (_executionProfileStatus != null) _executionProfileStatus.Text = "Applying…";
                await ExecutionProfileClient.Current.SetQualityAsync(quality, null, CancellationToken.None);
                await RefreshExecutionProfilesAsync();
                if (_executionProfileStatus != null) _executionProfileStatus.Text = "Execution profile applied across PaRuX Core.";
            }
            catch (Exception ex)
            {
                Log.Write("SetExecutionQualityAsync failed: " + ex.Message);
                if (_executionProfileStatus != null) _executionProfileStatus.Text = "Couldn't apply Execution Profile: " + ex.Message;
            }
        }

        private async Task RefreshExecutionProfilesAsync()
        {
            if (_executionQualityCombo == null || _executionProfileSummary == null || _executionOverrideList == null) return;
            try
            {
                ExecutionProfileStatus status = await ExecutionProfileClient.Current.GetAsync(null, CancellationToken.None);
                _executionProfileUiLoading = true;
                try
                {
                    ComboBoxItem? selected = _executionQualityCombo.Items.OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), status.ActiveQualityProfile, StringComparison.Ordinal));
                    _executionQualityCombo.SelectedItem = selected ?? _executionQualityCombo.Items.OfType<ComboBoxItem>().ElementAtOrDefault(1);
                }
                finally { _executionProfileUiLoading = false; }

                ExecutionResourceBudget b = status.Effective.Budget;
                _executionProfileSummary.Text =
                    $"Active: {status.ActiveQualityProfile} · wall clock {b.MaxWallClockMinutes} min · tools {b.MaxToolSteps:N0} · " +
                    $"parallel tasks {b.MaxParallelTasks} · subagents {b.MaxDirectSubagents} · verification {b.VerificationPasses} pass(es) · " +
                    $"context {(b.ContextFraction * 100):0}% · artifact cap {FormatExecutionProfileBytes(b.MaxArtifactBytes)} · RAM ≤ {b.MaxSystemRamPercent}% · VRAM ≤ {b.MaxVramPercent}%.";

                _executionOverrideList.Children.Clear();
                if (status.ModelOverrides.Count == 0)
                {
                    _executionOverrideList.Children.Add(new TextBlock
                    {
                        Text = "No model-specific override. PaRuX is using the deterministic profile defaults.", Opacity = 0.70
                    });
                }
                else
                {
                    foreach (ModelExecutionProfile model in status.ModelOverrides.OrderBy(x => x.ModelKey, StringComparer.OrdinalIgnoreCase))
                        _executionOverrideList.Children.Add(BuildExecutionOverrideRow(model));
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshExecutionProfilesAsync failed: " + ex.Message);
                _executionProfileSummary.Text = "Execution Profiles unavailable: " + ex.Message;
            }
        }

        private UIElement BuildExecutionOverrideRow(ModelExecutionProfile model)
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = model.ModelKey, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock
            {
                Text = $"{model.EvidenceSource} · context {model.PreferredContextTokens:N0} · reasoning {model.ReasoningEffort} · " +
                    $"delegation threshold {model.DelegationThreshold} · {model.ToolStrategy} · {model.StructuredOutputStrategy}",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.70
            });
            grid.Children.Add(text);

            var remove = new Button { Content = "Use defaults", Padding = new Thickness(8, 2, 8, 2) };
            string key = model.ModelKey;
            remove.Click += async (_, _) =>
            {
                try
                {
                    await ExecutionProfileClient.Current.DeleteModelOverrideAsync(key, CancellationToken.None);
                    await RefreshExecutionProfilesAsync();
                }
                catch (Exception ex)
                {
                    Log.Write("Delete execution model override failed: " + ex.Message);
                    if (_executionProfileStatus != null) _executionProfileStatus.Text = "Couldn't remove override: " + ex.Message;
                }
            };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
            return grid;
        }

        private static string FormatExecutionProfileBytes(long bytes)
        {
            double value = Math.Max(0, bytes);
            string[] suffix = { "B", "KiB", "MiB", "GiB", "TiB" };
            int index = 0;
            while (value >= 1024 && index < suffix.Length - 1) { value /= 1024; index++; }
            return value.ToString(index == 0 ? "0" : "0.#") + " " + suffix[index];
        }
    }
}
