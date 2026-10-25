using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Visual gallery and browser for generated images and video outputs.
    /// Shows thumbnails, prompt, model, timestamp, and status. Provides actions to open,
    /// reveal in folder, reuse as edit source, and delete with explicit confirmation.
    /// Outputs are never shown only as a text list.
    /// </summary>
    public sealed class MediaGalleryView : UserControl
    {
        private readonly TextBox _searchBox = new()
        {
            PlaceholderText = "Search outputs by prompt, model, title, or file name…",
            MinWidth = 280
        };
        private readonly ComboBox _filterCombo = new();
        private readonly ComboBox _sortCombo = new();
        private readonly Button _refreshButton = new() { Content = "Refresh" };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly TextBlock _emptyState = new()
        {
            Text = "No media outputs found in Artifact Workspace or generated folders.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(8, 12, 8, 12)
        };
        private readonly StackPanel _cardsPanel = new() { Spacing = 12 };
        private readonly MediaGalleryDeleteCoordinator _deleteCoordinator = new();

        private readonly List<MediaGalleryItem> _allItems = new();
        private CancellationTokenSource? _refreshCts;

        public event EventHandler<MediaGalleryItem>? ItemReusedAsEditSource;
        public event EventHandler<MediaGalleryItem>? ItemDeleted;

        public MediaGalleryView()
        {
            Content = BuildUi();
            Loaded += async (_, _) => await RefreshAsync();
            Unloaded += (_, _) => _refreshCts?.Cancel();
        }

        private UIElement BuildUi()
        {
            AutomationProperties.SetAutomationId(this, "MediaGallery_View");

            var root = new StackPanel { Spacing = 12 };

            var headerPanel = new StackPanel { Spacing = 4 };
            var title = new TextBlock
            {
                Text = "Media Gallery & Output Browsing",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            };
            AutomationProperties.SetAutomationId(title, "MediaGallery_Title");
            headerPanel.Children.Add(title);

            var subtitle = new TextBlock
            {
                Text = "Visual thumbnails and metadata for generated images and videos. Open, reveal, reuse as edit source, or delete with confirmation.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 13
            };
            headerPanel.Children.Add(subtitle);
            root.Children.Add(headerPanel);

            // Filter bar
            var filterBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 4, 0, 4)
            };

            AutomationProperties.SetAutomationId(_searchBox, "MediaGallery_Search_Box");
            _searchBox.TextChanged += (_, _) => ApplyFilterAndSort();
            filterBar.Children.Add(_searchBox);

            _filterCombo.Items.Add(new ComboBoxItem { Content = "All Media", Tag = MediaGalleryFilter.All });
            _filterCombo.Items.Add(new ComboBoxItem { Content = "Images Only", Tag = MediaGalleryFilter.ImagesOnly });
            _filterCombo.Items.Add(new ComboBoxItem { Content = "Videos Only", Tag = MediaGalleryFilter.VideosOnly });
            _filterCombo.Items.Add(new ComboBoxItem { Content = "Final Only", Tag = MediaGalleryFilter.FinalOnly });
            _filterCombo.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_filterCombo, "MediaGallery_Filter_Combo");
            AutomationProperties.SetName(_filterCombo, "Filter media");
            _filterCombo.SelectionChanged += (_, _) => ApplyFilterAndSort();
            filterBar.Children.Add(_filterCombo);

            _sortCombo.Items.Add(new ComboBoxItem { Content = "Newest First", Tag = MediaGallerySort.NewestFirst });
            _sortCombo.Items.Add(new ComboBoxItem { Content = "Oldest First", Tag = MediaGallerySort.OldestFirst });
            _sortCombo.Items.Add(new ComboBoxItem { Content = "Title (A-Z)", Tag = MediaGallerySort.TitleAscending });
            _sortCombo.Items.Add(new ComboBoxItem { Content = "File Size", Tag = MediaGallerySort.SizeDescending });
            _sortCombo.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(_sortCombo, "MediaGallery_Sort_Combo");
            AutomationProperties.SetName(_sortCombo, "Sort media");
            _sortCombo.SelectionChanged += (_, _) => ApplyFilterAndSort();
            filterBar.Children.Add(_sortCombo);

            AutomationProperties.SetAutomationId(_refreshButton, "MediaGallery_Refresh_Button");
            _refreshButton.Click += async (_, _) => await RefreshAsync();
            filterBar.Children.Add(_refreshButton);

            root.Children.Add(filterBar);

            AutomationProperties.SetAutomationId(_status, "MediaGallery_Status_Text");
            root.Children.Add(_status);

            AutomationProperties.SetAutomationId(_emptyState, "MediaGallery_EmptyState_Text");
            root.Children.Add(_emptyState);

            AutomationProperties.SetAutomationId(_cardsPanel, "MediaGallery_Cards_Panel");
            root.Children.Add(_cardsPanel);

            return root;
        }

        public async Task RefreshAsync()
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
            CancellationToken ct = _refreshCts.Token;

            try
            {
                _status.Text = "Loading media outputs…";
                List<MediaGalleryItem> discovered = await DiscoverMediaOutputsAsync(ct);
                _allItems.Clear();
                _allItems.AddRange(discovered);
                ApplyFilterAndSort();
                _status.Text = $"Gallery updated: {_allItems.Count} media output(s) available.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _status.Text = "Gallery refresh failed: " + ex.Message;
            }
        }

        public void SetItems(IEnumerable<MediaGalleryItem> items)
        {
            _allItems.Clear();
            if (items != null) _allItems.AddRange(items);
            ApplyFilterAndSort();
        }

        private async Task<List<MediaGalleryItem>> DiscoverMediaOutputsAsync(CancellationToken ct)
        {
            var results = new List<MediaGalleryItem>();
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Query Artifact Workspace for image and video artifacts
            try
            {
                ArtifactListResponse response = await ArtifactWorkspaceClient.Current.ListAsync(null, ct);
                foreach (ArtifactDto artifact in response.Artifacts.Where(a =>
                    !string.Equals(a.Status, "deleted", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(a.ArtifactType, "image", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a.ArtifactType, "video", StringComparison.OrdinalIgnoreCase))))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        ArtifactVersionListResponse versionResponse = await ArtifactWorkspaceClient.Current.ListVersionsAsync(artifact.Id, ct);
                        ArtifactVersionDto? latest = versionResponse.Versions
                            .OrderByDescending(v => v.VersionNumber)
                            .FirstOrDefault();

                        MediaGalleryItem item = MediaGalleryItemMapper.FromArtifact(artifact, latest);
                        if (!string.IsNullOrWhiteSpace(item.FilePath))
                            knownPaths.Add(Path.GetFullPath(item.FilePath));
                        results.Add(item);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        results.Add(MediaGalleryItemMapper.FromArtifact(artifact, null));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status.Text = "Artifact Workspace media query notice: " + ex.Message;
            }

            // 2. Discover files in local generated output directories that might not be registered yet
            try
            {
                AppPaths.EnsureCreated();
                ScanDirectory(AppPaths.GeneratedImagesDir, ".png", ".jpg", ".jpeg", ".webp");
                ScanDirectory(AppPaths.GeneratedVideosDir, ".mp4", ".webm", ".mov");
            }
            catch (Exception ex)
            {
                Log.Write("Media directory scan warning: " + ex.Message);
            }

            void ScanDirectory(string directory, params string[] extensions)
            {
                if (!Directory.Exists(directory)) return;
                var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(file);
                    if (!extSet.Contains(ext)) continue;

                    string fullPath = Path.GetFullPath(file);
                    if (knownPaths.Add(fullPath))
                    {
                        results.Add(MediaGalleryItemMapper.FromFile(
                            fullPath,
                            prompt: Path.GetFileNameWithoutExtension(fullPath),
                            model: "Local Media Output",
                            createdUtc: File.GetCreationTimeUtc(fullPath),
                            status: "final"));
                    }
                }
            }

            return results;
        }

        private void ApplyFilterAndSort()
        {
            var filter = MediaGalleryFilter.All;
            if (_filterCombo.SelectedItem is ComboBoxItem fItem && fItem.Tag is MediaGalleryFilter fVal)
                filter = fVal;

            var sort = MediaGallerySort.NewestFirst;
            if (_sortCombo.SelectedItem is ComboBoxItem sItem && sItem.Tag is MediaGallerySort sVal)
                sort = sVal;

            string search = _searchBox.Text ?? string.Empty;

            IReadOnlyList<MediaGalleryItem> filtered = MediaGalleryBrowser.FilterAndSort(_allItems, filter, sort, search);

            _cardsPanel.Children.Clear();
            _emptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (MediaGalleryItem item in filtered)
            {
                _cardsPanel.Children.Add(BuildCard(item));
            }
        }

        private UIElement BuildCard(MediaGalleryItem item)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.DimGray),
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 255, 255, 255))
            };
            AutomationProperties.SetAutomationId(card, $"MediaGallery_Card_{item.Id}");

            var grid = new Grid { ColumnSpacing = 14 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Thumbnail container
            var thumbBox = new Border
            {
                Width = 160,
                Height = 110,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
                Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 160, 110) }
            };

            if (item.IsImage && item.FileExists)
            {
                try
                {
                    var image = new Image
                    {
                        Source = new BitmapImage(new Uri(item.FilePath)),
                        Stretch = Stretch.UniformToFill
                    };
                    thumbBox.Child = image;
                }
                catch
                {
                    thumbBox.Child = new TextBlock
                    {
                        Text = "🖼 IMAGE",
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Opacity = 0.7
                    };
                }
            }
            else if (item.IsVideo)
            {
                var videoStack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 4
                };
                videoStack.Children.Add(new TextBlock
                {
                    Text = "🎬 VIDEO",
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                if (!string.IsNullOrWhiteSpace(item.FormattedDimensions))
                {
                    videoStack.Children.Add(new TextBlock
                    {
                        Text = item.FormattedDimensions,
                        FontSize = 11,
                        Opacity = 0.75,
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                }
                thumbBox.Child = videoStack;
            }
            else
            {
                thumbBox.Child = new TextBlock
                {
                    Text = item.ArtifactType.ToUpperInvariant(),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Opacity = 0.7
                };
            }
            Grid.SetColumn(thumbBox, 0);
            grid.Children.Add(thumbBox);

            // Metadata & Actions
            var contentPanel = new StackPanel { Spacing = 4 };

            var promptText = new TextBlock
            {
                Text = item.Prompt,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTipService.SetToolTip(promptText, item.Prompt);
            AutomationProperties.SetAutomationId(promptText, $"MediaGallery_Prompt_{item.Id}");
            contentPanel.Children.Add(promptText);

            var detailsText = new TextBlock
            {
                Text = item.FormattedDetails,
                FontSize = 12,
                Opacity = 0.75
            };
            AutomationProperties.SetAutomationId(detailsText, $"MediaGallery_Details_{item.Id}");
            contentPanel.Children.Add(detailsText);

            var pathText = new TextBlock
            {
                Text = item.FilePath,
                FontSize = 11,
                Opacity = 0.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTipService.SetToolTip(pathText, item.FilePath);
            AutomationProperties.SetAutomationId(pathText, $"MediaGallery_Path_{item.Id}");
            contentPanel.Children.Add(pathText);

            // Action Buttons
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var openBtn = new Button
            {
                Content = "Open",
                IsEnabled = MediaGalleryBrowser.CanOpen(item)
            };
            AutomationProperties.SetAutomationId(openBtn, $"MediaGallery_Open_{item.Id}");
            openBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                    _status.Text = $"Opened {item.Title}.";
                }
                catch (Exception ex)
                {
                    _status.Text = "Failed to open output: " + ex.Message;
                }
            };
            actions.Children.Add(openBtn);

            var revealBtn = new Button
            {
                Content = "Reveal in folder",
                IsEnabled = MediaGalleryBrowser.CanReveal(item)
            };
            AutomationProperties.SetAutomationId(revealBtn, $"MediaGallery_Reveal_{item.Id}");
            revealBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                    _status.Text = $"Revealed {item.Title} in Explorer.";
                }
                catch (Exception ex)
                {
                    _status.Text = "Failed to reveal in folder: " + ex.Message;
                }
            };
            actions.Children.Add(revealBtn);

            var reuseBtn = new Button
            {
                Content = "Reuse as edit source",
                IsEnabled = MediaGalleryBrowser.CanReuseAsEditSource(item)
            };
            AutomationProperties.SetAutomationId(reuseBtn, $"MediaGallery_ReuseEdit_{item.Id}");
            reuseBtn.Click += (_, _) =>
            {
                SetEditSourceResult result = MediaEditSourceBridge.Current.SetEditSource(item.FilePath, item.Prompt);
                _status.Text = result.Message;
                if (result.Success)
                    ItemReusedAsEditSource?.Invoke(this, item);
            };
            actions.Children.Add(reuseBtn);

            var deleteBtn = new Button
            {
                Content = "Delete",
                IsEnabled = MediaGalleryBrowser.CanDelete(item)
            };
            AutomationProperties.SetAutomationId(deleteBtn, $"MediaGallery_Delete_{item.Id}");
            deleteBtn.Click += async (_, _) => await HandleDeleteWithConfirmationAsync(item);
            actions.Children.Add(deleteBtn);

            contentPanel.Children.Add(actions);

            Grid.SetColumn(contentPanel, 1);
            grid.Children.Add(contentPanel);

            card.Child = grid;
            return card;
        }

        private async Task HandleDeleteWithConfirmationAsync(MediaGalleryItem item)
        {
            _deleteCoordinator.RequestDelete(item.Id, item.Title, item.FilePath);

            var confirmDialog = new ContentDialog
            {
                Title = "Delete Media Output",
                Content = $"Are you sure you want to permanently delete '{item.Title}'?\n\nPath: {item.FilePath}\n\nThis action cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            ContentDialogResult result = await confirmDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                _deleteCoordinator.CancelDelete(item.Id);
                _status.Text = "Deletion cancelled.";
                return;
            }

            DeleteConfirmationResult delResult = _deleteCoordinator.ConfirmDelete(item.Id, path =>
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
                return false;
            });

            if (delResult.Success)
            {
                if (!string.IsNullOrWhiteSpace(item.ArtifactId))
                {
                    try
                    {
                        await ArtifactWorkspaceClient.Current.SetStatusAsync(item.ArtifactId, "deleted", CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Artifact status set to deleted notice: " + ex.Message);
                    }
                }

                _allItems.RemoveAll(i => i.Id == item.Id);
                ApplyFilterAndSort();
                _status.Text = $"Permanently deleted media output '{item.Title}'.";
                ItemDeleted?.Invoke(this, item);
            }
            else
            {
                _status.Text = "Delete failed: " + (delResult.Error ?? "unknown error");
            }
        }
    }
}
