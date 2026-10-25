using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private bool _modelDownloadSettingsAdded;

        private void AddModelDownloadSettingsSection()
        {
            if (_modelDownloadSettingsAdded || SettingsContentPanel == null) return;
            _modelDownloadSettingsAdded = true;

            var section = new StackPanel
            {
                Tag = "Models",
                Spacing = 8,
                Visibility = string.Equals(_selectedSettingsCategory, "Models", StringComparison.Ordinal)
                    ? Visibility.Visible : Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(section, "Settings_ModelDownloads_Section");
            section.Children.Add(new TextBlock
            {
                Text = "MODEL DOWNLOADS",
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
                && stroke is Microsoft.UI.Xaml.Media.Brush brush)
                card.BorderBrush = brush;

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "One bandwidth limit is shared by every model download. Set 0 for unlimited.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var bandwidthBox = new TextBox
            {
                Header = "Shared limit (MB/s)",
                PlaceholderText = "0 = unlimited",
                MinWidth = 220
            };
            AutomationProperties.SetAutomationId(bandwidthBox, "Settings_ModelDownloadBandwidth_TextBox");
            content.Children.Add(bandwidthBox);

            var bandwidthActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var saveBandwidth = new Button { Content = "Save bandwidth limit" };
            AutomationProperties.SetAutomationId(saveBandwidth, "Settings_ModelDownloadBandwidth_Save_Button");
            var bandwidthStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(bandwidthStatus, "Settings_ModelDownloadBandwidth_Status");
            bandwidthActions.Children.Add(saveBandwidth);
            bandwidthActions.Children.Add(bandwidthStatus);
            content.Children.Add(bandwidthActions);

            var divider = new Border { Height = 1, Opacity = 0.25, Margin = new Thickness(0, 4, 0, 4) };
            content.Children.Add(divider);
            content.Children.Add(new TextBlock
            {
                Text = "Hugging Face access token",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = "Only needed for gated or private repositories. PaRuX stores it with the server's local credential protection. The saved value is write-only and is never shown here.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var tokenBox = new PasswordBox { PlaceholderText = "Enter a token to save or replace it" };
            AutomationProperties.SetAutomationId(tokenBox, "Settings_HuggingFaceToken_PasswordBox");
            content.Children.Add(tokenBox);

            var credentialActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var saveToken = new Button { Content = "Save token" };
            AutomationProperties.SetAutomationId(saveToken, "Settings_HuggingFaceToken_Save_Button");
            var removeToken = new Button { Content = "Remove saved token", IsEnabled = false };
            AutomationProperties.SetAutomationId(removeToken, "Settings_HuggingFaceToken_Remove_Button");
            credentialActions.Children.Add(saveToken);
            credentialActions.Children.Add(removeToken);
            content.Children.Add(credentialActions);

            var credentialStatus = new TextBlock { Text = "Checking saved-token status…", TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
            AutomationProperties.SetAutomationId(credentialStatus, "Settings_HuggingFaceToken_Status");
            content.Children.Add(credentialStatus);

            saveBandwidth.Click += async (sender, args) =>
            {
                if (!ModelDownloadSettingsPolicy.TryParseBandwidthLimit(bandwidthBox.Text, out double limit))
                {
                    bandwidthStatus.Text = "Enter a number from 0 to 10000 MB/s.";
                    return;
                }

                saveBandwidth.IsEnabled = false;
                try
                {
                    await ModelDownloadClient.Current.SaveTransferSettingsAsync(
                        new ModelDownloadTransferSettings { BandwidthLimitMegabytesPerSecond = limit }, default);
                    bandwidthStatus.Text = "Saved · " + ModelDownloadSettingsPolicy.DescribeBandwidthLimit(limit)
                        + " shared across model downloads.";
                }
                catch (Exception ex) { bandwidthStatus.Text = "Couldn't save the bandwidth limit: " + ex.Message; }
                finally { saveBandwidth.IsEnabled = true; }
            };

            saveToken.Click += async (sender, args) =>
            {
                string token = tokenBox.Password;
                if (string.IsNullOrWhiteSpace(token))
                {
                    credentialStatus.Text = "Enter a token to save or replace the current one.";
                    return;
                }

                saveToken.IsEnabled = false;
                try
                {
                    await ModelDownloadClient.Current.SaveHubCredentialAsync(token.Trim(), default);
                    tokenBox.Password = string.Empty;
                    await RefreshCredentialStatusAsync();
                }
                catch
                {
                    // Do not include request or response content here; the token is never reflected into UI text.
                    credentialStatus.Text = "Couldn't save the token. Check PaRuX Core and try again.";
                }
                finally { saveToken.IsEnabled = true; }
            };

            removeToken.Click += async (sender, args) =>
            {
                removeToken.IsEnabled = false;
                try
                {
                    await ModelDownloadClient.Current.ClearHubCredentialAsync(default);
                    tokenBox.Password = string.Empty;
                    await RefreshCredentialStatusAsync();
                }
                catch
                {
                    credentialStatus.Text = "Couldn't remove the saved token. Check PaRuX Core and try again.";
                    removeToken.IsEnabled = true;
                }
            };

            card.Child = content;
            section.Children.Add(card);
            SettingsContentPanel.Children.Add(section);
            _ = LoadModelDownloadSettingsAsync();

            async System.Threading.Tasks.Task RefreshCredentialStatusAsync()
            {
                try
                {
                    ModelDownloadCredentialStatus status = await ModelDownloadClient.Current.GetHubCredentialStatusAsync(default);
                    credentialStatus.Text = ModelDownloadSettingsPolicy.DescribeCredentialStatus(status);
                    removeToken.IsEnabled = status.TokenConfigured;
                }
                catch
                {
                    credentialStatus.Text = "Couldn't check saved-token status. The token value is never displayed.";
                    removeToken.IsEnabled = false;
                }
            }

            async System.Threading.Tasks.Task LoadModelDownloadSettingsAsync()
            {
                try
                {
                    ModelDownloadTransferSettings settings = await ModelDownloadClient.Current.GetTransferSettingsAsync(default);
                    bandwidthBox.Text = settings.BandwidthLimitMegabytesPerSecond.ToString(
                        "0.##", System.Globalization.CultureInfo.CurrentCulture);
                    bandwidthStatus.Text = "Current: "
                        + ModelDownloadSettingsPolicy.DescribeBandwidthLimit(settings.BandwidthLimitMegabytesPerSecond);
                }
                catch (Exception ex) { bandwidthStatus.Text = "Couldn't load the shared limit: " + ex.Message; }
                await RefreshCredentialStatusAsync();
            }
        }
    }
}
