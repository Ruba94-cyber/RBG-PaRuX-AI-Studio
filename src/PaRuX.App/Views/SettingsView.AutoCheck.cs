using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>"Automatic checks" Settings section, bound to /api/v1/maintenance/settings, /status and /run-now.</summary>
    public sealed partial class SettingsView
    {
        // True until the first status read has populated the controls: XAML initialisation raises
        // Toggled/ValueChanged before every control exists, which used to throw (and would have saved defaults).
        private bool _autoCheckLoading = true;
        private bool _autoCheckBusy;

        private MediaLaneControl? _mediaLane;

        /// <summary>Content lane (Standard / Uncensored / Automatic) for media generation; the same control and setting as the Create tabs.</summary>
        private async Task RefreshMediaLaneAsync()
        {
            if (MediaLaneHost == null) return;
            if (_mediaLane == null)
            {
                _mediaLane = new MediaLaneControl("Settings_Media");
                MediaLaneHost.Children.Add(_mediaLane);
            }
            await _mediaLane.RefreshAsync();
        }

        public async Task RefreshAutoCheckAsync(CancellationToken ct = default)
        {
            if (Settings_AutoCheck_Enabled_Toggle == null || Settings_AutoCheck_Interval_Box == null) return;
            try
            {
                MaintenanceStatusDto status = await AutoMaintenanceClient.Current.GetStatusAsync(ct);
                ApplyAutoCheck(status);
                if (Settings_AutoCheck_Status_Text != null) Settings_AutoCheck_Status_Text.Text = string.Empty;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write("RefreshAutoCheckAsync failed: " + ex.Message);
                _autoCheckLoading = false;
                if (Settings_AutoCheck_Status_Text != null)
                    Settings_AutoCheck_Status_Text.Text = "Automatic checks could not be read: " + ex.Message;
            }
        }

        private void ApplyAutoCheck(MaintenanceStatusDto status)
        {
            _autoCheckLoading = true;
            try
            {
                Settings_AutoCheck_Enabled_Toggle.IsOn = status.Settings.Enabled;
                Settings_AutoCheck_Interval_Box.Value = status.Settings.IntervalHours;
                Settings_AutoCheck_Interval_Box.IsEnabled = status.Settings.Enabled;
                if (Settings_AutoCheck_RuntimeUpdate_Toggle != null)
                    Settings_AutoCheck_RuntimeUpdate_Toggle.IsOn = status.Settings.KeepRuntimesUpdated;
                if (Settings_AutoCheck_MediaOptimize_Toggle != null)
                    Settings_AutoCheck_MediaOptimize_Toggle.IsOn = status.Settings.AutoOptimizeMedia;
                if (Settings_AutoCheck_MediaOptimize_Text != null && !_mediaOptimizeBusy)
                    Settings_AutoCheck_MediaOptimize_Text.Text = MaintenancePresenter.SummarizeMediaOptimization(status.MediaOptimization);
                if (Settings_AutoCheck_RuntimeUpdate_Text != null)
                    Settings_AutoCheck_RuntimeUpdate_Text.Text = MaintenancePresenter.SummarizeRuntimeUpdate(status, DateTime.UtcNow);
                if (Settings_AutoCheck_LastRun_Text != null)
                    Settings_AutoCheck_LastRun_Text.Text = MaintenancePresenter.SummarizeLastRun(status, DateTime.UtcNow);
            }
            finally { _autoCheckLoading = false; }
        }

        private async void AutoCheckEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_autoCheckLoading) return;
            await SaveAutoCheckAsync();
        }

        private async void AutoCheckRuntimeUpdateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_autoCheckLoading) return;
            await SaveAutoCheckAsync();
        }

        private async void AutoCheckMediaOptimizeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_autoCheckLoading) return;
            await SaveAutoCheckAsync();
        }

        private bool _mediaOptimizeBusy;

        private async void AutoCheckMediaOptimize_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaOptimizeBusy) return;
            _mediaOptimizeBusy = true;
            Settings_AutoCheck_MediaOptimize_Button.IsEnabled = false;
            Settings_AutoCheck_MediaOptimizeCancel_Button.Visibility = Visibility.Visible;
            Settings_AutoCheck_MediaOptimize_Text.Text = "Optimizing the media engine: starting...";
            try
            {
                MediaOptimizationStatusDto status = await AutoMaintenanceClient.Current.StartMediaOptimizationAsync(CancellationToken.None);
                // The run is a background job on the server (minutes long): poll its status until it stops, so the page stays responsive.
                for (int i = 0; i < 2400; i++)
                {
                    await Task.Delay(1500);
                    status = await AutoMaintenanceClient.Current.GetMediaOptimizationAsync(CancellationToken.None);
                    Settings_AutoCheck_MediaOptimize_Text.Text = MaintenancePresenter.SummarizeMediaOptimization(status);
                    if (status.Phase is "done" or "idle" or "cancelled") break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Settings_AutoCheck_MediaOptimize_Text.Text = "Media engine optimisation failed: " + ex.Message;
            }
            finally
            {
                _mediaOptimizeBusy = false;
                Settings_AutoCheck_MediaOptimize_Button.IsEnabled = true;
                Settings_AutoCheck_MediaOptimizeCancel_Button.Visibility = Visibility.Collapsed;
            }
        }

        private async void AutoCheckMediaOptimizeCancel_Click(object sender, RoutedEventArgs e)
        {
            try { await AutoMaintenanceClient.Current.CancelMediaOptimizationAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write("Cancel media optimisation failed: " + ex.Message); }
        }

        private bool _runtimeUpdateBusy;

        private async void AutoCheckRuntimeUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            if (_runtimeUpdateBusy) return;
            _runtimeUpdateBusy = true;
            Settings_AutoCheck_RuntimeUpdate_Button.IsEnabled = false;
            Settings_AutoCheck_Status_Text.Text = "Updating PaRuX-managed runtimes…";
            try
            {
                RuntimeUpdateRunDto run = await AutoMaintenanceClient.Current.RunRuntimeUpdateNowAsync(CancellationToken.None);
                await RefreshAutoCheckAsync();
                Settings_AutoCheck_Status_Text.Text = run.Outcome == "busy"
                    ? "Another runtime update is already running."
                    : (run.Steps.Count > 0 ? run.Steps[^1] : "Runtime update finished.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Settings_AutoCheck_Status_Text.Text = "Runtime update failed: " + ex.Message;
            }
            finally
            {
                _runtimeUpdateBusy = false;
                Settings_AutoCheck_RuntimeUpdate_Button.IsEnabled = true;
            }
        }

        private async void AutoCheckIntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_autoCheckLoading) return;
            await SaveAutoCheckAsync();
        }

        private async Task SaveAutoCheckAsync()
        {
            if (Settings_AutoCheck_Enabled_Toggle == null || Settings_AutoCheck_Interval_Box == null
                || Settings_AutoCheck_RuntimeUpdate_Toggle == null || Settings_AutoCheck_MediaOptimize_Toggle == null)
                return;
            bool enabled = Settings_AutoCheck_Enabled_Toggle.IsOn;
            double hours = Settings_AutoCheck_Interval_Box.Value;
            bool keepRuntimes = Settings_AutoCheck_RuntimeUpdate_Toggle.IsOn;
            bool autoOptimize = Settings_AutoCheck_MediaOptimize_Toggle.IsOn;
            try
            {
                MaintenanceSettingsDto saved = await AutoMaintenanceClient.Current.SetSettingsAsync(enabled, hours, CancellationToken.None, keepRuntimes, autoOptimize);
                _autoCheckLoading = true;
                try
                {
                    Settings_AutoCheck_RuntimeUpdate_Toggle.IsOn = saved.KeepRuntimesUpdated;
                    Settings_AutoCheck_MediaOptimize_Toggle.IsOn = saved.AutoOptimizeMedia;
                    Settings_AutoCheck_Enabled_Toggle.IsOn = saved.Enabled;
                    Settings_AutoCheck_Interval_Box.Value = saved.IntervalHours;
                    Settings_AutoCheck_Interval_Box.IsEnabled = saved.Enabled;
                }
                finally { _autoCheckLoading = false; }
                Settings_AutoCheck_Status_Text.Text = "Saved.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Settings_AutoCheck_Status_Text.Text = "Could not save: " + ex.Message;
                await RefreshAutoCheckAsync();
            }
        }

        private async void AutoCheckNow_Click(object sender, RoutedEventArgs e)
        {
            if (_autoCheckBusy) return;
            _autoCheckBusy = true;
            Settings_AutoCheck_CheckNow_Button.IsEnabled = false;
            Settings_AutoCheck_Status_Text.Text = "Checking…";
            try
            {
                MaintenanceLastReportDto report = await AutoMaintenanceClient.Current.RunNowAsync(CancellationToken.None);
                await RefreshAutoCheckAsync();
                Settings_AutoCheck_Status_Text.Text = report.Outcome == "busy"
                    ? "Another check is already running."
                    : "Check finished.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Settings_AutoCheck_Status_Text.Text = "Check failed: " + ex.Message;
            }
            finally
            {
                _autoCheckBusy = false;
                Settings_AutoCheck_CheckNow_Button.IsEnabled = true;
            }
        }
    }
}
