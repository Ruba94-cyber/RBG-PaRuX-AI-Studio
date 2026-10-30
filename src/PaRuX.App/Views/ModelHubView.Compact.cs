using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Progressive disclosure for Model Hub: keep catalogue rows short and
    /// open technical metadata and quantization choices only on demand.</summary>
    public sealed partial class ModelHubView
    {
        private async Task ShowModelDetailsAsync(
            string title, IEnumerable<(string Label, string? Value)> fields, Uri? modelCardUri = null)
        {
            var content = new StackPanel { Spacing = 10 };
            foreach ((string label, string? value) in fields)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var section = new StackPanel { Spacing = 3 };
                section.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 12, Opacity = 0.72,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                section.Children.Add(new TextBlock
                {
                    Text = value, TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
                content.Children.Add(section);
            }
            if (modelCardUri is { Scheme: "https", Host: "huggingface.co" })
                content.Children.Add(new HyperlinkButton
                {
                    Content = "Read model card and repository terms",
                    NavigateUri = modelCardUri,
                    HorizontalAlignment = HorizontalAlignment.Left
                });

            var scroll = new ScrollViewer
            {
                Content = content, MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var dialog = new ContentDialog
            {
                Title = title, Content = scroll, CloseButtonText = "Close",
                XamlRoot = XamlRoot, MaxWidth = 720
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            AutomationProperties.SetAutomationId(dialog, "ModelHub_ModelDetails_Dialog");
            await dialog.ShowAsync();
        }

        private async Task ReviewSearchVariantsAsync(
            ModelSearchResult repo,
            IReadOnlyList<ModelHubSplitSetPresentation.QuantChoiceOption> choices,
            int preferredIndex)
        {
            if (choices.Count == 0)
            {
                _status.Text = "This repository has no GGUF variants to review.";
                return;
            }

            var picker = new ComboBox
            {
                MinWidth = 330,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                PlaceholderText = "Select a complete quantization"
            };
            AutomationProperties.SetAutomationId(picker, "ModelHub_VariantPicker_ComboBox");
            foreach (ModelHubSplitSetPresentation.QuantChoiceOption option in choices)
                picker.Items.Add(new ComboBoxItem
                {
                    Content = option.Label + " · ~" + FormatBytes(option.Choice.SizeBytes),
                    Tag = option.Choice,
                    IsEnabled = option.CanDownload
                });

            var hardwareText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
            };
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock
            {
                Text = repo.RepoId, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock
            {
                Text = "Choose a complete GGUF set. Size and hardware fit are estimates; the pinned download plan and licence are reviewed before any transfer.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
            });
            body.Children.Add(picker);
            body.Children.Add(hardwareText);
            var dialog = new ContentDialog
            {
                Title = "Quantization options",
                Content = body,
                PrimaryButtonText = "Review download",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                MaxWidth = 720
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            AutomationProperties.SetAutomationId(dialog, "ModelHub_QuantizationDialog");
            picker.SelectionChanged += (_, _) =>
            {
                ModelQuantChoice? selected = (picker.SelectedItem as ComboBoxItem)?.Tag as ModelQuantChoice;
                dialog.IsPrimaryButtonEnabled = selected?.Complete == true;
                if (selected == null) { hardwareText.Text = "No complete variant selected."; return; }
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(selected.SizeBytes, _hardware);
                hardwareText.Text = "On this PC: " + ModelHubRowPresentation.HardwareFit(fit.Verdict, fit.Summary);
            };
            if (preferredIndex >= 0 && preferredIndex < picker.Items.Count)
                picker.SelectedIndex = preferredIndex;
            dialog.IsPrimaryButtonEnabled = picker.SelectedItem is ComboBoxItem { Tag: ModelQuantChoice choice } && choice.Complete;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (picker.SelectedItem is not ComboBoxItem { Tag: ModelQuantChoice selectedChoice } || !selectedChoice.Complete) return;
            try { await ChooseQuantChoiceAsync(repo.RepoId, selectedChoice); }
            catch (Exception ex) { _status.Text = "Could not review this variant: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }
    }
}
