using System;
using System.Collections.Generic;
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
    /// The compact content-lane selector shared by Create and Settings. The lane is one global setting (GET/POST /api/v1/media/lane),
    /// so every instance shows the same value after Refresh. Wording comes from <see cref="MediaModelPickerPresenter"/>.
    /// </summary>
    public sealed class MediaLaneControl : StackPanel
    {
        private readonly ComboBox _lane = new ComboBox { MinWidth = 150 };
        private readonly TextBlock _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };
        private bool _loading;

        public MediaLaneControl(string automationPrefix)
        {
            Spacing = 4;
            foreach (string lane in MediaModelPickerPresenter.Lanes) _lane.Items.Add(lane);
            _lane.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_lane, automationPrefix + "_Lane_ComboBox");
            AutomationProperties.SetName(_lane, "Content lane");
            _lane.SelectionChanged += async (_, _) => await OnLaneChangedAsync();
            var label = new TextBlock { Text = "Content lane", FontSize = 11, Opacity = 0.68 };
            Children.Add(label);
            Children.Add(_lane);
            Children.Add(_hint);
            _hint.Text = MediaModelPickerPresenter.LaneHint("Standard");
        }

        public async Task RefreshAsync()
        {
            try
            {
                MediaLaneStatus status = await MediaCheckpointsClient.Current.GetLaneAsync(CancellationToken.None);
                Apply(MediaModelPickerPresenter.NormalizeLane(status.Lane));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _hint.Text = "The content lane could not be read: " + ex.Message;
            }
        }

        private void Apply(string lane)
        {
            _loading = true;
            try
            {
                _lane.SelectedItem = lane;
                _hint.Text = MediaModelPickerPresenter.LaneHint(lane);
            }
            finally { _loading = false; }
        }

        private async Task OnLaneChangedAsync()
        {
            if (_loading || _lane.SelectedItem is not string lane) return;
            try
            {
                MediaLaneStatus saved = await MediaCheckpointsClient.Current.SetLaneAsync(lane, CancellationToken.None);
                Apply(MediaModelPickerPresenter.NormalizeLane(saved.Lane));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _hint.Text = "The content lane was not changed: " + ex.Message;
                await RefreshAsync();
            }
        }
    }

    /// <summary>
    /// The per-tab "Model" row: Automatic (recommended) plus every installed model this workflow can run, and the content lane.
    /// Choosing a model pins it through the existing checkpoint override; choosing Automatic clears the pin so the router picks per prompt.
    /// </summary>
    public sealed class MediaModelRow : StackPanel
    {
        private readonly string _idPrefix;
        private readonly ComboBox _model = new ComboBox { MinWidth = 280 };
        private readonly TextBlock _hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2, FontSize = 12, Opacity = 0.7
        };
        private readonly MediaLaneControl _lane;
        private readonly Button _detailsButton = new Button { Content = "Details" };
        private IReadOnlyList<MediaModelPickerItem> _items = Array.Empty<MediaModelPickerItem>();
        private string _type;
        private bool _loading;

        /// <param name="type">Checkpoint type: "image", "video" or "edit".</param>
        public MediaModelRow(string type, string idPrefix)
        {
            _type = type;
            _idPrefix = idPrefix;
            Spacing = 6;
            Orientation = Orientation.Vertical;
            AutomationProperties.SetAutomationId(_model, idPrefix + "_Model_ComboBox");
            AutomationProperties.SetName(_model, "Model");
            _model.SelectionChanged += async (_, _) => await OnModelChangedAsync();
            _lane = new MediaLaneControl(idPrefix);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _model.MinWidth = 0;
            _model.Width = 340;
            controls.Children.Add(_model);
            AutomationProperties.SetAutomationId(_detailsButton, idPrefix + "_ModelDetails_Button");
            AutomationProperties.SetName(_detailsButton, "View selected model details");
            _detailsButton.Click += async (_, _) => await ShowDetailsAsync();
            controls.Children.Add(_detailsButton);
            Children.Add(new TextBlock { Text = "Model", FontSize = 11, Opacity = 0.68 });
            Children.Add(controls);
            Children.Add(_hint);
            var laneEditor = new Expander
            {
                Header = "Content preferences",
                Content = _lane,
                IsExpanded = false
            };
            AutomationProperties.SetAutomationId(laneEditor, idPrefix + "_ContentLane_Expander");
            Children.Add(laneEditor);
            _model.Items.Add(MediaModelPickerPresenter.AutomaticLabel);
            _model.SelectedIndex = 0;
            Loaded += async (_, _) => await RefreshAsync();
        }

        /// <summary>The Edit tab runs instruction edits on the edit checkpoint and variations on the image checkpoint.</summary>
        public async Task SetTypeAsync(string type)
        {
            if (string.Equals(_type, type, StringComparison.Ordinal)) return;
            _type = type;
            await RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            await _lane.RefreshAsync();
            try
            {
                List<MediaCheckpointStatus> all = await MediaCheckpointsClient.Current.GetCheckpointsAsync(CancellationToken.None);
                MediaCheckpointStatus? status = all.Find(s => string.Equals(s.Type, _type, StringComparison.Ordinal));
                if (status == null) { _hint.Text = "This model type is not reported by PaRuX Core."; return; }
                MediaModelPickerState state = MediaModelPickerPresenter.Build(status.CurrentFile, status.IsCheckpointPinned, status.CompatibleFiles,
                    status.RoutingUsesOverride, status.RoutingMode, status.IsCheckpointPinned, status.PinnedFile);
                _loading = true;
                try
                {
                    _items = state.Items;
                    _model.Items.Clear();
                    foreach (MediaModelPickerItem item in state.Items)
                    {
                        var option = new ComboBoxItem { Content = CompactModelLabel(item.Label) };
                        ToolTipService.SetToolTip(option, item.Label);
                        AutomationProperties.SetName(option, item.Label);
                        _model.Items.Add(option);
                    }
                    _model.SelectedIndex = state.SelectedIndex;
                }
                finally { _loading = false; }
                _hint.Text = state.Hint;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _hint.Text = "The installed models could not be listed: " + ex.Message;
            }
        }

        private static string CompactModelLabel(string label)
        {
            if (string.IsNullOrEmpty(label) || label.Length <= 60) return label;
            // Retain both the family prefix and distinguishing quantization or pin suffix.
            return label.Substring(0, 35).TrimEnd() + "…" + label.Substring(label.Length - 20);
        }

        private async Task ShowDetailsAsync()
        {
            MediaModelPickerItem? chosen = _model.SelectedIndex >= 0 && _model.SelectedIndex < _items.Count
                ? _items[_model.SelectedIndex] : null;
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(new TextBlock
            {
                Text = chosen?.FileName is { Length: > 0 } ? "Selected model file" : "Automatic model selection",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var identity = new TextBox
            {
                Text = chosen?.FileName ?? "PaRuX chooses the best compatible model for each request.",
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true
            };
            AutomationProperties.SetAutomationId(identity, _idPrefix + "_FullModelIdentity_TextBox");
            details.Children.Add(identity);
            details.Children.Add(new TextBlock { Text = _hint.Text, TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                Title = "Model selection details",
                Content = details,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task OnModelChangedAsync()
        {
            if (_loading || _model.SelectedIndex < 0 || _model.SelectedIndex >= _items.Count) return;
            MediaModelPickerAction action = MediaModelPickerPresenter.ActionFor(_items[_model.SelectedIndex]);
            string? error = null;
            try
            {
                if (action.ClearOverride) await MediaCheckpointsClient.Current.ResetCheckpointAsync(_type, CancellationToken.None);
                else await MediaCheckpointsClient.Current.SetCheckpointAsync(_type, action.FileName!, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = "The model was not changed: " + ex.Message;
            }
            await RefreshAsync();
            if (error != null) _hint.Text = error;
        }
    }
}
