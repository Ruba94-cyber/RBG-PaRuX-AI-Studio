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
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Settings › Runtime Components & Lifecycle: inspect component health (installed, version, path,
    /// and ownership: PaRuX-managed, user-managed or system-detected) and run Detect, Locate, Install,
    /// or Skip at any time.
    /// </summary>
    public sealed partial class SettingsView
    {
        private bool _componentsSettingsBusy;
        private CancellationTokenSource? _componentsSettingsCts;

        private async void RefreshComponentsSettings_Click(object sender, RoutedEventArgs e)
        {
            await RefreshRuntimeComponentsAsync();
        }

        public async Task RefreshRuntimeComponentsAsync()
        {
            if (_componentsSettingsBusy || ComponentsSettingsList == null) return;
            SetComponentsSettingsBusy(true, "Detecting installed components…");
            try
            {
                List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
                RenderComponentsSettings(components);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = components.Count + " component(s) checked. Detection never installs or changes software.";
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings RefreshRuntimeComponents failed: " + ex.Message);
                if (ComponentsSettingsList != null) ComponentsSettingsList.Children.Clear();
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = "Component discovery failed: " + ex.Message;
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
            }
        }

        private void SetComponentsSettingsBusy(bool busy, string message)
        {
            _componentsSettingsBusy = busy;
            if (ComponentsSettingsProgress != null)
            {
                ComponentsSettingsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ComponentsSettingsStatusText != null && !string.IsNullOrWhiteSpace(message))
            {
                ComponentsSettingsStatusText.Text = message;
            }
            if (RefreshComponentsSettingsButton != null)
            {
                RefreshComponentsSettingsButton.IsEnabled = !busy;
            }
        }

        private void RenderComponentsSettings(IReadOnlyList<ComponentStatus> components)
        {
            if (ComponentsSettingsList == null) return;
            ComponentsSettingsList.Children.Clear();

            foreach (ComponentStatus component in components)
            {
                ComponentDisplayItem item = ComponentLifecyclePresenter.FormatComponent(component);
                string id = item.Id;

                var card = new Border
                {
                    Padding = new Thickness(12),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var cardBody = new StackPanel { Spacing = 4 };
                card.Child = cardBody;

                var header = new Grid { ColumnSpacing = 8 };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = new TextBlock
                {
                    Text = item.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                AutomationProperties.SetAutomationId(title, "Settings_Component_" + id + "_Title");
                Grid.SetColumn(title, 0);
                header.Children.Add(title);

                var ownership = new TextBlock
                {
                    Text = item.OwnershipLabel,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                    Opacity = 0.75
                };
                AutomationProperties.SetAutomationId(ownership, "Settings_Component_" + id + "_Ownership");
                Grid.SetColumn(ownership, 1);
                header.Children.Add(ownership);
                cardBody.Children.Add(header);

                var stateBlock = new TextBlock
                {
                    Text = item.StateSummary,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.85
                };
                AutomationProperties.SetAutomationId(stateBlock, "Settings_Component_" + id + "_State");
                cardBody.Children.Add(stateBlock);

                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    var pathBlock = new TextBlock
                    {
                        Text = item.Path,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        Opacity = 0.7
                    };
                    AutomationProperties.SetAutomationId(pathBlock, "Settings_Component_" + id + "_Path");
                    cardBody.Children.Add(pathBlock);
                }

                if (!string.IsNullOrWhiteSpace(item.ValidationMessage))
                {
                    var valBlock = new TextBlock
                    {
                        Text = item.ValidationMessage,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        Opacity = 0.7
                    };
                    AutomationProperties.SetAutomationId(valBlock, "Settings_Component_" + id + "_ValidationMessage");
                    cardBody.Children.Add(valBlock);
                }

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(0, 4, 0, 0)
                };

                if (item.CanInstall)
                {
                    var btn = SettingsComponentActionButton(item.InstallActionLabel, "Settings_Component_" + id + "_InstallButton",
                        async () => await InstallComponentSettingsAsync(component));
                    actions.Children.Add(btn);
                }

                if (item.CanLocate)
                {
                    var btn = SettingsComponentActionButton(item.LocateActionLabel, "Settings_Component_" + id + "_LocateButton",
                        async () => await LocateComponentSettingsAsync(component));
                    actions.Children.Add(btn);
                }

                if (item.CanRemove)
                {
                    var btn = SettingsComponentActionButton(item.RemoveActionLabel, "Settings_Component_" + id + "_RemoveButton",
                        async () => await RemoveLocationSettingsAsync(component));
                    actions.Children.Add(btn);
                }

                if (item.CanSkip)
                {
                    var btn = SettingsComponentActionButton(item.SkipActionLabel, "Settings_Component_" + id + "_SkipButton",
                        async () => await SkipComponentSettingsAsync(component));
                    actions.Children.Add(btn);
                }

                if (item.CanUnskip)
                {
                    var btn = SettingsComponentActionButton(item.UnskipActionLabel, "Settings_Component_" + id + "_UnskipButton",
                        async () => await UnskipComponentSettingsAsync(component));
                    actions.Children.Add(btn);
                }

                if (actions.Children.Count > 0)
                {
                    cardBody.Children.Add(actions);
                }

                ComponentsSettingsList.Children.Add(card);
            }
        }

        private Button SettingsComponentActionButton(string text, string automationId, Func<Task> action)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(8, 3, 8, 3)
            };
            AutomationProperties.SetAutomationId(button, automationId);
            AutomationProperties.SetName(button, text);
            button.Click += async (s, e) =>
            {
                if (_componentsSettingsBusy) return;
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    Log.Write("Settings component action failed: " + ex.Message);
                    if (ComponentsSettingsStatusText != null)
                    {
                        ComponentsSettingsStatusText.Text = ex.Message;
                    }
                    SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                }
            };
            return button;
        }

        private async Task InstallComponentSettingsAsync(ComponentStatus component)
        {
            string? backend = ComponentLifecyclePresenter.BackendForComponent(component.Id);
            if (backend == null)
            {
                throw new InvalidOperationException("This component has no PaRuX-managed installer.");
            }

            SetComponentsSettingsBusy(true, "Checking available builds for " + component.DisplayName + "…");
            _componentsSettingsCts?.Cancel();
            _componentsSettingsCts?.Dispose();
            _componentsSettingsCts = new CancellationTokenSource();
            CancellationToken token = _componentsSettingsCts.Token;

            try
            {
                List<RuntimeBuildOption> available = await RuntimeManagerClient.Current.GetAvailableBuildsAsync(backend, token);
                RuntimeBuildOption? selected = ComponentLifecyclePresenter.SelectRecommendedBuild(available);
                if (selected == null)
                {
                    throw new InvalidOperationException("No compatible Windows build is currently published for " + component.DisplayName + ".");
                }

                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = "Installing " + component.DisplayName + " " + selected.Tag
                        + (string.IsNullOrWhiteSpace(selected.Variant) ? string.Empty : " (" + selected.Variant + ")") + "…";
                }

                RuntimeInstallStatus started = await RuntimeManagerClient.Current.StartInstallAsync(
                    backend, selected.Tag, selected.Variant, token);

                RuntimeInstallStatus final = await RuntimeManagerClient.Current.WaitForInstallAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (ComponentsSettingsStatusText != null)
                        {
                            ComponentsSettingsStatusText.Text = ComponentLifecyclePresenter.FormatInstallProgress(
                                component.DisplayName, progress.Stage, progress.BytesDownloaded, progress.TotalBytes);
                        }
                    });
                }, token);

                if (final.Stage != "completed")
                {
                    throw new InvalidOperationException(final.Error ?? ("Install ended with state " + final.Stage + "."));
                }

                try
                {
                    await RuntimeManagerClient.Current.UnskipAsync(component.Id, CancellationToken.None);
                }
                catch
                {
                    // Stale skip state is cosmetic and discovery hides it while installed
                }

                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = ComponentLifecyclePresenter.FormatInstallCompletion(component.DisplayName, final);
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task LocateComponentSettingsAsync(ComponentStatus component)
        {
            string path;
            if (component.Id == "comfyui")
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                path = folder.Path;
            }
            else
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
                if (file == null) return;
                path = file.Path;
            }

            SetComponentsSettingsBusy(true, "Registering existing " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.LocateAsync(component.Id, path, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + " location registered.";
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task RemoveLocationSettingsAsync(ComponentStatus component)
        {
            SetComponentsSettingsBusy(true, "Forgetting location for " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.RemoveLocationAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + " location forgotten.";
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task SkipComponentSettingsAsync(ComponentStatus component)
        {
            SetComponentsSettingsBusy(true, "Skipping " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.SkipAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + " skipped for now.";
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }

        private async Task UnskipComponentSettingsAsync(ComponentStatus component)
        {
            SetComponentsSettingsBusy(true, "Re-enabling setup for " + component.DisplayName + "…");
            try
            {
                await RuntimeManagerClient.Current.UnskipAsync(component.Id, CancellationToken.None);
                if (ComponentsSettingsStatusText != null)
                {
                    ComponentsSettingsStatusText.Text = component.DisplayName + " setup re-enabled.";
                }
            }
            finally
            {
                SetComponentsSettingsBusy(false, ComponentsSettingsStatusText?.Text ?? string.Empty);
                await RefreshRuntimeComponentsAsync();
            }
        }
    }
}
