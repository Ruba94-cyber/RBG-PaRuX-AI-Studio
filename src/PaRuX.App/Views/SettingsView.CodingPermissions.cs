using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Coding Agent network/restore authority. This controls only NEW coding tasks; the authority is
    /// copied into each durable task when it is created and cannot be widened by changing Settings
    /// while a task is already queued/running. General run_command remains no-network in every mode.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _codingPermissionSettingsAdded;

        private void AddCodingPermissionSettingsSection()
        {
            if (_codingPermissionSettingsAdded || SettingsContentPanel == null) return;
            _codingPermissionSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "Agents",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "Agents", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };

            section.Children.Add(new TextBlock
            {
                Text = "CODING AGENT PERMISSIONS",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var card = new Border
            {
                Padding = new Thickness(16),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1)
            };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke)
                && stroke is Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "Choose the authority copied into newly created Coding Agent tasks. Existing tasks keep the exact permissions they were created with. Normal shell commands stay inside the AppContainer with network disabled in every profile.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var picker = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Coding permission profile"
            };
            picker.Items.Add(new ComboBoxItem
            {
                Content = "Offline — no dependency downloads",
                Tag = CodingPermissionProfile.Offline
            });
            picker.Items.Add(new ComboBoxItem
            {
                Content = "Package Fetch — frozen/no-script dependency fetch only",
                Tag = CodingPermissionProfile.PackageFetch
            });
            picker.Items.Add(new ComboBoxItem
            {
                Content = "Trusted Restore — allow restore paths that may execute project/plugin code",
                Tag = CodingPermissionProfile.TrustedRestore
            });

            string current = CodingPermissionProfile.Normalize(SettingsStore.Current.Data.CodingPermissionProfile);
            picker.SelectedIndex = current switch
            {
                CodingPermissionProfile.PackageFetch => 1,
                CodingPermissionProfile.TrustedRestore => 2,
                _ => 0
            };
            content.Children.Add(picker);

            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
            content.Children.Add(detail);

            void RefreshDetail(string profile)
            {
                detail.Text = profile switch
                {
                    CodingPermissionProfile.PackageFetch =>
                        "Package Fetch grants only PaRuX-discovered dependency operations that avoid lifecycle scripts/build execution where the ecosystem permits it (for example npm/pnpm with scripts disabled, Cargo fetch, Go module download). The global Internet switch must also be ON.",
                    CodingPermissionProfile.TrustedRestore =>
                        "Trusted Restore additionally permits PaRuX-discovered restore/configuration flows that may execute repository-controlled build backends, MSBuild targets or plugins. It still does not give network access to arbitrary run_command actions, and the global Internet switch remains authoritative.",
                    _ =>
                        "Offline is the safest default. Coding can use repository-local and already-cached dependencies, but PaRuX will not download packages for the task."
                };
            }
            RefreshDetail(current);

            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedItem is not ComboBoxItem item || item.Tag is not string value) return;
                string normalized = CodingPermissionProfile.Normalize(value);
                SettingsStore.Current.Data.CodingPermissionProfile = normalized;
                SettingsStore.Current.Save();
                RefreshDetail(normalized);
            };

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
        }
    }
}
