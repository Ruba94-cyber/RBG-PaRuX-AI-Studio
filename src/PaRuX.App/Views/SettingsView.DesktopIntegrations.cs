using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// The "Apps on this PC" settings card, built in code because it is a list whose rows depend on
    /// what is actually installed - a fixed XAML list of every supported app would show a user
    /// entries for four things they do not have.
    ///
    /// Each row leads with what PaRuX would read, in the app's own terms, <em>before</em> the button
    /// that connects it. Consent given after reading "Markdown notes in your vault, including
    /// sub-folders" is consent; consent given to "Connect Obsidian?" is a guess.
    /// </summary>
    public sealed partial class SettingsView
    {
        private async Task RefreshDesktopIntegrationsAsync()
        {
            if (DesktopIntegrationsList == null) return;
            try
            {
                List<DesktopIntegrationStatus> statuses =
                    await DesktopIntegrationClient.Current.GetStatusesAsync(CancellationToken.None);
                RenderDesktopIntegrations(statuses);
            }
            catch (Exception ex)
            {
                Log.Write("RefreshDesktopIntegrationsAsync failed: " + ex.Message);
                if (DesktopIntegrationsStatusText != null)
                    DesktopIntegrationsStatusText.Text = "Couldn't read integration status: " + ex.Message;
            }
        }

        private void RenderDesktopIntegrations(List<DesktopIntegrationStatus> statuses)
        {
            if (DesktopIntegrationsList == null) return;
            DesktopIntegrationsList.Children.Clear();

            int connected = statuses.Count(s => s.Connected);
            int detected = statuses.Count(s => s.Detected && !s.Connected);
            if (DesktopIntegrationsStatusText != null)
            {
                DesktopIntegrationsStatusText.Text = connected == 0
                    ? (detected == 0
                        ? "Nothing connected. Choose a folder below to connect an app."
                        : detected + " app" + (detected == 1 ? "" : "s") + " found on this PC and not connected.")
                    : connected + " connected" + (detected > 0 ? ", " + detected + " more found on this PC." : ".");
            }

            // Connected first, then what was found, then the rest: the list should open on what the
            // user has already decided rather than on a catalogue.
            foreach (DesktopIntegrationStatus status in statuses
                .OrderByDescending(s => s.Connected)
                .ThenByDescending(s => s.Detected)
                .ThenBy(s => s.Kind, StringComparer.Ordinal)
                .ThenBy(s => s.DisplayName, StringComparer.Ordinal))
            {
                DesktopIntegrationsList.Children.Add(BuildIntegrationRow(status));
            }
        }

        private Border BuildIntegrationRow(DesktopIntegrationStatus status)
        {
            var panel = new StackPanel { Spacing = 4 };

            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new TextBlock
            {
                Text = status.DisplayName,
                Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"],
                VerticalAlignment = VerticalAlignment.Center
            });
            heading.Children.Add(new TextBlock
            {
                Text = status.Connected ? "Connected" : status.Detected ? "Found on this PC" : status.Kind,
                Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"],
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(heading);

            // What it reads, before the button that turns it on.
            panel.Children.Add(new TextBlock
            {
                Text = status.WhatItReads,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
            });

            if (status.Connected && !string.IsNullOrWhiteSpace(status.ConnectedPath))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Reading: " + status.ConnectedPath
                        + (status.LastReadUtc.HasValue
                            ? "  ·  " + status.LastItemCount + " items at "
                              + status.LastReadUtc.Value.ToLocalTime().ToString("g")
                            : string.Empty),
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                });
            }
            else if (status.Detected && !string.IsNullOrWhiteSpace(status.DetectedPath))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Found at: " + status.DetectedPath,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                });
            }

            if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Last read failed: " + status.LastError,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                });
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0)
            };
            string id = status.Id;

            if (status.Connected)
            {
                var preview = new Button { Content = "Show me what it reads" };
                preview.Click += async (_, _) => await PreviewDesktopIntegrationAsync(id, status.DisplayName);
                buttons.Children.Add(preview);

                var disconnect = new Button { Content = "Disconnect" };
                disconnect.Click += async (_, _) => await DisconnectDesktopIntegrationAsync(id);
                buttons.Children.Add(disconnect);
            }
            else
            {
                if (status.Detected && !status.RequiresExplicitPath)
                {
                    var connect = new Button
                    {
                        Content = "Connect",
                        Style = (Style)Application.Current.Resources["AccentButtonStyle"]
                    };
                    connect.Click += async (_, _) => await ConnectDesktopIntegrationAsync(id, null);
                    buttons.Children.Add(connect);
                }

                var choose = new Button { Content = status.Detected ? "Choose another folder…" : "Choose folder…" };
                choose.Click += async (_, _) => await ChooseDesktopIntegrationPathAsync(id, status.Kind);
                buttons.Children.Add(choose);
            }

            panel.Children.Add(buttons);

            return new Border
            {
                Style = (Style)Application.Current.Resources["PaRuXCardStyle"],
                Child = panel
            };
        }

        private async Task ConnectDesktopIntegrationAsync(string id, string? path)
        {
            try
            {
                await DesktopIntegrationClient.Current.ConnectAsync(id, path, CancellationToken.None);
                await RefreshDesktopIntegrationsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("ConnectDesktopIntegrationAsync failed: " + ex.Message);
                if (DesktopIntegrationsStatusText != null) DesktopIntegrationsStatusText.Text = ex.Message;
            }
        }

        private async Task DisconnectDesktopIntegrationAsync(string id)
        {
            try
            {
                await DesktopIntegrationClient.Current.DisconnectAsync(id, CancellationToken.None);
                await RefreshDesktopIntegrationsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("DisconnectDesktopIntegrationAsync failed: " + ex.Message);
                if (DesktopIntegrationsStatusText != null) DesktopIntegrationsStatusText.Text = ex.Message;
            }
        }

        /// <summary>
        /// Bookmarks live in a single file and everything else in a folder, so the picker matches the
        /// source rather than making the user work out which one this is.
        /// </summary>
        private async Task ChooseDesktopIntegrationPathAsync(string id, string kind)
        {
            try
            {
                string? path = string.Equals(kind, "bookmarks", StringComparison.OrdinalIgnoreCase)
                    ? await PickFileAsync()
                    : await PickFolderAsync();
                if (string.IsNullOrWhiteSpace(path)) return;
                await ConnectDesktopIntegrationAsync(id, path);
            }
            catch (Exception ex)
            {
                Log.Write("ChooseDesktopIntegrationPathAsync failed: " + ex.Message);
                if (DesktopIntegrationsStatusText != null) DesktopIntegrationsStatusText.Text = ex.Message;
            }
        }

        private async Task<string?> PickFolderAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            // An unpackaged WinUI app has to hand the picker the window handle itself, exactly as
            // every other picker in this class does.
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        private async Task<string?> PickFileAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        /// <summary>
        /// Shows the first items PaRuX would actually read. This is the difference between trusting
        /// a claim and checking it, and it is cheap: the same read the model would do, capped.
        /// </summary>
        private async Task PreviewDesktopIntegrationAsync(string id, string displayName)
        {
            try
            {
                List<DesktopIntegrationItem> items =
                    await DesktopIntegrationClient.Current.PreviewAsync(id, 15, CancellationToken.None);

                var body = new StackPanel { Spacing = 10 };
                if (items.Count == 0)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = "PaRuX found nothing to read there yet.",
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                foreach (DesktopIntegrationItem item in items)
                {
                    var entry = new StackPanel { Spacing = 2 };
                    entry.Children.Add(new TextBlock
                    {
                        Text = item.Title,
                        Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"],
                        TextWrapping = TextWrapping.Wrap
                    });
                    entry.Children.Add(new TextBlock
                    {
                        Text = string.Join("  ·  ", new[]
                        {
                            item.Subtitle,
                            item.Timestamp?.ToLocalTime().ToString("g"),
                            item.Location
                        }.Where(part => !string.IsNullOrWhiteSpace(part))),
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                    });
                    entry.Children.Add(new TextBlock
                    {
                        Text = item.Excerpt,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
                    });
                    body.Children.Add(entry);
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "What PaRuX reads from " + displayName,
                    CloseButtonText = "Close",
                    Content = new ScrollViewer { Content = body, MaxHeight = 460 }
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Write("PreviewDesktopIntegrationAsync failed: " + ex.Message);
                if (DesktopIntegrationsStatusText != null) DesktopIntegrationsStatusText.Text = ex.Message;
            }
        }

        /// <summary>
        /// The folder generated documents land in. Shown as a path rather than a friendly label
        /// because the one thing a user needs from this row is to be able to go and find the file.
        /// </summary>
        private async Task RefreshDocumentOutputAsync()
        {
            if (DocumentOutputText == null) return;
            try
            {
                DocumentOutputStatus status =
                    await DesktopIntegrationClient.Current.GetDocumentOutputAsync(CancellationToken.None);
                ShowDocumentOutput(status);
            }
            catch (Exception ex)
            {
                Log.Write("RefreshDocumentOutputAsync failed: " + ex.Message);
                DocumentOutputText.Text = "Couldn't read the output folder: " + ex.Message;
            }
        }

        private void ShowDocumentOutput(DocumentOutputStatus status)
        {
            if (DocumentOutputText == null) return;
            DocumentOutputText.Text = status.IsDefault
                ? "Saving to PaRuX's own folder: " + status.Folder
                : "Saving to " + status.Folder;
            if (ResetDocumentOutputButton != null) ResetDocumentOutputButton.IsEnabled = !status.IsDefault;
        }

        private async void ChooseDocumentOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChooseDocumentOutputButton != null) ChooseDocumentOutputButton.IsEnabled = false;
                string? folder = await PickFolderAsync();
                if (string.IsNullOrWhiteSpace(folder)) return;
                ShowDocumentOutput(
                    await DesktopIntegrationClient.Current.SetDocumentOutputAsync(folder, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Log.Write("ChooseDocumentOutput_Click failed: " + ex.Message);
                if (DocumentOutputText != null) DocumentOutputText.Text = ex.Message;
            }
            finally
            {
                if (ChooseDocumentOutputButton != null) ChooseDocumentOutputButton.IsEnabled = true;
            }
        }

        private async void ResetDocumentOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ResetDocumentOutputButton != null) ResetDocumentOutputButton.IsEnabled = false;
                ShowDocumentOutput(
                    await DesktopIntegrationClient.Current.SetDocumentOutputAsync(null, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Log.Write("ResetDocumentOutput_Click failed: " + ex.Message);
                if (DocumentOutputText != null) DocumentOutputText.Text = ex.Message;
                if (ResetDocumentOutputButton != null) ResetDocumentOutputButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Re-runs the sidebar walkthrough. The tour lives on MainWindow because it points at
        /// MainWindow's own controls; Settings only asks for it.
        /// </summary>
        private void ShowGuidedTour_Click(object sender, RoutedEventArgs e)
        {
            App.MainWin?.StartGuidedTour();
        }

        private async void RefreshDesktopIntegrations_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RefreshDesktopIntegrationsButton != null) RefreshDesktopIntegrationsButton.IsEnabled = false;
                await RefreshDesktopIntegrationsAsync();
            }
            finally
            {
                if (RefreshDesktopIntegrationsButton != null) RefreshDesktopIntegrationsButton.IsEnabled = true;
            }
        }
    }
}
