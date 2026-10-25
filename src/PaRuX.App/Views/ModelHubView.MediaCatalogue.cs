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
        private string _mediaLane = MediaHubPresenter.Standard;

        private async Task<MediaCatalogueResponse?> TryGetMediaCatalogueAsync(CancellationToken ct)
        {
            try { return await MediaCheckpointsClient.Current.GetCatalogueAsync(_mediaLane, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
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
                        + (status.IsOverridden ? " · custom selection" : " · workflow default"),
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }

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

            foreach (MediaHubCapabilityModel cap in MediaHubPresenter.Build(_mediaCatalogue, _mediaLane))
            {
                _media.Children.Add(new TextBlock
                {
                    Text = cap.Label,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 0)
                });
                _media.Children.Add(new TextBlock { Text = cap.AutomaticLine, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 });
                if (cap.EmptyMessage.Length > 0)
                    _media.Children.Add(new TextBlock { Text = cap.EmptyMessage, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
                if (cap.Best != null) _media.Children.Add(BuildMediaCatalogueCard(cap.Best));
                if (cap.Alternatives.Count > 0)
                {
                    _media.Children.Add(new TextBlock { Text = "Alternatives", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) });
                    foreach (MediaHubCard alt in cap.Alternatives) _media.Children.Add(BuildMediaCatalogueCard(alt));
                }
            }
            return true;
        }

        private UIElement BuildMediaCatalogueCard(MediaHubCard card)
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = (card.IsBest ? "Recommended · " : string.Empty) + card.Title,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock { Text = card.Subtitle, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(new TextBlock { Text = card.Reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.76 });
            if (card.Status.Length > 0)
                panel.Children.Add(new TextBlock { Text = card.Status, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 });

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
