using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings for the optional cloud fallback. The section is written to make the boundary
    /// obvious rather than to make the feature attractive: the switch is off, the state line always
    /// says plainly whether a turn could leave the device, and once one has, the section keeps
    /// saying so afterwards instead of only mentioning it in the chat stream that scrolled past.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _cloudFallbackLoading;
        private bool _cloudFallbackSetupPending;
        private long _cloudFallbackUserActionVersion;
        private int _cloudFallbackSaveOperations;
        private int _cloudFallbackTestOperations;
        private readonly CloudFallbackMutationQueue _cloudFallbackMutations = new();

        private async Task RefreshCloudFallbackAsync()
        {
            if (CloudFallbackStatusText == null || _cloudFallbackSetupPending) return;
            long requestVersion = _cloudFallbackUserActionVersion;
            try
            {
                CloudFallbackStatus status = await CloudFallbackClient.Current.GetStatusAsync(CancellationToken.None);
                // A status read that began before a user action must not undo the unsaved form or a newer toggle.
                if (requestVersion != _cloudFallbackUserActionVersion || _cloudFallbackSetupPending) return;
                ApplyCloudFallbackStatus(status);
            }
            catch (Exception ex)
            {
                Log.Write("RefreshCloudFallbackAsync failed: " + ex.Message);
                if (requestVersion != _cloudFallbackUserActionVersion || _cloudFallbackSetupPending) return;
                CloudFallbackStatusText.Text = "Couldn't load cloud fallback status: " + ex.Message;
            }
        }

        private void ApplyCloudFallbackStatus(CloudFallbackStatus status)
        {
            _cloudFallbackLoading = true;
            try
            {
                if (CloudFallbackProviderCombo != null && CloudFallbackProviderCombo.Items.Count == 0)
                {
                    foreach (CloudFallbackProviderOption option in status.AvailableProviders)
                    {
                        CloudFallbackProviderCombo.Items.Add(new ComboBoxItem
                        {
                            Content = option.DisplayName,
                            Tag = option
                        });
                    }
                }

                if (CloudFallbackProviderCombo != null)
                {
                    ComboBoxItem? match = CloudFallbackProviderCombo.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => item.Tag is CloudFallbackProviderOption option
                            && string.Equals(option.Id, status.ProviderId, StringComparison.OrdinalIgnoreCase));
                    CloudFallbackProviderCombo.SelectedItem = match ?? CloudFallbackProviderCombo.Items.FirstOrDefault();
                }

                if (CloudFallbackToggle != null) CloudFallbackToggle.IsOn = status.Enabled;
                if (CloudFallbackDetailPanel != null)
                    CloudFallbackDetailPanel.Visibility = status.Enabled ? Visibility.Visible : Visibility.Collapsed;
                if (CloudFallbackBaseUrlBox != null) CloudFallbackBaseUrlBox.Text = status.BaseUrl ?? string.Empty;
                if (CloudFallbackModelBox != null) CloudFallbackModelBox.Text = status.Model ?? string.Empty;
                if (CloudFallbackApiKeyBox != null)
                {
                    // Never round-trip the credential to the UI. An empty box with this placeholder
                    // means "a key is stored, leave it alone"; typing replaces it.
                    CloudFallbackApiKeyBox.Password = string.Empty;
                    CloudFallbackApiKeyBox.PlaceholderText = status.HasApiKey
                        ? "A key is saved - type to replace it"
                        : "Paste the provider API key";
                }

                ApplyCloudFallbackProviderDefaults(fillEmptyFieldsOnly: true);

                CloudFallbackStatusText.Text = status.Armed
                    ? "Armed: if local inference fails, one turn may be answered by "
                        + status.ProviderDisplayName + " (" + status.Model + ") and leave this device."
                    : status.Enabled
                        ? "Not armed - " + (status.NotArmedReason ?? "the fallback is incomplete.")
                        : "Off. Every turn stays on this device; a local failure stays a failure.";

                if (CloudFallbackLastUsedText != null)
                {
                    if (status.LastUsedUtc.HasValue)
                    {
                        CloudFallbackLastUsedText.Visibility = Visibility.Visible;
                        CloudFallbackLastUsedText.Text = "Last used "
                            + status.LastUsedUtc.Value.ToLocalTime().ToString("g")
                            + " (" + status.UseCount + " turn" + (status.UseCount == 1 ? "" : "s") + " total). Reason: "
                            + (status.LastUsedReason ?? "not recorded");
                    }
                    else
                    {
                        CloudFallbackLastUsedText.Visibility = Visibility.Collapsed;
                    }
                }
            }
            finally
            {
                _cloudFallbackLoading = false;
            }
        }

        private CloudFallbackProviderOption? SelectedCloudFallbackProvider() =>
            (CloudFallbackProviderCombo?.SelectedItem as ComboBoxItem)?.Tag as CloudFallbackProviderOption;

        /// <summary>
        /// A preset knows its own endpoint, so the URL box is read-only for one and editable only
        /// for "custom". <paramref name="fillEmptyFieldsOnly"/> keeps a value the user already saved
        /// from being overwritten when the section is merely re-read.
        /// </summary>
        private void ApplyCloudFallbackProviderDefaults(bool fillEmptyFieldsOnly)
        {
            CloudFallbackProviderOption? option = SelectedCloudFallbackProvider();
            if (option == null) return;

            bool custom = string.Equals(option.Id, "custom", StringComparison.OrdinalIgnoreCase);
            if (CloudFallbackBaseUrlBox != null)
            {
                CloudFallbackBaseUrlBox.IsReadOnly = !custom;
                if (!custom && (!fillEmptyFieldsOnly || string.IsNullOrWhiteSpace(CloudFallbackBaseUrlBox.Text)))
                    CloudFallbackBaseUrlBox.Text = option.BaseUrl;
                else if (custom && !fillEmptyFieldsOnly)
                    CloudFallbackBaseUrlBox.Text = string.Empty;
            }

            if (CloudFallbackModelBox != null
                && !string.IsNullOrWhiteSpace(option.SuggestedModel)
                && (!fillEmptyFieldsOnly || string.IsNullOrWhiteSpace(CloudFallbackModelBox.Text)))
            {
                CloudFallbackModelBox.Text = option.SuggestedModel;
            }
        }

        private void CloudFallbackProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cloudFallbackLoading) return;
            ApplyCloudFallbackProviderDefaults(fillEmptyFieldsOnly: false);
        }

        private async void CloudFallbackToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_cloudFallbackLoading || CloudFallbackToggle == null) return;
            long actionVersion = ++_cloudFallbackUserActionVersion;

            if (!CloudFallbackToggle.IsOn)
            {
                // Turning it off drops the stored credential server-side as well - the capability and
                // the key it needs go away together.
                try
                {
                    CloudFallbackStatus status = await _cloudFallbackMutations.EnqueueAsync(
                        () => CloudFallbackClient.Current.DisableAsync(CancellationToken.None));
                    if (actionVersion != _cloudFallbackUserActionVersion) return;
                    _cloudFallbackSetupPending = false;
                    ApplyCloudFallbackStatus(status);
                }
                catch (Exception ex)
                {
                    if (actionVersion != _cloudFallbackUserActionVersion) return;
                    Log.Write("CloudFallbackToggle_Toggled (off) failed: " + ex.Message);
                    // The server decides what the switch shows, not the click. If the fallback is still armed the
                    // switch stays On, and the status line says that the state could not be confirmed.
                    CloudFallbackStatus? observed = null;
                    ToggleRecovery recovered = await ToggleFailureRecovery.RecoverAsync(
                        lastKnownServerState: true,
                        readServerState: async ct =>
                        {
                            observed = await CloudFallbackClient.Current.GetStatusAsync(ct);
                            return observed.Enabled;
                        },
                        CancellationToken.None);
                    if (actionVersion != _cloudFallbackUserActionVersion) return;
                    string failure = "Couldn't turn the fallback off: " + ex.Message;
                    if (observed != null)
                    {
                        // Applies the full status the server just reported (switch, detail panel and state line).
                        _cloudFallbackSetupPending = false;
                        ApplyCloudFallbackStatus(observed);
                        CloudFallbackStatusText.Text = failure + "\n" + CloudFallbackStatusText.Text;
                    }
                    else
                    {
                        _cloudFallbackLoading = true;
                        try
                        {
                            CloudFallbackToggle.IsOn = recovered.Displayed;
                        }
                        finally
                        {
                            _cloudFallbackLoading = false;
                        }
                        if (CloudFallbackDetailPanel != null)
                            CloudFallbackDetailPanel.Visibility = recovered.Displayed ? Visibility.Visible : Visibility.Collapsed;
                        CloudFallbackStatusText.Text = failure
                            + " The server could not confirm whether the fallback is still armed; the switch keeps its last known state.";
                    }
                }
                return;
            }

            // Turning it on only opens the form. Nothing is armed until Save succeeds with a key and
            // a model, so flipping this switch by itself can never send anything anywhere.
            _cloudFallbackSetupPending = true;
            if (CloudFallbackDetailPanel != null) CloudFallbackDetailPanel.Visibility = Visibility.Visible;
            CloudFallbackStatusText.Text = "Choose a provider, paste an API key and a model id, then Save.";
        }

        private async void SaveCloudFallback_Click(object sender, RoutedEventArgs e)
        {
            CloudFallbackProviderOption? option = SelectedCloudFallbackProvider();
            if (option == null)
            {
                CloudFallbackStatusText.Text = "Choose a provider first.";
                return;
            }

            long actionVersion = ++_cloudFallbackUserActionVersion;

            string apiKey = CloudFallbackApiKeyBox?.Password ?? string.Empty;
            var request = new SetCloudFallbackRequest
            {
                Enabled = true,
                ProviderId = option.Id,
                BaseUrl = CloudFallbackBaseUrlBox?.Text?.Trim(),
                Model = CloudFallbackModelBox?.Text?.Trim() ?? string.Empty,
                // Null, not empty: an untouched box must keep the saved key rather than clear it.
                ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey
            };

            try
            {
                _cloudFallbackSaveOperations++;
                if (SaveCloudFallbackButton != null) SaveCloudFallbackButton.IsEnabled = false;
                if (TestCloudFallbackButton != null) TestCloudFallbackButton.IsEnabled = false;
                CloudFallbackStatus status = await _cloudFallbackMutations.EnqueueAsync(
                    () => CloudFallbackClient.Current.SaveAsync(request, CancellationToken.None));
                if (actionVersion != _cloudFallbackUserActionVersion) return;
                _cloudFallbackSetupPending = false;
                ApplyCloudFallbackStatus(status);
            }
            catch (Exception ex)
            {
                if (actionVersion != _cloudFallbackUserActionVersion) return;
                Log.Write("SaveCloudFallback_Click failed: " + ex.Message);
                CloudFallbackStatusText.Text = "Couldn't save the fallback: " + ex.Message;
            }
            finally
            {
                if (SaveCloudFallbackButton != null)
                {
                    if (_cloudFallbackSaveOperations > 0) _cloudFallbackSaveOperations--;
                    SaveCloudFallbackButton.IsEnabled = _cloudFallbackSaveOperations == 0 && _cloudFallbackTestOperations == 0;
                    if (TestCloudFallbackButton != null)
                        TestCloudFallbackButton.IsEnabled = _cloudFallbackSaveOperations == 0 && _cloudFallbackTestOperations == 0;
                }
            }
        }

        private async void TestCloudFallback_Click(object sender, RoutedEventArgs e)
        {
            long actionVersion = ++_cloudFallbackUserActionVersion;
            try
            {
                _cloudFallbackTestOperations++;
                if (TestCloudFallbackButton != null) TestCloudFallbackButton.IsEnabled = false;
                if (SaveCloudFallbackButton != null) SaveCloudFallbackButton.IsEnabled = false;
                CloudFallbackStatusText.Text = "Checking the provider...";
                CloudFallbackTestResult result = await CloudFallbackClient.Current.TestAsync(CancellationToken.None);
                if (actionVersion != _cloudFallbackUserActionVersion) return;
                CloudFallbackStatusText.Text = result.Detail;
                if (result.Success) await RefreshCloudFallbackAsync();
            }
            catch (Exception ex)
            {
                if (actionVersion != _cloudFallbackUserActionVersion) return;
                Log.Write("TestCloudFallback_Click failed: " + ex.Message);
                CloudFallbackStatusText.Text = "Couldn't reach the provider: " + ex.Message;
            }
            finally
            {
                if (TestCloudFallbackButton != null)
                {
                    if (_cloudFallbackTestOperations > 0) _cloudFallbackTestOperations--;
                    TestCloudFallbackButton.IsEnabled = _cloudFallbackTestOperations == 0;
                    if (SaveCloudFallbackButton != null)
                        SaveCloudFallbackButton.IsEnabled = _cloudFallbackTestOperations == 0 && _cloudFallbackSaveOperations == 0;
                }
            }
        }
    }
}
