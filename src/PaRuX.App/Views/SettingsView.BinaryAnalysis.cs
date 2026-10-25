using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Binary Analysis settings are kept in a small partial so the already-large Settings XAML
    /// does not gain another duplicated card implementation. The section is added once after the
    /// template exists and participates in the existing category rail through Tag="Agents".
    /// This partial is also the single SettingsView template hook used by other dynamically mounted
    /// v2 platform sections, avoiding competing OnApplyTemplate overrides across partial files.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _binaryAnalysisSettingsAdded;

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            AddBinaryAnalysisSettingsSection();
            AddCodingPermissionSettingsSection();
            AddExecutionProfileSettingsSection();
            AddPluginBundleSettingsSection();
            AddModelDownloadSettingsSection();
            ApplyCompetitiveBenchmarkUi();
            ReconcileLegacyStopAllCopy(this);
        }

        private void AddBinaryAnalysisSettingsSection()
        {
            if (_binaryAnalysisSettingsAdded || SettingsContentPanel == null) return;
            _binaryAnalysisSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "Agents",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "Agents", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };

            var heading = new TextBlock
            {
                Text = "BINARY ANALYSIS / REVERSE ENGINEERING",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            };
            section.Children.Add(heading);

            var card = new Border
            {
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1)
            };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke)
                && stroke is Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = "Dedicated local workspace used by Binary Analysis when a target file is not attached directly in Chat. Keep this explicit: PaRuX no longer borrows the legacy global Coding Agent workspace for new work.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            var pathBox = new TextBox
            {
                PlaceholderText = "C:\\work\\binary-analysis",
                Text = SettingsStore.Current.Data.BinaryAnalysisWorkspaceRoot ?? string.Empty
            };
            content.Children.Add(pathBox);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var browse = new Button { Content = "Browse…" };
            var clear = new Button { Content = "Clear" };
            buttons.Children.Add(browse);
            buttons.Children.Add(clear);
            content.Children.Add(buttons);

            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
            content.Children.Add(status);

            void SavePath(string? value)
            {
                string normalized = value?.Trim() ?? string.Empty;
                SettingsStore.Current.Data.BinaryAnalysisWorkspaceRoot = normalized;
                SettingsStore.Current.Save();
                pathBox.Text = normalized;
                status.Text = normalized.Length == 0
                    ? "No Binary Analysis workspace is configured. PaRuX will ask for one instead of silently borrowing another agent's repository."
                    : "Binary Analysis workspace saved.";
            }

            pathBox.LostFocus += (sender, args) => SavePath(pathBox.Text);
            clear.Click += (sender, args) => SavePath(string.Empty);
            browse.Click += async (sender, args) =>
            {
                try
                {
                    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(
                        picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                    Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
                    if (folder != null) SavePath(folder.Path);
                }
                catch (Exception ex)
                {
                    Log.Write("Binary Analysis workspace picker failed: " + ex.Message);
                    status.Text = "Couldn't choose the folder: " + ex.Message;
                }
            };

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
        }
    }
}
