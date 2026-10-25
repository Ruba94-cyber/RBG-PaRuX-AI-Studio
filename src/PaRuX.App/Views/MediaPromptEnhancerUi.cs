using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Optional "Enhance prompt" step for the Create workspace: toggle (default OFF) + review dialog.</summary>
    internal static class MediaPromptEnhancerUi
    {
        public const string ToggleAutomationId = "Create_EnhancePrompt_Toggle";
        public const string EnhancedBoxAutomationId = "Create_EnhancePrompt_Enhanced_Box";

        /// <summary>The image toggle keeps the canonical id; other panels pass a suffix so ids stay page-unique.</summary>
        public static CheckBox CreateToggle(string idSuffix = "") =>
            Named(new CheckBox
            {
                Content = "Enhance prompt with the local text model (review before generating)",
                IsChecked = false
            }, ToggleAutomationId + idSuffix);

        private static T Named<T>(T element, string id) where T : DependencyObject
        {
            AutomationProperties.SetAutomationId(element, id);
            return element;
        }

        public sealed record Outcome(string PromptToUse, PromptEnhancementResult Result, bool UsedEnhanced);

        /// <summary>Returns null when the user cancels generation.</summary>
        public static async Task<Outcome?> ReviewAsync(XamlRoot root, string kind, string prompt, CancellationToken ct)
        {
            bool remoteSeen = false;
            // Decide locality BEFORE anything is sent. If cloud fallback is configured, the text route may leave the
            // device, and there is no per-feature consent for enhancement - so fail closed (also when status is unknown).
            bool modelIsLocal;
            try { modelIsLocal = !(await CloudFallbackClient.Current.GetStatusAsync(ct)).Enabled; }
            catch (OperationCanceledException) { throw; }
            catch { modelIsLocal = false; }
            PromptEnhancementResult result = await MediaPromptEnhancer.EnhanceAsync(kind, prompt, async (instruction, token) =>
            {
                var answer = new StringBuilder();
                await ChatClient.Current.StreamAsync(
                    new[] { new PaRuX.Models.ChatMessage { Role = "user", Content = instruction } },
                    "You rewrite prompts for media generation models. Reply with only the rewritten prompt.",
                    d => { if (d != null && answer.Length < 8192) answer.Append(d); },
                    null, null, null, null, null, true, token,
                    onProvenance: p => { if (!string.Equals(p.Locality, ChatProvenance.LocalLocality, StringComparison.OrdinalIgnoreCase) && p.Locality != null) remoteSeen = true; });
                // No cloud text without explicit consent: a remote answer is discarded, never shown or used.
                if (remoteSeen) throw new InvalidOperationException("remote provider not permitted");
                return answer.ToString();
            }, localOnly: false, modelIsLocal: modelIsLocal, cloudConsent: false, ct: ct);

            var original = new TextBlock { Text = "Your prompt", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var originalText = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            var enhancedLabel = new TextBlock
            {
                Text = result.UsedFallback ? "Enhancement not applied (nothing was sent to a model): " + result.FallbackReason + ". Your original will be used unless you edit it." : "Enhanced prompt (editable)",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            var enhancedBox = Named(new TextBox { Text = result.Enhanced, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220 }, EnhancedBoxAutomationId);
            var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
            panel.Children.Add(original);
            panel.Children.Add(originalText);
            panel.Children.Add(enhancedLabel);
            panel.Children.Add(enhancedBox);

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Review prompt",
                Content = panel,
                PrimaryButtonText = "Generate with this prompt",
                SecondaryButtonText = "Use my original",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary
            };
            ContentDialogResult choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None) return null;
            if (choice == ContentDialogResult.Secondary) return new Outcome(prompt, result, false);

            string chosen = enhancedBox.Text.Trim();
            if (chosen.Length == 0) chosen = prompt;
            return new Outcome(chosen, result with { Enhanced = chosen }, !string.Equals(chosen, prompt, StringComparison.Ordinal));
        }
    }
}
