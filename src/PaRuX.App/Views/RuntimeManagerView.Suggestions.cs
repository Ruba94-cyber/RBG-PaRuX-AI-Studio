using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>"Suggested for this PC": read-only runtime/backend suggestions. Nothing is installed until the user
    /// presses the explicit install action, which goes through the same managed install flow as the component cards.</summary>
    public sealed partial class RuntimeManagerView
    {
        private readonly StackPanel _suggestionList = new StackPanel { Spacing = 10 };

        private UIElement BuildSuggestionsCard()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            AutomationProperties.SetAutomationId(card, "RuntimeManager_Suggestion_Card");
            AutomationProperties.SetName(card, "Suggested for this PC");
            var body = new StackPanel { Spacing = 8 };
            card.Child = body;
            body.Children.Add(new TextBlock
            {
                Text = "Suggested for this PC",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Optional changes for this PC. Nothing is installed without your approval.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            body.Children.Add(_suggestionList);
            return card;
        }

        private async Task RefreshSuggestionsAsync(CancellationToken ct)
        {
            _suggestionList.Children.Clear();
            RuntimeSuggestionsResponse response;
            try { response = await AutoMaintenanceClient.Current.GetRuntimeSuggestionsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _suggestionList.Children.Add(SuggestionNote("RuntimeManager_Suggestion_Status_Text", "Suggestions are unavailable right now: " + ex.Message));
                return;
            }

            if (response.Suggestions.Count == 0)
            {
                _suggestionList.Children.Add(SuggestionNote("RuntimeManager_Suggestion_Status_Text",
                    response.DismissedCount > 0
                        ? "No new suggestions (" + response.DismissedCount + " dismissed)."
                        : "No suggestions - nothing to change for this PC right now."));
                return;
            }
            foreach (RuntimeBackendSuggestion suggestion in response.Suggestions)
                _suggestionList.Children.Add(BuildSuggestionItem(suggestion));
        }

        private static TextBlock SuggestionNote(string automationId, string text)
        {
            var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
            AutomationProperties.SetAutomationId(note, automationId);
            return note;
        }

        private UIElement BuildSuggestionItem(RuntimeBackendSuggestion suggestion)
        {
            string key = SuggestionAutomationKey(suggestion.Id);
            var item = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
            AutomationProperties.SetAutomationId(item, "RuntimeManager_Suggestion_" + key + "_Item");
            var stack = new StackPanel { Spacing = 6 };
            item.Child = stack;

            var title = new TextBlock { Text = suggestion.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(title, "RuntimeManager_Suggestion_" + key + "_Title_Text");
            stack.Children.Add(title);
            stack.Children.Add(new TextBlock
            {
                Text = suggestion.Why,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
                FontSize = 12,
                Opacity = 0.84
            });

            var badge = new TextBlock { Text = MaintenancePresenter.EvidenceLabel(suggestion), FontSize = 11, Opacity = 0.72 };
            AutomationProperties.SetAutomationId(badge, "RuntimeManager_Suggestion_" + key + "_Evidence_Text");
            stack.Children.Add(badge);
            // The full hardware evidence is available on demand rather than repeated in each card.

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            actions.Children.Add(ActionButton("Details", () => ShowSuggestionDetailsAsync(suggestion),
                "RuntimeManager_Suggestion_" + key + "_Details_Button"));
            string? installLabel = MaintenancePresenter.PrimaryActionLabel(suggestion);
            if (installLabel != null)
                actions.Children.Add(ActionButton(installLabel, () => InstallSuggestionAsync(suggestion), "RuntimeManager_Suggestion_" + key + "_Install_Button"));
            if (suggestion.OfferBenchmark)
                actions.Children.Add(ActionButton("Verify with a quick local benchmark", () => OptimizeAsync(), "RuntimeManager_Suggestion_" + key + "_Benchmark_Button"));
            actions.Children.Add(ActionButton("Dismiss", async () =>
            {
                await AutoMaintenanceClient.Current.DismissRuntimeSuggestionAsync(suggestion.Id, CancellationToken.None);
                await RefreshSuggestionsAsync(CancellationToken.None);
            }, "RuntimeManager_Suggestion_" + key + "_Dismiss_Button"));
            stack.Children.Add(actions);
            return item;
        }

        private async Task ShowSuggestionDetailsAsync(RuntimeBackendSuggestion suggestion)
        {
            var details = new StackPanel { Spacing = 10 };
            details.Children.Add(new TextBlock { Text = suggestion.Why, TextWrapping = TextWrapping.Wrap });
            details.Children.Add(SuggestionNote("RuntimeManager_Suggestion_Detail_Evidence_Label",
                MaintenancePresenter.EvidenceLabel(suggestion)));
            if (suggestion.Evidence.Count > 0)
            {
                details.Children.Add(new TextBlock
                {
                    Text = "Evidence",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                foreach (string evidence in suggestion.Evidence)
                    details.Children.Add(new TextBlock { Text = "• " + evidence, TextWrapping = TextWrapping.Wrap });
            }
            if (suggestion.Informational && suggestion.Action == "none")
                details.Children.Add(new TextBlock
                {
                    Text = "Informational only: PaRuX will not change this runtime.",
                    TextWrapping = TextWrapping.Wrap
                });
            var dialog = new ContentDialog
            {
                Title = suggestion.Title,
                Content = new ScrollViewer { Content = details, MaxHeight = 380 },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task InstallSuggestionAsync(RuntimeBackendSuggestion suggestion)
        {
            ComponentStatus? component = _lastComponents.FirstOrDefault(c => string.Equals(c.Id, suggestion.ComponentId, StringComparison.Ordinal));
            if (component == null || !component.Actions.Contains("install"))
            {
                _status.Text = "This runtime cannot be installed by PaRuX on this PC.";
                return;
            }
            await InstallAsync(component);
        }

        private static string SuggestionAutomationKey(string id)
        {
            var sb = new StringBuilder(id.Length);
            foreach (char c in id) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
    }
}
