using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Presents a one-download review gate when repository license metadata is missing,
    /// unavailable, or outside the conservative permissive SPDX allow-list. This is not
    /// legal verification and deliberately does not persist consent.
    /// </summary>
    internal static class ModelLicenseConsentPrompt
    {
        public static async Task<bool> ConfirmAndStartRepositoryDownloadAsync(
            XamlRoot xamlRoot, string repoId, string fileName,
            Func<string, ModelDownloadLicenseEvidence, Task> startDownload)
        {
            ModelRepoDetails? details = null;
            bool metadataRetrieved = false;
            try
            {
                details = await ModelDownloadClient.Current.GetDetailsAsync(repoId, System.Threading.CancellationToken.None);
                metadataRetrieved = true;
            }
            catch (Exception ex)
            {
                Log.Write("Model license metadata lookup failed for " + repoId + ": " + ex.Message);
            }

            if (!ModelRevisionIdentity.IsValid(details?.Revision))
                return await ShowRevisionUnavailableAsync(xamlRoot);

            ModelLicenseConsentPolicy.Decision decision = ModelLicenseConsentPolicy.Evaluate(details?.License, metadataRetrieved);
            return await ConfirmAndStartAtRevisionAsync(xamlRoot, repoId, fileName, decision, details!.Revision!, startDownload);
        }

        public static async Task<bool> ConfirmAndStartAtRevisionAsync(
            XamlRoot xamlRoot,
            string repoId,
            string fileName,
            ModelLicenseConsentPolicy.Decision decision,
            string? revision,
            Func<string, ModelDownloadLicenseEvidence, Task> startDownload)
        {
            string? normalizedRevision = ModelRevisionIdentity.Normalize(revision);
            if (normalizedRevision == null) return await ShowRevisionUnavailableAsync(xamlRoot);
            if (startDownload == null) throw new ArgumentNullException(nameof(startDownload));

            return await ConfirmAndStartCoreAsync(
                xamlRoot, repoId, fileName, decision, normalizedRevision,
                evidence => startDownload(normalizedRevision, evidence));
        }

        private static async Task<bool> ConfirmAndStartCoreAsync(
            XamlRoot xamlRoot,
            string repoId,
            string fileName,
            ModelLicenseConsentPolicy.Decision decision,
            string revision,
            Func<ModelDownloadLicenseEvidence, Task> startDownload)
        {
            if (startDownload == null) throw new ArgumentNullException(nameof(startDownload));
            ModelDownloadLicenseEvidence evidence = new()
            {
                MetadataStatus = decision.LicenseMetadataStatus,
                PublisherDeclaredLicense = decision.PublisherDeclaredLicense,
                MetadataObservedUtc = decision.MetadataObservedUtc,
                ReviewRequired = decision.RequiresReview
            };
            if (!decision.RequiresReview)
            {
                await startDownload(evidence);
                return true;
            }

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = decision.MetadataStatus,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = decision.MetadataRetrieved
                    ? "Hugging Face repository metadata is publisher-declared and has not been independently verified."
                    : "Hugging Face repository metadata could not be loaded. Review the repository terms before proceeding.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            });

            content.Children.Add(new TextBlock
            {
                Text = "Selected immutable repository revision: " + revision,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = "Publisher licence metadata observed (UTC): " + decision.MetadataObservedUtc.ToString("u"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.82
            });

            Uri? modelCardUri = ModelLicenseConsentPolicy.GetModelCardUri(repoId, revision);
            if (modelCardUri != null)
            {
                content.Children.Add(new HyperlinkButton
                {
                    Content = "Review model card and repository terms",
                    NavigateUri = modelCardUri,
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }

            var confirmation = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = "I reviewed the repository/model card terms and choose to download " + fileName + ". This decision applies to this download only.",
                    TextWrapping = TextWrapping.Wrap
                },
                IsChecked = false
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirmation, "ModelLicenseConsent_Confirm_CheckBox");
            var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(validation, "ModelLicenseConsent_Validation_Text");
            content.Children.Add(confirmation);
            content.Children.Add(validation);
            content.Children.Add(new TextBlock
            {
                Text = "PaRuX does not independently determine whether these terms are appropriate for your use. No download starts unless you confirm below.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Review model terms before downloading",
                Content = content,
                PrimaryButtonText = "Download selected file",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "ModelLicenseConsent_Dialog");
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (confirmation.IsChecked != true)
                {
                    args.Cancel = true;
                    validation.Text = "Review the repository terms and select the confirmation before downloading.";
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            evidence.ReviewAcknowledged = true;
            evidence.ReviewAcknowledgedUtc = DateTime.UtcNow;
            await startDownload(evidence);
            return true;
        }

        private static async Task<bool> ShowRevisionUnavailableAsync(XamlRoot xamlRoot)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "Cannot pin this model revision",
                Content = new TextBlock
                {
                    Text = "Hugging Face did not provide a valid immutable commit SHA. PaRuX cannot bind the reviewed terms to exact model bytes, so this new download was not started. Try again when repository metadata is available.",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "Close"
            };
            await dialog.ShowAsync();
            return false;
        }
    }
}
