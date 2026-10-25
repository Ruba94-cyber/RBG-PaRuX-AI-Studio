using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.System;

namespace PaRuX.Views
{
    /// <summary>
    /// Ownership-aware update surface. Application upgrades are GitHub MSI major upgrades and are
    /// offered only after a detached signed manifest matches the exact release asset and the MSI
    /// bytes pass the signed size/SHA-256 check. The actual elevated handoff goes through the
    /// installed PaRuX Update Helper, which re-authenticates the envelope and copies the MSI into
    /// Program Files protected staging before Windows Installer sees it. PaRuX-managed runtimes
    /// reuse the Core's verified install/rollback pipeline; USER_MANAGED software is never mutated.
    /// </summary>
    public sealed class UpdateManagerView : UserControl
    {
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly StackPanel _app = new StackPanel { Spacing = 8 };
        private readonly StackPanel _components = new StackPanel { Spacing = 10 };
        private readonly StackPanel _models = new StackPanel { Spacing = 8 };
        private CancellationTokenSource? _operation;
        private string? _activeInstallId;

        public UpdateManagerView()
        {
            _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAsync(); };
            Unloaded += (_, _) => _operation?.Cancel();
        }

        public async void OnShown() => await RefreshAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(24),
                MaxWidth = 1050,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            var titleRow = new Grid { ColumnSpacing = 8 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.Children.Add(new TextBlock { Text = "Update Manager", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var refresh = new Button { Content = "Check again" };
            refresh.Click += async (_, _) => await RefreshAsync();
            Grid.SetColumn(refresh, 1); titleRow.Children.Add(refresh);
            body.Children.Add(titleRow);
            body.Children.Add(new TextBlock
            {
                Text = "One place to see what PaRuX owns and may update. The Windows app uses explicit GitHub MSI major upgrades, USER_MANAGED software is never modified, and models are never silently replaced.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            body.Children.Add(_busy);
            body.Children.Add(_status);
            body.Children.Add(Section("PARUX APPLICATION", _app));
            body.Children.Add(Section("MANAGED RUNTIMES & COMPONENTS", _components));
            body.Children.Add(Section("MODEL CONTENT", _models));
            return scroll;
        }

        private async Task RefreshAsync()
        {
            SetBusy(true, "Checking update state…");
            try
            {
                await RefreshAppAsync();
                await RefreshComponentsAsync();
                await RefreshModelsAsync();
                _status.Text = "Update state refreshed. No update is applied without an explicit action.";
            }
            catch (Exception ex) { _status.Text = "Update state refresh failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task RefreshAppAsync()
        {
            AppReleaseStatus release = await AppUpdateClient.Current.CheckLatestAsync(CancellationToken.None);
            _app.Children.Clear();
            _app.Children.Add(new TextBlock
            {
                Text = "Installed: " + release.CurrentVersion
                    + (string.IsNullOrWhiteSpace(release.LatestVersion) ? string.Empty : " · Published: " + release.LatestVersion),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            _app.Children.Add(new TextBlock { Text = release.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
            _app.Children.Add(new TextBlock
            {
                Text = release.SecureInstallerMessage,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = release.SecureInstallerReady ? 0.9 : 0.68
            });
            if (release.PublishedUtc.HasValue)
                _app.Children.Add(new TextBlock { Text = "Published " + release.PublishedUtc.Value.ToLocalTime().ToString("g"), FontSize = 12, Opacity = 0.68 });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (release.UpdateAvailable && release.SecureInstallerReady)
            {
                var install = new Button { Content = "Download & install verified MSI" };
                install.Click += async (_, _) => await InstallAppUpdateAsync(release);
                actions.Children.Add(install);
            }
            if (release.UpdateAvailable && Uri.TryCreate(release.ReleaseUrl, UriKind.Absolute, out Uri? releaseUri))
            {
                var open = new Button { Content = "Review GitHub Release" };
                open.Click += async (_, _) => await Launcher.LaunchUriAsync(releaseUri);
                actions.Children.Add(open);
            }
            if (actions.Children.Count > 0) _app.Children.Add(actions);

            _app.Children.Add(new TextBlock
            {
                Text = "Security boundary: a trusted in-app upgrade requires the detached PaRuX release signature, an exact GitHub version/asset match and MSI SHA-256 verification. After UAC, the installed Update Helper independently re-verifies that same signed envelope, copies the MSI into Program Files protected staging, verifies the protected copy again, waits for PaRuX to exit, and only then starts Windows Installer. Project/model/runtime state lives outside Program Files and is not part of the MSI replacement.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.68
            });
        }

        private async Task InstallAppUpdateAsync(AppReleaseStatus release)
        {
            if (!release.SecureInstallerReady || release.SignedManifest == null
                || string.IsNullOrWhiteSpace(release.SignedEnvelopeJson))
            {
                _status.Text = "This release is not eligible for secure in-app installation.";
                return;
            }

            var confirmation = new StackPanel { Spacing = 8 };
            confirmation.Children.Add(new TextBlock
            {
                Text = "PaRuX " + release.SignedManifest.Version + " will be downloaded from the exact signed GitHub Release asset. Its signed envelope and MSI hash are checked before elevation and independently checked again by the installed Update Helper after UAC.",
                TextWrapping = TextWrapping.Wrap
            });
            confirmation.Children.Add(new TextBlock
            {
                Text = "Windows will request administrator approval for PaRuX Update Helper. The helper stages the verified MSI under Program Files, starts Windows Installer only from that protected copy, and waits for this PaRuX process to close before the Major Upgrade begins. Your Projects, models, settings, runtime data and generated files are outside the MSI install directory and are not intentionally removed by this upgrade.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            var dialog = new ContentDialog
            {
                Title = "Install verified PaRuX update?",
                Content = confirmation,
                PrimaryButtonText = "Download, verify & install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            _operation?.Cancel();
            _operation?.Dispose();
            _operation = new CancellationTokenSource();
            CancellationToken ct = _operation.Token;
            SetBusy(true, "Downloading verified PaRuX MSI…");

            try
            {
                var progress = new Progress<AppUpdateDownloadProgress>(p =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        var presentation = InstallProgressPresenter.Present(p.BytesDownloaded, p.TotalBytes, "downloading", prefix: "Downloading PaRuX MSI");
                        _busy.IsIndeterminate = presentation.IsIndeterminate;
                        if (presentation.Fraction.HasValue)
                        {
                            _busy.Minimum = 0;
                            _busy.Maximum = 1;
                            _busy.Value = presentation.Fraction.Value;
                        }
                        _status.Text = presentation.FullText;
                    });
                });

                string msiPath = await AppUpdateClient.Current.DownloadVerifiedMsiAsync(release, progress, ct);
                ct.ThrowIfCancellationRequested();
                string envelopePath = await PersistVerifiedEnvelopeForHandoffAsync(release, msiPath, ct);
                ct.ThrowIfCancellationRequested();

                string helperPath = Path.Combine(AppContext.BaseDirectory, "UpdateHelper", "PaRuX.UpdateHelper.exe");
                if (!File.Exists(helperPath))
                    throw new FileNotFoundException("The installed PaRuX Update Helper is missing. Repair/reinstall PaRuX before using in-app upgrades.", helperPath);

                _status.Text = "MSI + signed envelope verified. Requesting protected updater elevation…";
                var helperInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = true, // parux:shell-execute-reviewed: Verb="runas" UAC elevation requires it; helperPath is the app's own verified install path, checked above.
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory
                };
                helperInfo.ArgumentList.Add("--msi");
                helperInfo.ArgumentList.Add(msiPath);
                helperInfo.ArgumentList.Add("--envelope");
                helperInfo.ArgumentList.Add(envelopePath);
                helperInfo.ArgumentList.Add("--wait-pid");
                helperInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

                using Process? bootstrap = Process.Start(helperInfo);
                if (bootstrap == null)
                    throw new InvalidOperationException("PaRuX Update Helper did not start.");

                // Do not cancel this wait after UAC has been approved. Exit code 0 means an elevated
                // worker has already copied and re-verified the update under protected Program Files
                // staging and is waiting for this exact Desktop PID to terminate.
                await bootstrap.WaitForExitAsync();
                if (bootstrap.ExitCode != 0)
                    throw new InvalidOperationException(
                        "PaRuX Update Helper rejected the update before Windows Installer handoff (exit "
                        + bootstrap.ExitCode + "). No MSI was started.");

                _status.Text = "Protected update staging verified. PaRuX is closing so Windows Installer can perform the Major Upgrade.";
                global::PaRuX.App.ExitApplication();
            }
            catch (OperationCanceledException)
            {
                _status.Text = "Application update download cancelled. Nothing was installed.";
                SetBusy(false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _status.Text = "Administrator approval was cancelled. PaRuX remains unchanged.";
                SetBusy(false);
            }
            catch (Exception ex)
            {
                _status.Text = "Application update was not installed: " + ex.Message;
                SetBusy(false);
            }
        }

        private static async Task<string> PersistVerifiedEnvelopeForHandoffAsync(
            AppReleaseStatus release, string msiPath, CancellationToken ct)
        {
            if (release.SignedManifest == null || string.IsNullOrWhiteSpace(release.SignedEnvelopeJson))
                throw new InvalidOperationException("Verified signed update envelope is unavailable.");

            byte[] bytes = Encoding.UTF8.GetBytes(release.SignedEnvelopeJson);
            if (bytes.Length <= 0 || bytes.Length > SignedAppUpdateVerifier.MaximumEnvelopeBytes)
                throw new InvalidDataException("Signed update envelope size is outside the allowed bound.");

            SignedAppUpdateVerification verification = SignedAppUpdateVerifier.Verify(
                release.SignedEnvelopeJson, ReleaseTrustAnchor.PublicKeyPem);
            if (!verification.Valid || verification.Manifest == null
                || !string.Equals(verification.Manifest.Version, release.SignedManifest.Version, StringComparison.Ordinal)
                || !string.Equals(verification.Manifest.PackageSha256, release.SignedManifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Signed update envelope changed after release verification.");

            string directory = Path.GetDirectoryName(Path.GetFullPath(msiPath))
                ?? throw new InvalidDataException("Downloaded MSI directory is unavailable.");
            string finalPath = Path.Combine(directory,
                "PaRuX-" + release.SignedManifest.Version + "-win-x64.update.json");
            string pendingPath = finalPath + ".pending";
            try
            {
                if (File.Exists(pendingPath)) File.Delete(pendingPath);
                await using (var stream = new FileStream(
                    pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
                    await stream.FlushAsync(ct);
                }
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(pendingPath, finalPath);
                return finalPath;
            }
            catch
            {
                try { if (File.Exists(pendingPath)) File.Delete(pendingPath); } catch { }
                throw;
            }
        }

        private async Task RefreshComponentsAsync()
        {
            List<ComponentStatus> components = await RuntimeManagerClient.Current.GetComponentsAsync(CancellationToken.None);
            _components.Children.Clear();
            foreach (ComponentStatus component in components)
            {
                var card = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
                var body = new StackPanel { Spacing = 5 };
                card.Child = body;
                body.Children.Add(new TextBlock
                {
                    Text = component.DisplayName + " · " + Ownership(component.Ownership),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                body.Children.Add(new TextBlock
                {
                    Text = (component.Detected ? component.Health : component.Skipped ? "skipped" : "not configured")
                        + (string.IsNullOrWhiteSpace(component.Version) ? string.Empty : " · " + component.Version)
                        + " · validation " + component.ValidationStatus,
                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.72
                });

                string? backend = Backend(component.Id);
                if (component.Ownership == ComponentOwnership.PARUX_MANAGED && backend != null)
                {
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var update = new Button { Content = "Check / update…" };
                    update.Click += async (_, _) => await CheckRuntimeBuildsAsync(component, backend);
                    actions.Children.Add(update);
                    if (component.Actions.Contains("rollback"))
                    {
                        var rollback = new Button { Content = "Rollback…" };
                        rollback.Click += async (_, _) => await RollbackAsync(component, backend);
                        actions.Children.Add(rollback);
                    }
                    body.Children.Add(actions);
                }
                else if (component.Ownership == ComponentOwnership.USER_MANAGED)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = "Managed by you. PaRuX can use/validate this location but Update Manager will not modify it.",
                        FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.68
                    });
                }
                else if (component.Ownership == ComponentOwnership.SYSTEM_DETECTED)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = "System-detected dependency. Update it through the operating system or its own package source.",
                        FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.68
                    });
                }
                _components.Children.Add(card);
            }
        }

        private async Task CheckRuntimeBuildsAsync(ComponentStatus component, string backend)
        {
            try
            {
                SetBusy(true, "Loading verified builds for " + component.DisplayName + "…");
                List<RuntimeBuildOption> available = await RuntimeManagerClient.Current.GetAvailableBuildsAsync(backend, CancellationToken.None);
                if (available.Count == 0) throw new InvalidOperationException("No compatible verified build is currently advertised.");

                var combo = new ComboBox { MinWidth = 560 };
                foreach (RuntimeBuildOption build in available)
                {
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = build.Tag
                            + (string.IsNullOrWhiteSpace(build.Variant) ? string.Empty : " / " + build.Variant)
                            + (build.Recommended ? " · recommended" : string.Empty)
                            + (build.InstalledLocally ? " · already installed" : string.Empty),
                        Tag = build
                    });
                }
                int recommended = available.FindIndex(x => x.Recommended);
                combo.SelectedIndex = recommended >= 0 ? recommended : 0;
                var dialog = new ContentDialog
                {
                    Title = "Install / update " + component.DisplayName,
                    Content = combo,
                    PrimaryButtonText = "Install selected build",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { SetBusy(false); return; }
                if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not RuntimeBuildOption selected) { SetBusy(false); return; }
                await InstallRuntimeAsync(component, selected);
            }
            catch (Exception ex) { _status.Text = "Runtime update failed: " + ex.Message; SetBusy(false); }
        }

        private async Task InstallRuntimeAsync(ComponentStatus component, RuntimeBuildOption build)
        {
            _operation?.Cancel();
            _operation?.Dispose();
            _operation = new CancellationTokenSource();
            CancellationToken ct = _operation.Token;
            RuntimeInstallStatus started = await RuntimeManagerClient.Current.StartInstallAsync(build.Backend, build.Tag, build.Variant ?? string.Empty, ct);
            _activeInstallId = started.Id;
            try
            {
                RuntimeInstallStatus final = await RuntimeManagerClient.Current.WaitForInstallAsync(started.Id, progress =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        var presentation = InstallProgressPresenter.Present(progress, component.DisplayName);
                        _busy.IsIndeterminate = presentation.IsIndeterminate;
                        if (presentation.Fraction.HasValue)
                        {
                            _busy.Minimum = 0;
                            _busy.Maximum = 1;
                            _busy.Value = presentation.Fraction.Value;
                        }
                        _status.Text = presentation.FullText;
                    });
                }, ct);
                if (final.Stage != "completed") throw new InvalidOperationException(final.Error ?? "Update ended as " + final.Stage + ".");
                _status.Text = component.DisplayName + " updated to " + final.Tag + ".";
            }
            finally
            {
                _activeInstallId = null;
                SetBusy(false);
                await RefreshComponentsAsync();
            }
        }

        private async Task RollbackAsync(ComponentStatus component, string backend)
        {
            try
            {
                List<RuntimeBuildOption> installed = await RuntimeManagerClient.Current.GetInstalledBuildsAsync(backend, CancellationToken.None);
                List<RuntimeBuildOption> targets = installed.Where(x => !x.IsActive).ToList();
                if (targets.Count == 0) { _status.Text = "No previous managed build is available for rollback."; return; }
                var combo = new ComboBox { MinWidth = 520 };
                foreach (RuntimeBuildOption target in targets)
                    combo.Items.Add(new ComboBoxItem { Content = target.Tag + (string.IsNullOrWhiteSpace(target.Variant) ? string.Empty : " / " + target.Variant), Tag = target });
                combo.SelectedIndex = 0;
                var dialog = new ContentDialog
                {
                    Title = "Rollback " + component.DisplayName,
                    Content = combo,
                    PrimaryButtonText = "Rollback",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not RuntimeBuildOption selected) return;
                await RuntimeManagerClient.Current.RollbackAsync(backend, selected.Tag, selected.Variant ?? string.Empty, CancellationToken.None);
                _status.Text = component.DisplayName + " rolled back to " + selected.Tag + ".";
                await RefreshComponentsAsync();
            }
            catch (Exception ex) { _status.Text = "Rollback failed: " + ex.Message; }
        }

        private async Task RefreshModelsAsync()
        {
            List<ManagedModelInfo> models = await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None);
            _models.Children.Clear();
            _models.Children.Add(new TextBlock
            {
                Text = models.Count + " managed model file(s). Models are content, not executable runtime updates: PaRuX never replaces a quantization or revision silently. Choose any replacement explicitly in Model Hub.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });
            foreach (ManagedModelInfo model in models.Take(30))
            {
                _models.Children.Add(new TextBlock
                {
                    Text = (string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias + " · " + model.FileName)
                        + " · " + model.Quantization
                        + (string.IsNullOrWhiteSpace(model.RepoId) ? " · source unknown" : " · " + model.RepoId),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                });
            }
        }

        private static string? Backend(string componentId) => componentId switch
        {
            "llama-server-cuda" => "text-cuda",
            "llama-server-vulkan" => "text-vulkan",
            "comfyui" => "media-comfyui",
            _ => null
        };

        private static string Ownership(ComponentOwnership ownership) => ownership switch
        {
            ComponentOwnership.PARUX_MANAGED => "PaRuX managed",
            ComponentOwnership.USER_MANAGED => "User managed",
            ComponentOwnership.SYSTEM_DETECTED => "System detected",
            _ => ownership.ToString()
        };

        private static Border Section(string title, UIElement content)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            panel.Children.Add(content);
            return new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel };
        }

        private void SetBusy(bool busy, string? text = null)
        {
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!busy) _busy.IsIndeterminate = true;
            if (!string.IsNullOrWhiteSpace(text)) _status.Text = text;
        }

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
    }
}
