using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private bool _modelSelectorLoaded;
        private bool _suppressModelSelectorEvents;
        private bool _modelRoutedSubscribed;
        private ChatModelRouteInfo? _lastChatModelRoute;

        internal void EnsureModelRoutingSubscribed()
        {
            if (_modelRoutedSubscribed) return;
            _modelRoutedSubscribed = true;
            ChatClient.Current.ModelRouted += OnChatModelRouted;
        }

        private async void ModelSelector_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureModelRoutingSubscribed();
            if (_modelSelectorLoaded) return;
            _modelSelectorLoaded = true;
            InitializeNativeCodingHandoffUpgrade();
            await LoadModelSelectorAsync();
        }

        private void OnChatModelRouted(ChatModelRouteInfo route)
        {
            _dispatcher.TryEnqueue(() =>
            {
                _lastChatModelRoute = route;
                RefreshSimpleModeAnsweringModel();
                string profile = route.ManualLock
                    ? "Manual lock"
                    : "Auto · " + PrettyAutoProfile(route.AutoProfile);
                string capability = route.SelectedIntelligence.HasValue
                    ? " · fit estimate " + (route.SelectedIntelligence.Value * 100).ToString("0") + "%"
                    : string.Empty;
                string speed = route.TokensPerSecond.HasValue
                    ? " · bench " + route.TokensPerSecond.Value.ToString("0.0") + " tok/s"
                    : string.Empty;
                string selected = ChatModelRouteInfo.SanitizeModelName(route.SelectedModel);
                if (string.IsNullOrWhiteSpace(selected)) selected = "configured model";
                ShowTransientStatus(selected + " · " + profile + capability + speed);
                ToolTipService.SetToolTip(ModelSelector,
                    string.IsNullOrWhiteSpace(route.Reason)
                        ? "Last routed turn: " + selected + " · " + profile
                        : "Last routed turn: " + selected + " · " + profile + "\n\n" + route.Reason);
            });
        }

        private async void ModelRouteExplanationButton_Click(object sender, RoutedEventArgs e)
        {
            ChatModelRouteInfo? route = _lastChatModelRoute;
            if (route == null)
            {
                ChatMessage? lastAssistant = ChatSession.Current.Conversation?.Messages
                    .LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.ModelId));
                if (lastAssistant != null)
                {
                    route = new ChatModelRouteInfo
                    {
                        SelectedModel = lastAssistant.ModelId!,
                        Reason = lastAssistant.FallbackReason ?? string.Empty
                    };
                }
            }

            var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
            foreach (ChatModelRouteExplanationLine line in ChatModelRouteExplanationBuilder.Build(route))
            {
                var label = new TextBlock
                {
                    Text = line.Label,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                };
                var value = new TextBlock
                {
                    Text = line.Value,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, line.Value);
                if (line.Label == "No route yet")
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(value, "Chat_ModelRoute_Explain_EmptyState_Text");
                content.Children.Add(label);
                content.Children.Add(value);
            }

            var dialog = new ContentDialog
            {
                Title = "Why this model?",
                Content = new ScrollViewer { Content = content, MaxHeight = 440 },
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }

        private static string PrettyAutoProfile(string profile) => profile switch
        {
            AutoIntelligenceProfile.MaxIntelligence => "Max intelligence",
            AutoIntelligenceProfile.Responsive => "Responsive",
            AutoIntelligenceProfile.Custom => "Custom",
            _ => "Balanced"
        };

        private async Task LoadModelSelectorAsync()
        {
            _suppressModelSelectorEvents = true;
            try
            {
                string selectedSelector = NormalizeSavedModelSelection(SettingsStore.Current.Data.ChatModelSelection);
                bool routingUnreadable = false;
                List<ManagedModelInfo> models;
                try
                {
                    ChatRoutingPreferenceDto corePreference =
                        await ModelRoutingClient.Current.GetChatPreferenceAsync(CancellationToken.None);
                    if (corePreference.Unreadable) routingUnreadable = true;
                    else
                    {
                        selectedSelector = NormalizeSavedModelSelection(corePreference.Selector);
                        SettingsStore.Current.Data.ChatModelSelection = selectedSelector;
                        SettingsStore.Current.Save();
                    }
                }
                catch (Exception ex) { Log.Write("Chat routing preference could not be read from Core: " + ex.Message); }

                try { models = await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None); }
                catch (Exception ex)
                {
                    Log.Write("Chat model selector could not load managed models: " + ex.Message);
                    models = new List<ManagedModelInfo>();
                }

                ModelSelector.Items.Clear();
                AddModelChoice("Auto · Balanced", "parux-auto:balanced");
                AddModelChoice("Auto · Max intelligence", "parux-auto:max_intelligence");
                AddModelChoice("Auto · Responsive", "parux-auto:responsive");
                AddModelChoice("Auto · Custom…", "parux-auto:custom");
                foreach (ManagedModelInfo model in models.OrderBy(
                    m => string.IsNullOrWhiteSpace(m.Alias) ? m.FileName : m.Alias + " · " + m.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    string display = string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias + " · " + model.FileName;
                    AddModelChoice("Manual · " + display, "parux-manual:" + model.FileName);
                }

                ComboBoxItem? selected = ModelSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    string.Equals(item.Tag as string, selectedSelector, StringComparison.OrdinalIgnoreCase));
                if (selected == null && selectedSelector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase))
                {
                    string missing = selectedSelector.Substring("parux-manual:".Length);
                    selected = new ComboBoxItem
                    {
                        Content = "Manual · " + missing + " (missing)", Tag = selectedSelector, IsEnabled = false
                    };
                    ModelSelector.Items.Add(selected);
                }
                if (selected == null)
                {
                    selected = ModelSelector.Items.OfType<ComboBoxItem>().First();
                    SettingsStore.Current.Data.ChatModelSelection = selected.Tag as string ?? string.Empty;
                    SettingsStore.Current.Save();
                }
                ModelSelector.SelectedItem = selected;
                if (routingUnreadable)
                {
                    // Never present a corrupt saved preference as "Auto": say it is unreadable. Choosing any
                    // entry re-saves a valid preference.
                    var unreadable = new ComboBoxItem
                    {
                        Content = "Routing preference unreadable (re-select to repair)", Tag = selected.Tag, IsEnabled = false
                    };
                    ModelSelector.Items.Add(unreadable);
                    ModelSelector.SelectedItem = unreadable;
                }
            }
            finally { _suppressModelSelectorEvents = false; }
        }

        private void AddModelChoice(string label, string selector) =>
            ModelSelector.Items.Add(new ComboBoxItem { Content = label, Tag = selector });

        private async void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressModelSelectorEvents) return;
            if (ModelSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string selector) return;
            selector = NormalizeSavedModelSelection(selector);
            try
            {
                if (selector == "parux-auto:custom")
                {
                    bool configured = await ConfigureCustomAutoAsync();
                    if (!configured) { await LoadModelSelectorAsync(); return; }
                }
                ChatRoutingPreferenceDto persisted = await ModelRoutingClient.Current.SetChatPreferenceAsync(
                    RequestForSelector(selector), CancellationToken.None);
                string canonical = NormalizeSavedModelSelection(persisted.Selector);
                SettingsStore.Current.Data.ChatModelSelection = canonical;
                SettingsStore.Current.Save();
            }
            catch (Exception ex)
            {
                Log.Write("Chat model selection could not be persisted in Core: " + ex.Message);
                UserFailureGuidance guidance = UserFailureRecoveryMapper.MapModelSelector(ex);
                ShowTransientStatus(guidance.FormatStatusLine());
                await LoadModelSelectorAsync();
            }
        }

        private async Task<bool> ConfigureCustomAutoAsync()
        {
            AutoRoutingTuningDto tuning;
            try { tuning = await ModelRoutingClient.Current.GetAutoTuningAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Write("Could not load Custom AUTO tuning: " + ex.Message); return false; }

            NumberBox minFloor = AutoNumber("Minimum relative capability floor", tuning.MinimumCapabilityRatio, 0.35, 0.98, 0.01);
            NumberBox floorBoost = AutoNumber("Complexity → capability-floor boost", tuning.ComplexityFloorBoost, 0, 0.50, 0.01);
            NumberBox capability = AutoNumber("Base capability weight", tuning.BaseCapabilityWeight, 0.05, 0.90, 0.01);
            NumberBox performance = AutoNumber("Base performance / tok-s weight", tuning.BasePerformanceWeight, 0, 0.80, 0.01);
            NumberBox compatibility = AutoNumber("Hardware compatibility weight", tuning.CompatibilityWeight, 0.05, 0.40, 0.01);
            NumberBox reuse = AutoNumber("Already-loaded model reuse weight", tuning.LoadedReuseWeight, 0, 0.30, 0.01);
            NumberBox capabilityBoost = AutoNumber("Complexity → capability-weight boost", tuning.ComplexityCapabilityBoost, 0, 0.50, 0.01);
            NumberBox performancePenalty = AutoNumber("Complexity → performance-weight reduction", tuning.ComplexityPerformancePenalty, 0, 0.50, 0.01);

            var stack = new StackPanel { Spacing = 10, MaxWidth = 560 };
            stack.Children.Add(new TextBlock
            {
                Text = "Custom AUTO still estimates task difficulty first. These controls decide how aggressively PaRuX escalates toward stronger models versus keeping a sufficiently capable faster model. Hardware safety and exact Manual locks cannot be bypassed.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });
            foreach (NumberBox box in new[] { minFloor, floorBoost, capability, performance, compatibility, reuse, capabilityBoost, performancePenalty })
                stack.Children.Add(box);
            var reset = new Button { Content = "Reset fields to Balanced-like defaults", HorizontalAlignment = HorizontalAlignment.Left };
            reset.Click += (_, _) =>
            {
                var d = new AutoRoutingTuningDto();
                minFloor.Value = d.MinimumCapabilityRatio; floorBoost.Value = d.ComplexityFloorBoost;
                capability.Value = d.BaseCapabilityWeight; performance.Value = d.BasePerformanceWeight;
                compatibility.Value = d.CompatibilityWeight; reuse.Value = d.LoadedReuseWeight;
                capabilityBoost.Value = d.ComplexityCapabilityBoost; performancePenalty.Value = d.ComplexityPerformancePenalty;
            };
            stack.Children.Add(reset);

            var dialog = new ContentDialog
            {
                Title = "Auto · Custom intelligence",
                Content = new ScrollViewer { Content = stack, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "Save & use Custom",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            await ModelRoutingClient.Current.SetAutoTuningAsync(new SetAutoRoutingTuningRequest
            {
                MinimumCapabilityRatio = minFloor.Value,
                ComplexityFloorBoost = floorBoost.Value,
                BaseCapabilityWeight = capability.Value,
                BasePerformanceWeight = performance.Value,
                CompatibilityWeight = compatibility.Value,
                LoadedReuseWeight = reuse.Value,
                ComplexityCapabilityBoost = capabilityBoost.Value,
                ComplexityPerformancePenalty = performancePenalty.Value
            }, CancellationToken.None);
            return true;
        }

        private static NumberBox AutoNumber(string header, double value, double min, double max, double step) => new()
        {
            Header = header, Value = value, Minimum = min, Maximum = max, SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        private static SetChatRoutingPreferenceRequest RequestForSelector(string selector)
        {
            if (selector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase))
                return new SetChatRoutingPreferenceRequest
                {
                    Mode = RouteMode.Manual,
                    ManualModelFileName = selector.Substring("parux-manual:".Length)
                };
            string profile = selector.StartsWith("parux-auto:", StringComparison.OrdinalIgnoreCase)
                ? selector.Substring("parux-auto:".Length) : AutoIntelligenceProfile.Balanced;
            return new SetChatRoutingPreferenceRequest { Mode = RouteMode.Auto, AutoProfile = profile };
        }

        private static string NormalizeSavedModelSelection(string value)
        {
            string selector = (value ?? string.Empty).Trim();
            if (selector.Equals("parux-auto:responsive", StringComparison.OrdinalIgnoreCase)) return "parux-auto:responsive";
            if (selector.Equals("parux-auto:max_intelligence", StringComparison.OrdinalIgnoreCase)) return "parux-auto:max_intelligence";
            if (selector.Equals("parux-auto:custom", StringComparison.OrdinalIgnoreCase)) return "parux-auto:custom";
            if (selector.StartsWith("parux-manual:", StringComparison.OrdinalIgnoreCase)
                && selector.Length > "parux-manual:".Length)
                return "parux-manual:" + selector.Substring("parux-manual:".Length);
            return "parux-auto:balanced";
        }
    }
}
