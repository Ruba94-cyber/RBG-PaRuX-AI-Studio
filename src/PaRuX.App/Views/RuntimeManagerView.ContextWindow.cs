using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Controls the PHYSICAL attention window used when a model is launched. PaRuX Virtual Context
    /// is separate and automatic: conversations/projects may grow far beyond this number because
    /// older state is hierarchically compacted and task-relevant history is recalled back into the
    /// finite physical window. This partial also exposes the Core-owned Execution Profile and
    /// declarative Plugin Bundle surfaces beside runtime controls so policy is visible where model
    /// resources are configured instead of remaining an API-only capability.
    /// </summary>
    public sealed partial class RuntimeManagerView
    {
        private readonly ComboBox _contextLength = new ComboBox { MinWidth = 260 };
        private readonly TextBlock _contextSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        private readonly TextBlock _contextCost = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.68 };
        private readonly CheckBox _allowBeyondTrained = new CheckBox
        {
            Content = "Allow physical context beyond this model's trained window (experimental / lower far-context quality)",
            IsChecked = false
        };

        private readonly ComboBox _executionQuality = new ComboBox { MinWidth = 190 };
        private readonly TextBlock _executionProfileSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };

        private readonly TextBox _pluginBundlePath = new TextBox
        {
            PlaceholderText = "Select a .parux-plugin bundle to inspect",
            MinWidth = 430
        };
        private readonly TextBlock _pluginInspectionSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82 };
        private readonly StackPanel _trustedPublishers = new StackPanel { Spacing = 6 };
        private readonly CheckBox _pluginAllowUnsigned = new CheckBox
        {
            Content = "Allow unsigned bundle for this install (explicit unsafe exception)",
            IsChecked = false
        };
        private readonly CheckBox _pluginTrustPublisher = new CheckBox
        {
            Content = "Trust this verified publisher fingerprint for future installs",
            IsChecked = false
        };

        private ModelContextOptions? _contextOptions;
        private PluginBundleInspection? _lastPluginInspection;

        private UIElement BuildContextCard()
        {
            var surface = new StackPanel { Spacing = 16 };
            surface.Children.Add(BuildPhysicalContextSection());
            surface.Children.Add(BuildExecutionProfileSection());
            surface.Children.Add(BuildPluginBundleSection());
            return surface;
        }

        private UIElement BuildPhysicalContextSection()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;

            body.Children.Add(new TextBlock
            {
                Text = "Physical context window",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "This is how many tokens the selected model can attend to simultaneously in one inference. "
                    + "PaRuX derives launchable values from the model's attention geometry and this machine's memory. "
                    + "Choosing a larger physical window consumes more KV-cache memory; stretching beyond the trained "
                    + "window uses positional scaling and can reduce recall/coherence near the far end.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            var virtualContext = new Border
            {
                Padding = new Thickness(12),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
            virtualContext.Child = new TextBlock
            {
                Text = "Virtual Context · automatic\n"
                    + "You do NOT need a native 128k model to keep working past 128k. PaRuX can maintain 128k, 256k, "
                    + "1M+ logical histories on any model by keeping recent turns verbatim, preserving durable constraints/"
                    + "decisions/IDs/test results, and recalling task-relevant historical pages into the real window. "
                    + "This is long-running memory, not a false claim that every historical token is simultaneously in attention.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.86
            };
            body.Children.Add(virtualContext);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(new TextBlock
            {
                Text = "Physical length",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7
            });
            controls.Children.Add(_contextLength);

            var apply = new Button { Content = "Apply" };
            apply.Click += async (_, _) => await ApplyContextLengthAsync();
            controls.Children.Add(apply);

            var reset = new Button { Content = "Let PaRuX choose" };
            reset.Click += async (_, _) => await ClearContextLengthAsync();
            controls.Children.Add(reset);

            body.Children.Add(controls);
            body.Children.Add(_allowBeyondTrained);
            body.Children.Add(_contextCost);
            body.Children.Add(_contextSummary);

            _contextLength.SelectionChanged += (_, _) => ShowSelectedCost();
            return card;
        }

        private UIElement BuildExecutionProfileSection()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;
            body.Children.Add(new TextBlock
            {
                Text = "Execution Profile",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "One Core-owned policy controls time, tool budget, evidence/context spend, verification and parallelism across PaRuX. The selected model keeps its explicit routing choice; this changes how much work PaRuX may spend, not which model you selected.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_executionQuality, "Execution quality");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_contextLength, "Context length");
            foreach (string quality in ExecutionQualityProfile.All)
                _executionQuality.Items.Add(new ComboBoxItem { Content = ExecutionQualityLabel(quality), Tag = quality });
            _executionQuality.SelectedIndex = 1;

            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            controls.Children.Add(new TextBlock { Text = "Quality", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            controls.Children.Add(_executionQuality);
            var apply = new Button { Content = "Apply profile" };
            apply.Click += async (_, _) => await ApplyExecutionQualityAsync();
            controls.Children.Add(apply);
            var refresh = new Button { Content = "Refresh" };
            refresh.Click += async (_, _) => await RefreshExecutionProfileAsync(CancellationToken.None);
            controls.Children.Add(refresh);
            body.Children.Add(controls);
            body.Children.Add(_executionProfileSummary);
            return card;
        }

        private UIElement BuildPluginBundleSection()
        {
            var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            var body = new StackPanel { Spacing = 10 };
            card.Child = body;
            body.Children.Add(new TextBlock
            {
                Text = "Plugin Bundles",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            body.Children.Add(new TextBlock
            {
                Text = "Inspect declarative .parux-plugin bundles before installation. PaRuX validates the archive, signature/trust and requested permissions. Installed skills/automations remain disabled and MCP definitions remain staged/inert until separately enabled.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            });

            var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            pathRow.Children.Add(_pluginBundlePath);
            var browse = new Button { Content = "Browse…" };
            browse.Click += async (_, _) => await ChoosePluginBundleAsync();
            pathRow.Children.Add(browse);
            var inspect = new Button { Content = "Inspect" };
            inspect.Click += async (_, _) => await InspectPluginBundleAsync();
            pathRow.Children.Add(inspect);
            body.Children.Add(pathRow);
            body.Children.Add(_pluginInspectionSummary);
            body.Children.Add(_pluginAllowUnsigned);
            body.Children.Add(_pluginTrustPublisher);

            var installRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var install = new Button { Content = "Install inspected bundle" };
            install.Click += async (_, _) => await InstallPluginBundleAsync();
            installRow.Children.Add(install);
            var refreshTrust = new Button { Content = "Refresh trusted publishers" };
            refreshTrust.Click += async (_, _) => await RefreshTrustedPublishersAsync(CancellationToken.None);
            installRow.Children.Add(refreshTrust);
            body.Children.Add(installRow);
            body.Children.Add(new TextBlock
            {
                Text = "Trusted publishers",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            });
            body.Children.Add(_trustedPublishers);
            return card;
        }

        private async Task RefreshContextOptionsAsync(CancellationToken ct)
        {
            // These surfaces are independent from GGUF context selection, but RuntimeManagerView's
            // existing refresh path already calls this method after model discovery. Refresh them
            // here so the UI never displays stale Core policy/trust state.
            await RefreshExecutionProfileAsync(ct);
            await RefreshTrustedPublishersAsync(ct);

            _contextLength.Items.Clear();
            _contextOptions = null;
            _contextCost.Text = string.Empty;

            if (string.IsNullOrWhiteSpace(_selectedModelPath))
            {
                _contextSummary.Text = "Choose a model above to see its physical context options. Virtual Context remains automatic.";
                return;
            }

            ModelContextOptions options;
            try
            {
                options = await ModelRolesClient.Current.GetContextOptionsAsync(_selectedModelPath!, ct);
            }
            catch (Exception ex)
            {
                _contextSummary.Text = "Could not read this model's physical context options: " + ex.Message;
                return;
            }

            _contextOptions = options;

            if (!options.Known || options.Options.Count == 0)
            {
                _contextSummary.Text = options.Explanation
                    + " Virtual Context still allows histories larger than the model's native window without guessing a launch size.";
                return;
            }

            foreach (ModelContextOption option in options.Options)
            {
                _contextLength.Items.Add(new ComboBoxItem
                {
                    Content = DescribeLength(option.ContextLength) + "  ·  " + DescribeGigabytes(option.KvCacheBytes)
                        + " KV cache" + (option.RopeScale > 1.0 ? "  ·  beyond trained window" : string.Empty),
                    Tag = option
                });
            }

            long selected = options.Selected?.ContextLength ?? 0;
            int index = selected > 0
                ? options.Options.FindIndex(option => option.ContextLength == selected)
                : -1;
            _contextLength.SelectedIndex = index >= 0 ? index : options.Options.Count - 1;

            _contextSummary.Text = options.Selected != null
                ? "Physical context is currently " + DescribeLength(options.Selected.ContextLength)
                    + "; this model was trained for " + DescribeLength(options.DeclaredMaximum)
                    + ". Virtual Context continues beyond that physical window automatically."
                : "PaRuX is choosing this model's physical context. It was trained for "
                    + DescribeLength(options.DeclaredMaximum)
                    + ". Virtual Context continues beyond that physical window automatically.";
            ShowSelectedCost();
        }

        private async Task RefreshExecutionProfileAsync(CancellationToken ct)
        {
            try
            {
                ExecutionProfileStatus status = await ProductV2Client.Current
                    .GetExecutionProfileAsync(_selectedModelPath, ct);
                SelectExecutionQuality(status.ActiveQualityProfile);
                ModelExecutionProfile profile = status.Effective;
                ExecutionResourceBudget budget = profile.Budget;
                string scope = string.IsNullOrWhiteSpace(_selectedModelPath)
                    ? "Global policy"
                    : "Effective policy for " + System.IO.Path.GetFileName(_selectedModelPath);
                _executionProfileSummary.Text = scope
                    + " · " + ExecutionQualityLabel(status.ActiveQualityProfile)
                    + "\nContext " + profile.PreferredContextTokens.ToString("N0") + " tokens @ " + budget.ContextFraction.ToString("P0")
                    + " · tools " + budget.MaxToolSteps
                    + " · tool evidence " + profile.ToolObservationCharacters.ToString("N0") + " chars"
                    + " · repository evidence " + profile.RepositoryEvidenceCharacters.ToString("N0") + " chars"
                    + "\nVerification " + budget.VerificationPasses + " pass(es)"
                    + " · parallel tasks " + budget.MaxParallelTasks
                    + " · subagents " + budget.MaxDirectSubagents + " / depth " + budget.MaxDelegationDepth
                    + " · " + profile.ToolStrategy + " · " + profile.StructuredOutputStrategy
                    + "\nEvidence source: " + profile.EvidenceSource;
            }
            catch (Exception ex)
            {
                _executionProfileSummary.Text = "Could not load the Execution Profile from Core: " + ex.Message;
            }
        }

        private async Task ApplyExecutionQualityAsync()
        {
            string quality = SelectedExecutionQuality();
            try
            {
                ExecutionProfileStatus status = await ProductV2Client.Current
                    .SetExecutionQualityAsync(quality, _selectedModelPath, CancellationToken.None);
                SelectExecutionQuality(status.ActiveQualityProfile);
                await RefreshExecutionProfileAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _executionProfileSummary.Text = "Could not apply the Execution Profile: " + ex.Message;
            }
        }

        private string SelectedExecutionQuality() =>
            _executionQuality.SelectedItem is ComboBoxItem item && item.Tag is string value
                ? value
                : ExecutionQualityProfile.Balanced;

        private void SelectExecutionQuality(string quality)
        {
            for (int i = 0; i < _executionQuality.Items.Count; i++)
            {
                if (_executionQuality.Items[i] is ComboBoxItem item
                    && item.Tag is string value
                    && string.Equals(value, quality, StringComparison.Ordinal))
                {
                    _executionQuality.SelectedIndex = i;
                    return;
                }
            }
        }

        private static string ExecutionQualityLabel(string quality) => quality switch
        {
            ExecutionQualityProfile.Fast => "Fast",
            ExecutionQualityProfile.MaximumQuality => "Maximum Quality",
            ExecutionQualityProfile.Overnight => "Overnight",
            _ => "Balanced"
        };

        private async Task ChoosePluginBundleAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".parux-plugin");
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file == null) return;
            _pluginBundlePath.Text = file.Path;
            _lastPluginInspection = null;
            _pluginInspectionSummary.Text = "Bundle selected. Inspect it before installation.";
        }

        private async Task InspectPluginBundleAsync()
        {
            string path = _pluginBundlePath.Text?.Trim() ?? string.Empty;
            if (path.Length == 0)
            {
                _pluginInspectionSummary.Text = "Choose a plugin bundle first.";
                return;
            }
            try
            {
                PluginBundleInspection inspection = await ProductV2Client.Current
                    .InspectPluginAsync(path, CancellationToken.None);
                _lastPluginInspection = inspection;
                _pluginInspectionSummary.Text = RenderPluginInspection(inspection);
            }
            catch (Exception ex)
            {
                _lastPluginInspection = null;
                _pluginInspectionSummary.Text = "Plugin inspection failed: " + ex.Message;
            }
        }

        private async Task InstallPluginBundleAsync()
        {
            string path = _pluginBundlePath.Text?.Trim() ?? string.Empty;
            PluginBundleInspection? inspection = _lastPluginInspection;
            if (inspection == null || !inspection.Valid || path.Length == 0)
            {
                _pluginInspectionSummary.Text = "Inspect a valid bundle before installing it.";
                return;
            }
            if (!string.Equals(path, inspection.Manifest == null ? string.Empty : path, StringComparison.Ordinal))
            {
                // Defensive reset point: inspection is intentionally tied to the current path.
                _lastPluginInspection = null;
                _pluginInspectionSummary.Text = "The selected bundle changed. Inspect it again before installation.";
                return;
            }
            if (!inspection.SignaturePresent && _pluginAllowUnsigned.IsChecked != true)
            {
                _pluginInspectionSummary.Text = "This bundle is unsigned. Enable the explicit unsigned exception or use a signed bundle.";
                return;
            }
            if (inspection.SignaturePresent && !inspection.SignatureValid)
            {
                _pluginInspectionSummary.Text = "The bundle signature is invalid. PaRuX will not install it.";
                return;
            }

            string trust = inspection.SignaturePresent
                ? inspection.PublisherTrusted ? "Publisher is already trusted." : "Publisher is not yet trusted."
                : "UNSIGNED bundle: publisher identity is not cryptographically verified.";
            var dialog = new ContentDialog
            {
                Title = "Install inspected plugin bundle?",
                Content = inspection.Manifest.DisplayName + " " + inspection.Manifest.Version + "\n"
                    + trust + "\n"
                    + BuildPermissionSummary(inspection.EffectivePermissions) + "\n\n"
                    + "Skills/automations will be installed disabled and MCP definitions staged inert. Review them before enabling anything.",
                PrimaryButtonText = "Install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                PluginInstallResult result = await ProductV2Client.Current.InstallPluginAsync(new PluginInstallRequest
                {
                    BundlePath = path,
                    AllowUnsigned = _pluginAllowUnsigned.IsChecked == true,
                    TrustPublisher = _pluginTrustPublisher.IsChecked == true
                }, CancellationToken.None);
                _pluginInspectionSummary.Text = result.Message
                    + "\nInstalled skills: " + result.SkillIds.Count
                    + " · automations: " + result.AutomationIds.Count
                    + " · staged MCP definitions: " + result.StagedMcpDefinitions;
                await RefreshTrustedPublishersAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _pluginInspectionSummary.Text = "Plugin installation failed: " + ex.Message;
            }
        }

        private async Task RefreshTrustedPublishersAsync(CancellationToken ct)
        {
            try
            {
                List<PluginPublisherTrustDto> publishers = await ProductV2Client.Current
                    .ListTrustedPublishersAsync(ct);
                _trustedPublishers.Children.Clear();
                if (publishers.Count == 0)
                {
                    _trustedPublishers.Children.Add(new TextBlock
                    {
                        Text = "No publisher fingerprints are trusted yet.",
                        Opacity = 0.68
                    });
                    return;
                }

                foreach (PluginPublisherTrustDto publisher in publishers.OrderBy(x => x.Publisher))
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock
                    {
                        Text = publisher.Publisher + " · " + ShortFingerprint(publisher.Fingerprint),
                        VerticalAlignment = VerticalAlignment.Center,
                        MinWidth = 360
                    });
                    var revoke = new Button { Content = "Revoke trust", Tag = publisher.Fingerprint };
                    revoke.Click += async (sender, _) =>
                    {
                        if (sender is not Button button || button.Tag is not string fingerprint) return;
                        try
                        {
                            await ProductV2Client.Current.RevokeTrustedPublisherAsync(fingerprint, CancellationToken.None);
                            await RefreshTrustedPublishersAsync(CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _pluginInspectionSummary.Text = "Could not revoke publisher trust: " + ex.Message;
                        }
                    };
                    row.Children.Add(revoke);
                    _trustedPublishers.Children.Add(row);
                }
            }
            catch (Exception ex)
            {
                _trustedPublishers.Children.Clear();
                _trustedPublishers.Children.Add(new TextBlock
                {
                    Text = "Could not load trusted publishers: " + ex.Message,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        private static string RenderPluginInspection(PluginBundleInspection inspection)
        {
            string signature = !inspection.SignaturePresent
                ? "unsigned"
                : inspection.SignatureValid
                    ? inspection.PublisherTrusted ? "signature valid · publisher trusted" : "signature valid · publisher untrusted"
                    : "INVALID SIGNATURE";
            string warnings = inspection.Warnings.Count == 0
                ? string.Empty
                : "\nWarnings: " + string.Join(" | ", inspection.Warnings);
            string diff = inspection.PermissionDiff.Count == 0
                ? string.Empty
                : "\nPermission changes: " + string.Join(", ", inspection.PermissionDiff);
            return inspection.Manifest.DisplayName + " " + inspection.Manifest.Version
                + " · publisher " + inspection.Manifest.Publisher
                + " · " + signature
                + "\nSHA-256: " + inspection.BundleSha256
                + "\nSkills " + inspection.SkillCount + " · automations " + inspection.AutomationCount + " · MCP " + inspection.McpCount
                + "\n" + BuildPermissionSummary(inspection.EffectivePermissions)
                + diff + warnings;
        }

        private static string BuildPermissionSummary(PluginPermissionDeclaration permissions)
        {
            var granted = new List<string>();
            if (permissions.ReadFiles) granted.Add("read files");
            if (permissions.WriteFiles) granted.Add("write files");
            if (permissions.DeleteFiles) granted.Add("delete files");
            if (permissions.Shell) granted.Add("shell");
            if (permissions.Network) granted.Add("network");
            if (permissions.Browser) granted.Add("browser");
            if (permissions.ComputerUse) granted.Add("computer use");
            if (permissions.ExternalProcesses) granted.Add("external processes");
            if (permissions.Automations) granted.Add("automations");
            if (permissions.RemoteMcp) granted.Add("remote MCP");
            return "Declared permissions: " + (granted.Count == 0 ? "none" : string.Join(", ", granted));
        }

        private static string ShortFingerprint(string value)
        {
            string fingerprint = (value ?? string.Empty).Trim();
            return fingerprint.Length <= 18 ? fingerprint : fingerprint[..18] + "…";
        }

        private ModelContextOption? SelectedContextOption() =>
            _contextLength.SelectedItem is ComboBoxItem item ? item.Tag as ModelContextOption : null;

        private void ShowSelectedCost()
        {
            ModelContextOption? option = SelectedContextOption();
            _contextCost.Text = option == null ? string.Empty : "Physical-window cost: " + option.Cost;
        }

        private async Task ApplyContextLengthAsync()
        {
            ModelContextOption? option = SelectedContextOption();
            if (option == null || string.IsNullOrWhiteSpace(_selectedModelPath))
            {
                _contextSummary.Text = "Pick a model and a physical context length first.";
                return;
            }

            try
            {
                ModelContextPreferenceResult result = await ModelRolesClient.Current.SetContextPreferenceAsync(
                    new ModelContextPreferenceRequest
                    {
                        ModelPath = _selectedModelPath!,
                        ContextLength = option.ContextLength,
                        CacheType = option.CacheType,
                        UsesSystemRam = option.UsesSystemRam,
                        AllowBeyondTrainedContext = _allowBeyondTrained.IsChecked == true
                    },
                    CancellationToken.None);

                _contextSummary.Text = result.Applied
                    ? result.Message + (result.RequiresReload
                        ? " This physical-window change takes effect next time the model is loaded; Virtual Context does not require a reload."
                        : " Virtual Context remains automatic above this window.")
                    : result.Message;
            }
            catch (Exception ex)
            {
                _contextSummary.Text = "Could not apply that physical context length: " + ex.Message;
            }
        }

        private async Task ClearContextLengthAsync()
        {
            if (string.IsNullOrWhiteSpace(_selectedModelPath)) return;
            try
            {
                ModelContextPreferenceResult result = await ModelRolesClient.Current
                    .ClearContextPreferenceAsync(_selectedModelPath!, CancellationToken.None);
                _contextSummary.Text = result.Message + " Virtual Context remains automatic.";
                await RefreshContextOptionsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _contextSummary.Text = "Could not reset this model's physical context: " + ex.Message;
            }
        }

        private static string DescribeLength(long tokens) =>
            tokens <= 0 ? "unknown"
            : tokens % 1024 == 0 ? (tokens / 1024).ToString() + "k tokens"
            : tokens.ToString("N0") + " tokens";

        private static string DescribeGigabytes(long bytes) =>
            bytes <= 0 ? "unknown" : (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " GB";
    }
}
