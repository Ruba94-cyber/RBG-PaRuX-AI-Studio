using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Media catalogue part of the Model Hub: best-fit model per capability, Standard / Uncensored lanes, and
    /// downloads through the existing consent, revision-pinned and hash-verified lane. Kept apart from the rest of the view.</summary>
    public sealed partial class ModelHubView
    {
        private MediaCatalogueResponse? _mediaCatalogue;
        private (string CheckpointFamilyName, List<InstalledLoraChoice> Choices)? _mediaLoras;
        private readonly StackPanel _mediaLoraList = new StackPanel { Spacing = 2, Margin = new Thickness(0, 12, 0, 0) };
        private string _mediaLane = MediaHubPresenter.Standard;

        private bool _mediaLaneInitialised;
        private string _globalMediaLane = "Standard";

        private async Task<MediaCatalogueResponse?> TryGetMediaCatalogueAsync(CancellationToken ct)
        {
            try { _mediaLoras = await MediaCheckpointsClient.Current.GetInstalledLorasAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _mediaLoras = null; }
            try
            {
                // The global content lane decides which group opens first (once; the user's own tab clicks win afterwards).
                MediaLaneStatus lane = await MediaCheckpointsClient.Current.GetLaneAsync(ct);
                _globalMediaLane = lane.Lane;
                if (!_mediaLaneInitialised) { _mediaLane = MediaHubPresenter.InitialTabFor(lane.Lane); _mediaLaneInitialised = true; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            try { return await MediaCheckpointsClient.Current.GetCatalogueAsync(_mediaLane, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        /// <summary>Installed LoRAs under the media catalogue, each marked against the active image model's family.</summary>
        private void RenderInstalledLoras()
        {
            _mediaLoraList.Children.Clear();
            AutomationProperties.SetAutomationId(_mediaLoraList, "ModelHub_Media_InstalledLoras");
            if (_mediaLoras is not { } loras)
            {
                _mediaLoraList.Children.Add(new TextBlock { Text = "Installed LoRAs could not be read from PaRuX Core.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                _media.Children.Add(_mediaLoraList);
                return;
            }
            _mediaLoraList.Children.Add(new TextBlock
            {
                Text = CreateLoraStackPresenter.HubHeader(loras.Choices.Count, loras.CheckpointFamilyName),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            foreach (InstalledLoraChoice choice in loras.Choices)
                _mediaLoraList.Children.Add(new TextBlock { Text = CreateLoraStackPresenter.HubLine(choice), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8 });
            _mediaLoraList.Children.Add(new TextBlock
            {
                Text = "Pick up to " + CreateLoraStackPresenter.MaxSlots + " of these in Create > Image. A LoRA only works with the model family it was made for.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.65
            });
            _media.Children.Add(_mediaLoraList);
        }

        /// <summary>Renders the catalogue UI into the media panel. False when the catalogue is unavailable, so the
        /// older same-family list is shown instead of nothing.</summary>
        private bool RenderMediaCatalogue(IReadOnlyList<MediaCheckpointStatus> current)
        {
            if (_mediaCatalogue == null) return false;
            _media.Children.Clear();
            _media.Children.Add(new TextBlock
            {
                Text = "Best model per capability for this PC (" + _mediaCatalogue.HardwareSummary + "). Fit is an estimate and the tier is declared by the catalogue, not measured. Models PaRuX cannot run yet are listed but cannot be downloaded.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });
            foreach (MediaCheckpointStatus status in current.OrderBy(s => s.Type, StringComparer.OrdinalIgnoreCase))
            {
                string label = status.Type switch { "image" => "Image", "video" => "Video", "edit" => "Image Edit", _ => status.Type };
                _media.Children.Add(new TextBlock
                {
                    Text = label + " · active: " + (string.IsNullOrWhiteSpace(status.CurrentFile) ? "not configured" : status.CurrentFile)
                        + " · " + MediaSelectionPresenter.SummarySource(status),
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }

            // Discovery: the user decides when to look on Hugging Face; this only lists models and never downloads one.
            var discoveryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
            var refresh = new Button { Content = "Refresh from Hugging Face" };
            AutomationProperties.SetName(refresh, "Refresh media models from Hugging Face");
            AutomationProperties.SetAutomationId(refresh, "ModelHub_MediaDiscoveryRefresh");
            var refreshLine = new TextBlock
            {
                Text = MediaHubPresenter.DiscoveryStatusLine(_mediaCatalogue), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(refreshLine, "ModelHub_MediaDiscoveryStatus");
            refresh.Click += async (_, _) => await RefreshMediaDiscoveryAsync(refresh, refreshLine);
            discoveryRow.Children.Add(refresh);
            discoveryRow.Children.Add(refreshLine);
            _media.Children.Add(discoveryRow);

            var laneRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            foreach (string lane in new[] { MediaHubPresenter.Standard, MediaHubPresenter.Uncensored })
            {
                string captured = lane;
                var tab = new Button
                {
                    Content = MediaHubPresenter.LaneLabel(lane),
                    FontWeight = lane == _mediaLane ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    Opacity = lane == _mediaLane ? 1.0 : 0.65
                };
                AutomationProperties.SetName(tab, "Media lane " + MediaHubPresenter.LaneLabel(lane));
                AutomationProperties.SetAutomationId(tab, "ModelHub_MediaLane_" + MediaHubPresenter.LaneLabel(lane));
                tab.Click += (_, _) => { _mediaLane = captured; RenderMediaCatalogue(current); };
                laneRow.Children.Add(tab);
            }
            _media.Children.Add(laneRow);
            if (_mediaLane == MediaHubPresenter.Standard)
            {
                string pointer = MediaHubPresenter.UncensoredPointer(_mediaCatalogue, _globalMediaLane);
                if (pointer.Length > 0)
                {
                    var pointerLine = new TextBlock { Text = pointer, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 };
                    AutomationProperties.SetAutomationId(pointerLine, "ModelHub_UncensoredPointer");
                    _media.Children.Add(pointerLine);
                }
            }

            foreach (MediaHubCapabilityModel cap in MediaHubPresenter.Build(_mediaCatalogue, _mediaLane))
            {
                _media.Children.Add(new TextBlock
                {
                    Text = cap.Label,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 0)
                });
                _media.Children.Add(new TextBlock { Text = cap.AutomaticLine, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 });
                string? capType = MediaSelectionPresenter.TypeFor(cap.CapabilityId);
                MediaCheckpointStatus? capCurrent = capType == null ? null
                    : current.FirstOrDefault(s => string.Equals(s.Type, capType, StringComparison.OrdinalIgnoreCase));
                if (capType != null) _media.Children.Add(BuildMediaSelectionHeader(capType, capCurrent));
                if (cap.EmptyMessage.Length > 0)
                    _media.Children.Add(new TextBlock { Text = cap.EmptyMessage, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
                if (cap.Best != null) _media.Children.Add(BuildMediaCatalogueCard(cap.Best, capCurrent));
                if (cap.Alternatives.Count > 0)
                {
                    _media.Children.Add(new TextBlock { Text = "Alternatives", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) });
                    foreach (MediaHubCard alt in cap.Alternatives) _media.Children.Add(BuildMediaCatalogueCard(alt, capCurrent));
                }
                _media.Children.Add(new TextBlock
                {
                    Text = "Found on Hugging Face (" + MediaHubPresenter.DiscoveredNote + ")", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0)
                });
                if (cap.DiscoveredCards.Count == 0)
                    _media.Children.Add(new TextBlock { Text = cap.DiscoveredMessage, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 });
                if (cap.DiscoveredCards.Count > 0) _media.Children.Add(BuildDiscoveredWindow(cap, capCurrent));
            }
            RenderInstalledLoras();
            return true;
        }

        private readonly Dictionary<string, int> _discoveredLimits = new(StringComparer.Ordinal);

        /// <summary>Discovered candidates are windowed (default 6, best first, installed always shown) and cards are built
        /// lazily: only the visible ones exist, so ~700 candidates no longer make the page slow to scroll.</summary>
        private UIElement BuildDiscoveredWindow(MediaHubCapabilityModel cap, MediaCheckpointStatus? capCurrent)
        {
            string key = cap.CapabilityId + "_" + _mediaLane;
            var host = new StackPanel { Spacing = 0 };
            void Rebuild()
            {
                host.Children.Clear();
                int limit = _discoveredLimits.TryGetValue(key, out int l) ? l : DiscoveredListWindow.DefaultLimit;
                DiscoveredWindowResult<MediaHubCard> w = DiscoveredListWindow.Compute(cap.DiscoveredCards, c => c.Action == MediaHubAction.Installed, limit);
                foreach (MediaHubCard found in w.Visible) host.Children.Add(BuildMediaCatalogueCard(found, capCurrent));
                if (!w.CanShowMore && !w.CanShowFewer) return;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
                if (w.CanShowMore)
                {
                    var more = new Button { Content = w.ShowMoreLabel };
                    AutomationProperties.SetName(more, "Show more discovered " + cap.Label + " models");
                    AutomationProperties.SetAutomationId(more, "ModelHub_Discovered_ShowMore_" + cap.CapabilityId + "_" + _mediaLane);
                    more.Click += (_, _) => { _discoveredLimits[key] = DiscoveredListWindow.Expand(limit); Rebuild(); };
                    row.Children.Add(more);
                }
                if (w.CanShowFewer)
                {
                    var fewer = new Button { Content = w.ShowFewerLabel };
                    AutomationProperties.SetName(fewer, "Show fewer discovered " + cap.Label + " models");
                    AutomationProperties.SetAutomationId(fewer, "ModelHub_Discovered_ShowFewer_" + cap.CapabilityId + "_" + _mediaLane);
                    fewer.Click += (_, _) => { _discoveredLimits[key] = DiscoveredListWindow.Collapse(limit); Rebuild(); };
                    row.Children.Add(fewer);
                }
                host.Children.Add(row);
            }
            Rebuild();
            return host;
        }

        private UIElement BuildMediaCatalogueCard(MediaHubCard card, MediaCheckpointStatus? current)
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = (card.Badge.Length > 0 ? card.Badge + " · " : card.IsBest ? "Recommended · " : string.Empty) + card.Title,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock { Text = card.Subtitle, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(new TextBlock { Text = card.Reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.76 });
            if (card.Status.Length > 0)
                panel.Children.Add(new TextBlock { Text = card.Status, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 });

            MediaSelectionModel selection = MediaSelectionPresenter.ForCard(card.Entry, current);
            if (selection.ShowUse)
            {
                if (selection.ActiveLine.Length > 0)
                    panel.Children.Add(new TextBlock { Text = selection.ActiveLine, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var use = new Button { Content = selection.UseLabel, IsEnabled = selection.UseEnabled };
                AutomationProperties.SetName(use, "Use " + card.Title);
                AutomationProperties.SetAutomationId(use, "ModelHub_UseModel_" + card.VariantId);
                string useType = MediaSelectionPresenter.TypeFor(card.Entry.CapabilityId)!;
                string useFile = card.Entry.FileName;
                use.Click += async (_, _) => await UseMediaCheckpointAsync(useType, useFile, card.Title, use);
                panel.Children.Add(use);
            }

            RecommendedCheckpoint? download = MediaHubPresenter.ToDownload(card.Entry);
            if (card.Action == MediaHubAction.Download && download != null)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var get = new Button { Content = "1. " + card.ActionLabel };
                AutomationProperties.SetName(get, "Download " + card.Title);
                get.Click += async (_, _) => await DownloadMediaCheckpointAsync(download, get);
                var install = new Button { Content = "2. Install for automatic use" };
                AutomationProperties.SetName(install, "Install " + card.Title + " for automatic use");
                install.Click += async (_, _) => await InstallMediaCheckpointForAutomaticAsync(download);
                buttons.Children.Add(get);
                buttons.Children.Add(install);
                panel.Children.Add(buttons);
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = card.ActionLabel, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            }
            return panel;
        }

        /// <summary>Per capability: which model is active and the "Automatic" choice that clears the pinned override.</summary>
        private UIElement BuildMediaSelectionHeader(string type, MediaCheckpointStatus? current)
        {
            (string activeLine, bool automaticEnabled, string automaticLabel) = MediaSelectionPresenter.ForCapability(current);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var line = new TextBlock { Text = activeLine, TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            AutomationProperties.SetAutomationId(line, "ModelHub_ActiveModel_" + type);
            var automatic = new Button { Content = automaticLabel, IsEnabled = automaticEnabled };
            AutomationProperties.SetName(automatic, "Use Automatic model for " + type);
            AutomationProperties.SetAutomationId(automatic, "ModelHub_Automatic_" + type);
            automatic.Click += async (_, _) =>
            {
                automatic.IsEnabled = false;
                try
                {
                    await MediaCheckpointsClient.Current.ResetCheckpointAsync(type, CancellationToken.None);
                    _status.Text = "Automatic mode restored for " + type + ".";
                    await RefreshAllAsync();
                }
                catch (Exception ex)
                {
                    _status.Text = "Could not restore Automatic: " + CustomCheckpointPresenter.ExtractServerError(ex.Message);
                    automatic.IsEnabled = true;
                }
            };
            row.Children.Add(automatic);
            row.Children.Add(line);
            return row;
        }

        private async Task UseMediaCheckpointAsync(string type, string fileName, string title, Button button)
        {
            button.IsEnabled = false;
            try
            {
                await MediaCheckpointsClient.Current.SetCheckpointAsync(type, fileName, CancellationToken.None);
                _status.Text = title + " is now used for " + type + ".";
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                // The server refuses a family the workflow cannot run with a typed reason; show it as is.
                _status.Text = "Could not use " + title + ": " + CustomCheckpointPresenter.ExtractServerError(ex.Message);
                button.IsEnabled = true;
            }
        }

        private async Task RefreshMediaDiscoveryAsync(Button button, TextBlock statusLine)
        {
            button.IsEnabled = false;
            statusLine.Text = "Reading Hugging Face (public listings only, this can take a minute)…";
            try
            {
                MediaDiscoveryRefreshResultDto result = await MediaCheckpointsClient.Current.RefreshDiscoveryAsync(CancellationToken.None);
                _status.Text = MediaHubPresenter.RefreshResultLine(result);
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "Could not refresh from Hugging Face: " + CustomCheckpointPresenter.ExtractServerError(ex.Message);
                statusLine.Text = _status.Text;
                button.IsEnabled = true;
            }
        }

        private async Task InstallMediaCheckpointForAutomaticAsync(RecommendedCheckpoint checkpoint)
        {
            try
            {
                SetBusy(true, "Installing " + checkpoint.Title + "…");
                await MediaCheckpointsClient.Current.InstallForAutomaticAsync(checkpoint.Type, checkpoint.FileName, CancellationToken.None);
                _status.Text = checkpoint.Title + " is installed. Automatic mode for " + checkpoint.CapabilityId + " can now choose it.";
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "Install failed. Download the checkpoint completely first. " + ex.Message;
                SetBusy(false, _status.Text);
            }
        }
    }
}
