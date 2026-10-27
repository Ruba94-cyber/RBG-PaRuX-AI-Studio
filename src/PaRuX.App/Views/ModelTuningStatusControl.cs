using System;
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
    /// Small non-modal "Tuning for your PC" status. <see cref="Mode.Hub"/> is an InfoBar (running job, or the latest result for a few
    /// minutes); <see cref="Mode.Line"/> is a compact line for the Chat footer / Create status that shows only while a job is queued
    /// or running. Both offer Cancel. It polls the local server every few seconds while it is on screen and hides itself when idle.
    /// </summary>
    public sealed class ModelTuningStatusControl : UserControl
    {
        public enum Mode { Hub, Line }

        private readonly Mode _mode;
        private readonly string? _kind;
        private readonly InfoBar _bar = new InfoBar { IsOpen = false, IsClosable = true, Visibility = Visibility.Collapsed };
        private readonly StackPanel _line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly ProgressRing _ring = new ProgressRing { Width = 14, Height = 14, IsActive = true, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _lineText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
        private readonly Button _lineCancel = new Button { Content = "Cancel", Padding = new Thickness(8, 2, 8, 2) };
        private readonly Button _barCancel = new Button { Content = "Cancel" };
        private DispatcherTimer? _timer;
        private bool _inFlight;
        private string? _cancelId;
        private string? _dismissedKey;

        public ModelTuningStatusControl(Mode mode, string? kind = null)
        {
            _mode = mode;
            _kind = kind;
            AutomationProperties.SetAutomationId(this, mode == Mode.Hub ? "ModelTuning_Hub_Status" : "ModelTuning_Line_Status");
            _bar.ActionButton = _barCancel;
            _bar.Closed += (_, _) => _dismissedKey = _bar.Title + "|" + _bar.Message;
            _barCancel.Click += async (_, _) => await CancelAsync();
            _lineCancel.Click += async (_, _) => await CancelAsync();
            _line.Children.Add(_ring);
            _line.Children.Add(_lineText);
            _line.Children.Add(_lineCancel);
            Content = mode == Mode.Hub ? (UIElement)_bar : _line;
            Loaded += (_, _) => Start();
            Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
        }

        private void Start()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_inFlight) return;
            _inFlight = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                ModelTuningStatusDto status = await AutoMaintenanceClient.Current.GetTuningAsync(cts.Token);
                Apply(ModelTuningPresenter.Present(status, DateTime.UtcNow, activeOnly: _mode == Mode.Line, kind: _kind));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Server not reachable or not ready: stay quiet, the status is advisory.
            }
            finally { _inFlight = false; }
        }

        private void Apply(ModelTuningViewModel vm)
        {
            _cancelId = vm.CancelId;
            if (_mode == Mode.Hub)
            {
                string key = vm.Title + "|" + vm.Detail;
                if (!vm.Visible || (_dismissedKey == key && !vm.CanCancel))
                {
                    _bar.IsOpen = false;
                    _bar.Visibility = Visibility.Collapsed;
                    return;
                }
                _bar.Title = vm.Title;
                _bar.Message = string.IsNullOrEmpty(vm.ProposalText) ? vm.Detail : vm.Detail + " " + vm.ProposalText;
                _bar.Severity = vm.Severity switch
                {
                    "success" => InfoBarSeverity.Success,
                    "warning" => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Informational
                };
                _barCancel.Visibility = vm.CanCancel ? Visibility.Visible : Visibility.Collapsed;
                _bar.Visibility = Visibility.Visible;
                _bar.IsOpen = true;
                return;
            }
            if (!vm.Visible) { _line.Visibility = Visibility.Collapsed; return; }
            _lineText.Text = vm.Title + ". " + vm.Detail;
            ToolTipService.SetToolTip(_lineText, vm.Title + ". " + vm.Detail);
            _ring.IsActive = vm.Busy;
            _ring.Visibility = vm.Busy ? Visibility.Visible : Visibility.Collapsed;
            _lineCancel.Visibility = vm.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            _line.Visibility = Visibility.Visible;
        }

        private async Task CancelAsync()
        {
            string? id = _cancelId;
            if (id == null) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await AutoMaintenanceClient.Current.CancelTuningAsync(id, cts.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* already finished */ }
            await RefreshAsync();
        }
    }
}
