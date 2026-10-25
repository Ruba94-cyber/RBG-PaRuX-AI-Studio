using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>Desktop inspection/install surface for declarative .parux-plugin bundles. Install
    /// remains disabled until Core inspection succeeds; unsigned install and publisher trust are
    /// separate explicit decisions, matching PluginBundleService's fail-closed boundary.</summary>
    public sealed partial class SettingsView
    {
        private bool _pluginBundleSettingsAdded;
        private TextBox? _pluginBundlePath;
        private TextBlock? _pluginInspectionSummary;
        private TextBlock? _pluginStatus;
        private ToggleSwitch? _pluginAllowUnsigned;
        private ToggleSwitch? _pluginTrustPublisher;
        private Button? _pluginInstallButton;
        private StackPanel? _trustedPublisherList;
        private PluginBundleInspection? _lastPluginInspection;

        private void AddPluginBundleSettingsSection()
        {
            if (_pluginBundleSettingsAdded || SettingsContentPanel == null) return;
            _pluginBundleSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "PaRuX Platform",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "PaRuX Platform", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };
            section.Children.Add(new TextBlock
            {
                Text = "PLUGIN BUNDLES",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 0)
            });

            var card = new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1)
            };
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out object? stroke) && stroke is Brush brush)
                card.BorderBrush = brush;
            var content = new StackPanel { Spacing = 9 };
            content.Children.Add(new TextBlock
            {
                Text = "Inspect signed declarative .parux-plugin bundles before installation. Bundles cannot execute installer code; imported Skills and Automations start disabled and MCP definitions remain staged/inert until explicitly activated.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75
            });

            _pluginBundlePath = new TextBox { PlaceholderText = "C:\\Downloads\\example.parux-plugin" };
            _pluginBundlePath.TextChanged += (_, _) => ResetPluginInspection();
            content.Children.Add(_pluginBundlePath);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var browse = new Button { Content = "Browse…" };
            browse.Click += async (_, _) => await PickPluginBundleAsync();
            actions.Children.Add(browse);
            var inspect = new Button { Content = "Inspect" };
            inspect.Click += async (_, _) => await InspectPluginBundleAsync();
            actions.Children.Add(inspect);
            _pluginInstallButton = new Button { Content = "Install", IsEnabled = false };
            _pluginInstallButton.Click += async (_, _) => await InstallPluginBundleAsync();
            actions.Children.Add(_pluginInstallButton);
            content.Children.Add(actions);

            _pluginAllowUnsigned = new ToggleSwitch
            {
                Header = "Allow unsigned / unverifiable bundle",
                OnContent = "Explicitly allowed", OffContent = "Blocked",
                IsOn = false
            };
            content.Children.Add(_pluginAllowUnsigned);
            _pluginTrustPublisher = new ToggleSwitch
            {
                Header = "Trust this verified publisher fingerprint during install",
                OnContent = "Trust", OffContent = "Do not trust",
                IsOn = false
            };
            content.Children.Add(_pluginTrustPublisher);

            _pluginInspectionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
            content.Children.Add(_pluginInspectionSummary);
            _pluginStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
            content.Children.Add(_pluginStatus);

            content.Children.Add(new TextBlock
            {
                Text = "TRUSTED PUBLISHERS",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            _trustedPublisherList = new StackPanel { Spacing = 6 };
            content.Children.Add(_trustedPublisherList);
            var refreshTrust = new Button { Content = "Refresh publishers", HorizontalAlignment = HorizontalAlignment.Left };
            refreshTrust.Click += async (_, _) => await RefreshTrustedPublishersAsync();
            content.Children.Add(refreshTrust);

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
            _ = RefreshTrustedPublishersAsync();
        }

        private async Task PickPluginBundleAsync()
        {
            if (_pluginBundlePath == null) return;
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                picker.FileTypeFilter.Add(".parux-plugin");
                picker.FileTypeFilter.Add(".zip");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file != null) _pluginBundlePath.Text = file.Path;
            }
            catch (Exception ex)
            {
                Log.Write("Plugin bundle picker failed: " + ex.Message);
                if (_pluginStatus != null) _pluginStatus.Text = "Couldn't choose plugin bundle: " + ex.Message;
            }
        }

        private void ResetPluginInspection()
        {
            _lastPluginInspection = null;
            if (_pluginInstallButton != null) _pluginInstallButton.IsEnabled = false;
            if (_pluginInspectionSummary != null) _pluginInspectionSummary.Text = string.Empty;
            if (_pluginAllowUnsigned != null) _pluginAllowUnsigned.IsOn = false;
            if (_pluginTrustPublisher != null) _pluginTrustPublisher.IsOn = false;
        }

        private async Task InspectPluginBundleAsync()
        {
            string path = (_pluginBundlePath?.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                if (_pluginStatus != null) _pluginStatus.Text = "Choose a .parux-plugin bundle first.";
                return;
            }
            try
            {
                if (_pluginStatus != null) _pluginStatus.Text = "Inspecting in PaRuX Core…";
                PluginBundleInspection inspection = await PluginBundleClient.Current.InspectAsync(path, CancellationToken.None);
                _lastPluginInspection = inspection;
                if (_pluginInstallButton != null) _pluginInstallButton.IsEnabled = inspection.Valid;
                RenderPluginInspection(inspection);
                if (_pluginStatus != null) _pluginStatus.Text = inspection.Valid
                    ? "Inspection complete. Review signature, permissions and warnings before Install."
                    : "Installation blocked: " + (inspection.Error ?? "bundle validation failed");
            }
            catch (Exception ex)
            {
                Log.Write("Plugin inspection failed: " + ex.Message);
                ResetPluginInspection();
                if (_pluginStatus != null) _pluginStatus.Text = "Inspection failed: " + ex.Message;
            }
        }

        private void RenderPluginInspection(PluginBundleInspection inspection)
        {
            if (_pluginInspectionSummary == null) return;
            string signature = inspection.SignatureValid
                ? (inspection.PublisherTrusted ? "verified + trusted" : "verified, publisher not trusted")
                : inspection.SignaturePresent ? "INVALID signature" : "unsigned";
            PluginPermissionDeclaration p = inspection.EffectivePermissions;
            string[] permissions = new[]
            {
                p.ReadFiles ? "read-files" : null, p.WriteFiles ? "write-files" : null,
                p.DeleteFiles ? "delete-files" : null, p.Shell ? "shell" : null,
                p.Network ? "network" : null, p.Browser ? "browser" : null,
                p.ComputerUse ? "computer-use" : null, p.ExternalProcesses ? "external-processes" : null,
                p.Automations ? "automations" : null, p.RemoteMcp ? "remote-mcp" : null
            }.Where(x => x != null).Cast<string>().ToArray();
            string permissionText = permissions.Length == 0 ? "none" : string.Join(", ", permissions);
            string warnings = inspection.Warnings.Count == 0 ? "none" : string.Join(" · ", inspection.Warnings);
            string fingerprint = string.IsNullOrWhiteSpace(inspection.PublisherKeyFingerprint)
                ? "n/a" : inspection.PublisherKeyFingerprint;
            _pluginInspectionSummary.Text =
                $"{inspection.Manifest.DisplayName} {inspection.Manifest.Version} · publisher {inspection.Manifest.Publisher} · {signature}\n" +
                $"SHA-256: {inspection.BundleSha256}\nFingerprint: {fingerprint}\n" +
                $"Components: {inspection.SkillCount} skill(s), {inspection.AutomationCount} automation(s), {inspection.McpCount} staged MCP definition(s)\n" +
                $"Effective permissions: {permissionText}\nWarnings: {warnings}";
        }

        private async Task InstallPluginBundleAsync()
        {
            if (_lastPluginInspection == null || !_lastPluginInspection.Valid || _pluginBundlePath == null) return;
            string path = _pluginBundlePath.Text.Trim();
            try
            {
                if (_pluginStatus != null) _pluginStatus.Text = "Installing declarative components…";
                PluginInstallResult result = await PluginBundleClient.Current.InstallAsync(
                    path,
                    _pluginAllowUnsigned?.IsOn == true,
                    _pluginTrustPublisher?.IsOn == true,
                    CancellationToken.None);
                if (_pluginStatus != null) _pluginStatus.Text = result.Message;
                await RefreshTrustedPublishersAsync();
                await InspectPluginBundleAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Plugin install failed: " + ex.Message);
                if (_pluginStatus != null) _pluginStatus.Text = "Install blocked/failed: " + ex.Message;
            }
        }

        private async Task RefreshTrustedPublishersAsync()
        {
            if (_trustedPublisherList == null) return;
            try
            {
                var publishers = await PluginBundleClient.Current.ListTrustedPublishersAsync(CancellationToken.None);
                _trustedPublisherList.Children.Clear();
                if (publishers.Count == 0)
                {
                    _trustedPublisherList.Children.Add(new TextBlock { Text = "No publisher fingerprint is trusted yet.", Opacity = 0.70 });
                    return;
                }
                foreach (PluginPublisherTrustDto publisher in publishers.OrderBy(x => x.Publisher, StringComparer.OrdinalIgnoreCase))
                {
                    var grid = new Grid { ColumnSpacing = 8 };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var text = new TextBlock
                    {
                        Text = publisher.Publisher + " · " + publisher.Fingerprint + " · trusted " + publisher.TrustedUtc.ToLocalTime().ToString("g"),
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.75
                    };
                    grid.Children.Add(text);
                    string fingerprint = publisher.Fingerprint;
                    var revoke = new Button { Content = "Revoke", Padding = new Thickness(8, 2, 8, 2) };
                    revoke.Click += async (_, _) =>
                    {
                        try
                        {
                            await PluginBundleClient.Current.RevokePublisherAsync(fingerprint, CancellationToken.None);
                            await RefreshTrustedPublishersAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Revoke plugin publisher failed: " + ex.Message);
                            if (_pluginStatus != null) _pluginStatus.Text = "Couldn't revoke publisher: " + ex.Message;
                        }
                    };
                    Grid.SetColumn(revoke, 1);
                    grid.Children.Add(revoke);
                    _trustedPublisherList.Children.Add(grid);
                }
            }
            catch (Exception ex)
            {
                Log.Write("RefreshTrustedPublishersAsync failed: " + ex.Message);
                _trustedPublisherList.Children.Clear();
                _trustedPublisherList.Children.Add(new TextBlock { Text = "Trusted publishers unavailable: " + ex.Message, Opacity = 0.70 });
            }
        }
    }
}
