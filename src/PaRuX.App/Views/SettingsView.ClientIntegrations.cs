using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;
using PaRuX.Services.Integrations;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings › Integrations: the Universal Gateway and the external coding agents that can use it
    /// (Claude Code, Codex, OpenCode) plus generic OpenAI/Anthropic-compatible apps. Client rows are
    /// built from live detection so the page only offers what is really installed.
    /// </summary>
    public sealed partial class SettingsView
    {
        private GatewayStatusInfo? _gatewayStatus;
        private bool _integrationBusy;
        private int _integrationRefreshGeneration;

        private static string IntegrationFolderFile => Path.Combine(ClientIntegrationRuntime.Current.Store.Root, "launch-folder.txt");

        private string IntegrationWorkingFolder
        {
            get
            {
                try
                {
                    string saved = File.Exists(IntegrationFolderFile) ? File.ReadAllText(IntegrationFolderFile).Trim() : string.Empty;
                    if (saved.Length > 0 && Directory.Exists(saved)) return saved;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                string? project = ProjectWorkspaceSession.Current.RepositoryRoot;
                return project != null && Directory.Exists(project) ? project : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            set
            {
                try { IntegrationStore.WriteAtomically(IntegrationFolderFile, value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Saving integration folder failed: " + ex.Message); }
            }
        }

        private async Task RefreshIntegrationsAsync()
        {
            if (ClientIntegrationsList == null || _integrationBusy) return;
            int generation = ++_integrationRefreshGeneration;
            IntegrationWorkingFolderBox.Text = IntegrationWorkingFolder;
            try
            {
                _gatewayStatus = await RemoteAccessClient.Current.GetGatewayStatusAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _gatewayStatus = null;
                Log.Write("Gateway status failed: " + ex.Message);
            }
            if (generation != _integrationRefreshGeneration) return;
            RenderGatewayStatus();

            ClientIntegrationService service = ClientIntegrationRuntime.Current;
            ClientDetection[] detections = await Task.WhenAll(service.Clients.Select(c => service.DetectAsync(c, CancellationToken.None)));
            if (generation != _integrationRefreshGeneration) return;
            ClientIntegrationsList.Children.Clear();
            foreach (ClientDetection detection in detections)
                ClientIntegrationsList.Children.Add(BuildClientIntegrationCard(service.Get(detection.ClientId), detection, service.Manifest(detection.ClientId)));
        }

        private void RenderGatewayStatus()
        {
            GatewayStatusInfo? status = _gatewayStatus;
            if (status == null)
            {
                GatewayStateText.Text = "PaRuX Core is not reachable";
                GatewayDetailText.Text = "Start PaRuX Core (it starts with the app) and press Refresh.";
                GenericOpenAiUrlText.Text = GenericAnthropicUrlText.Text = string.Empty;
                return;
            }
            GatewayStateText.Text = status.Listening
                ? "Gateway running at " + status.BaseUrl
                : status.Enabled ? "Gateway port " + status.ConfiguredPort + " is in use by another program" : "Gateway is off";
            string routing = status.LastRouting == null
                ? "No external requests yet."
                : "Last request: " + status.LastRouting.RequestedModel + " → " + status.LastRouting.Role
                  + (status.LastRouting.ModelFile != null ? " (" + status.LastRouting.ModelFile + ")" : string.Empty)
                  + " at " + status.LastRouting.Utc.ToLocalTime().ToString("HH:mm:ss") + ".";
            GatewayDetailText.Text = "Models: " + string.Join(", ", status.Models.Select(m => m.Id)) + ". " + routing
                + (status.RestartRequired ? " Restart PaRuX to apply the saved port or on/off change." : string.Empty);
            GenericOpenAiUrlText.Text = status.BaseUrl + "/v1";
            GenericAnthropicUrlText.Text = status.BaseUrl;

            GatewayEnabledToggle.IsOn = status.Enabled;
            GatewayPortBox.Value = status.ConfiguredPort;
            foreach (object item in GatewayVendorModelCombo.Items)
                if (item is ComboBoxItem combo && (string)combo.Tag == status.VendorModelDefault) GatewayVendorModelCombo.SelectedItem = combo;
        }

        private Border BuildClientIntegrationCard(IClientIntegration client, ClientDetection detection, IntegrationManifest? manifest)
        {
            string automation = "Settings_Integration_" + string.Concat(client.ClientId.Where(char.IsLetterOrDigit));
            var panel = new StackPanel { Spacing = 6 };

            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new TextBlock
            {
                Text = client.DisplayName,
                Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"],
                VerticalAlignment = VerticalAlignment.Center
            });
            var statusText = new TextBlock
            {
                Text = ClientIntegrationStatusText.Label(detection.Status) + (detection.Version != null ? " · v" + detection.Version : string.Empty),
                Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(statusText, automation + "_Status_Text");
            heading.Children.Add(statusText);
            panel.Children.Add(heading);

            panel.Children.Add(new TextBlock
            {
                Text = client.Protocol + ". " + detection.Detail,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
            });
            if (manifest?.LastTestUtc != null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = (manifest.LastTestPassed == true ? "Last test passed: " : "Last test failed: ") + manifest.LastTestDetail
                           + " (" + manifest.LastTestUtc.Value.ToLocalTime().ToString("g") + ")",
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                });
            }

            var progress = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"], Visibility = Visibility.Collapsed };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(progress, automation + "_Progress_Text");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            bool connected = manifest != null;
            bool gatewayReady = _gatewayStatus?.Listening == true;
            if (detection.Installed)
            {
                buttons.Children.Add(ActionButton(connected ? "Reconnect" : "Connect", automation + "_Connect_Button", gatewayReady,
                    () => RunIntegrationActionAsync(client, progress, (service, reporter) =>
                        service.ConnectAsync(client, _gatewayStatus!.BaseUrl, ClientIntegrationService.DefaultModel, reporter, CancellationToken.None))));
                if (connected)
                {
                    buttons.Children.Add(ActionButton("Launch " + client.DisplayName + " with PaRuX", automation + "_Launch_Button", gatewayReady,
                        () => RunIntegrationActionAsync(client, progress, (service, _) =>
                            Task.FromResult(service.Launch(client, IntegrationWorkingFolder)))));
                    buttons.Children.Add(ActionButton("Test", automation + "_Test_Button", gatewayReady,
                        () => RunIntegrationActionAsync(client, progress, (service, reporter) =>
                        {
                            reporter.Report("Running a real test with " + client.DisplayName + "…");
                            return service.TestAsync(client, CancellationToken.None);
                        })));
                    buttons.Children.Add(ActionButton("Disconnect", automation + "_Disconnect_Button", true,
                        () => RunIntegrationActionAsync(client, progress, (service, _) => service.DisconnectAsync(client, CancellationToken.None))));
                    if (manifest!.Files.Any(f => f.BackupPath != null))
                        buttons.Children.Add(ActionButton("Restore original", automation + "_Restore_Button", true,
                            () => ConfirmAndRestoreAsync(client, progress)));
                }
            }
            if (!gatewayReady && detection.Installed)
                panel.Children.Add(new TextBlock
                {
                    Text = "The PaRuX gateway is not running, so Connect, Launch and Test are unavailable.",
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                });
            panel.Children.Add(buttons);
            panel.Children.Add(progress);

            return new Border
            {
                Child = panel,
                Padding = new Thickness(12),
                CornerRadius = (CornerRadius)Application.Current.Resources["PaRuXRadiusMedium"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["PaRuXStrokeBrush"],
                BorderThickness = new Thickness(1)
            };
        }

        private Button ActionButton(string text, string automationId, bool enabled, Func<Task> action)
        {
            var button = new Button { Content = text, IsEnabled = enabled && !_integrationBusy };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, automationId);
            button.Click += async (s, e) => await action();
            return button;
        }

        private async Task RunIntegrationActionAsync(IClientIntegration client, TextBlock progress,
            Func<ClientIntegrationService, IProgress<string>, Task<IntegrationOutcome>> action)
        {
            if (_integrationBusy) return;
            _integrationBusy = true;
            SetIntegrationButtonsEnabled(false);
            progress.Visibility = Visibility.Visible;
            var reporter = new Progress<string>(message => progress.Text = message);
            IntegrationOutcome outcome;
            try
            {
                outcome = await action(ClientIntegrationRuntime.Current, reporter);
            }
            catch (Exception ex)
            {
                Log.Write(client.ClientId + " integration action failed: " + ex);
                outcome = new IntegrationOutcome(false, ClientIntegrationStatus.ConfigurationError, ex.Message);
            }
            _integrationBusy = false;
            await RefreshIntegrationsAsync();
            // The refresh rebuilt the cards; show the outcome on the new card for this client.
            TextBlock? fresh = FindIntegrationProgress(client);
            if (fresh != null)
            {
                fresh.Visibility = Visibility.Visible;
                fresh.Text = (outcome.Success ? "✓ " : "✗ ") + outcome.Message;
            }
        }

        private TextBlock? FindIntegrationProgress(IClientIntegration client)
        {
            string id = "Settings_Integration_" + string.Concat(client.ClientId.Where(char.IsLetterOrDigit)) + "_Progress_Text";
            foreach (Border card in ClientIntegrationsList.Children.OfType<Border>())
                if (card.Child is StackPanel panel)
                    foreach (TextBlock text in panel.Children.OfType<TextBlock>())
                        if (Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(text) == id) return text;
            return null;
        }

        private void SetIntegrationButtonsEnabled(bool enabled)
        {
            foreach (Border card in ClientIntegrationsList.Children.OfType<Border>())
                if (card.Child is StackPanel panel)
                    foreach (StackPanel row in panel.Children.OfType<StackPanel>())
                        foreach (Button button in row.Children.OfType<Button>())
                            button.IsEnabled = enabled;
        }

        private async Task ConfirmAndRestoreAsync(IClientIntegration client, TextBlock progress)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Restore " + client.DisplayName + "'s original configuration?",
                Content = "PaRuX will put back the file exactly as it was before the first Connect and revoke the PaRuX key. Changes you made to that file since then will be lost; Disconnect keeps them and removes only PaRuX's entry.",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await RunIntegrationActionAsync(client, progress, (service, _) => service.RestoreAsync(client, CancellationToken.None));
        }

        private async void RefreshIntegrations_Click(object sender, RoutedEventArgs e) => await RefreshIntegrationsAsync();

        private async void ChooseIntegrationFolder_Click(object sender, RoutedEventArgs e)
        {
            string? folder = await PickFolderAsync();
            if (folder == null) return;
            IntegrationWorkingFolder = folder;
            IntegrationWorkingFolderBox.Text = folder;
        }

        private async void SaveGatewaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int port = double.IsNaN(GatewayPortBox.Value) ? 11435 : (int)GatewayPortBox.Value;
                string model = (GatewayVendorModelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "parux-auto";
                await RemoteAccessClient.Current.SaveGatewaySettingsAsync(
                    new GatewaySettingsUpdate { Enabled = GatewayEnabledToggle.IsOn, Port = port, VendorModelDefault = model }, CancellationToken.None);
                GatewaySettingsStatusText.Text = "Saved.";
                await RefreshIntegrationsAsync();
            }
            catch (Exception ex)
            {
                GatewaySettingsStatusText.Text = "Couldn't save: " + ex.Message;
            }
        }

        private async void CreateGenericGatewayKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var created = await RemoteAccessClient.Current.CreateApiKeyAsync("Generic app (gateway)", CancellationToken.None);
                GenericKeyText.Text = created.Key;
                GenericKeyPanel.Visibility = Visibility.Visible;
                _ = RefreshApiKeysAsync();
            }
            catch (Exception ex)
            {
                GenericKeyText.Text = "Couldn't create a key: " + ex.Message;
                GenericKeyPanel.Visibility = Visibility.Visible;
            }
        }

        private void CopyGenericOpenAiUrl_Click(object sender, RoutedEventArgs e) => CopyText(GenericOpenAiUrlText.Text);
        private void CopyGenericAnthropicUrl_Click(object sender, RoutedEventArgs e) => CopyText(GenericAnthropicUrlText.Text);
        private void CopyGenericKey_Click(object sender, RoutedEventArgs e) => CopyText(GenericKeyText.Text);

        private static void CopyText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch (Exception ex)
            {
                Log.Write("Copy failed: " + ex.Message);
            }
        }
    }
}
