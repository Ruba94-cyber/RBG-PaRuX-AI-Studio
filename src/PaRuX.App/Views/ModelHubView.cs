using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// First-class local-model workspace. Text GGUF variants and compatible Image/Video/Edit
    /// checkpoints are presented in one place even though the backing runtimes differ. PaRuX
    /// never auto-downloads or auto-activates a model: transfers and runtime assignment remain
    /// separate explicit user actions.
    /// </summary>
    public sealed partial class ModelHubView : UserControl
    {
        private readonly TextBox _search = new TextBox { PlaceholderText = "Search Hugging Face GGUF models" };
        private readonly ComboBox _category = new ComboBox { MinWidth = 150 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _candidateUtilityStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        private readonly Button _candidateUtilityConfigureButton = new Button
        { Content = "Set comparison criteria", IsEnabled = false };
        private readonly Button _candidateUtilityResetButton = new Button
        { Content = "Reset criteria", IsEnabled = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock _hardwareSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        private readonly TextBlock _discoveryRefreshStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        private string? _discoveryRefreshPausedReason;
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly Border _capabilityAcquireCard = new Border
        {
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        private readonly TextBlock _capabilityAcquireTitle = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        private readonly TextBlock _capabilityAcquireReason = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        private readonly TextBlock _capabilityAcquireSize = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75
        };
        private readonly Button _capabilityAcquireDownloadButton = new Button
        {
            Content = CapabilityAcquisitionPresenter.DownloadButtonLabel,
            TabIndex = CapabilityAcquisitionPresenter.DownloadTabIndex,
            IsTabStop = true
        };
        private readonly Button _capabilityAcquireLaterButton = new Button
        {
            Content = CapabilityAcquisitionPresenter.LaterButtonLabel,
            TabIndex = CapabilityAcquisitionPresenter.LaterTabIndex,
            IsTabStop = true
        };
        private CapabilityAcquisitionProposal? _currentCapabilityAcquisitionProposal;
        private readonly StackPanel _recommended = new StackPanel { Spacing = 10 };
        private readonly StackPanel _results = new StackPanel { Spacing = 10 };
        private readonly StackPanel _media = new StackPanel { Spacing = 12 };
        private readonly StackPanel _downloads = new StackPanel { Spacing = 8 };
        private readonly StackPanel _installed = new StackPanel { Spacing = 8 };
        private readonly StackPanel _candidates = new StackPanel { Spacing = 10 };
        private StackPanel? _body;
        private StackPanel? _candidateActions;
        private StackPanel? _candidateFilterRow;
        private StackPanel? _candidateUtilityActions;
        private Border? _candidatesSection;
        private readonly StackPanel _suggestions = new StackPanel { Spacing = 10 };
        private Border? _suggestionsSection;
        private SuggestionFeedStatusDto? _maintenanceStatus;
        private bool _maintenanceUnavailable;
        private readonly HashSet<string> _suggestionDeferredKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _suggestionHiddenKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _suggestionBusyKeys = new HashSet<string>(StringComparer.Ordinal);
        private ModelCohortFilter _cohortFilter = ModelCohortFilter.All;
        private StackPanel? _cohortFilterRow;
        private Border? _recommendedSection;
        private TextBlock? _recommendedTitle;
        private Border? _searchSection;
        private Border? _mediaSection;
        private Border? _downloadsSection;
        private Border? _installedSection;
        private readonly List<UIElement> _debugFixtures = new List<UIElement>();
        private bool _isAdvancedMode;
        private readonly TextBox _candidateFilter = new TextBox
        { PlaceholderText = "Filter by repository or GGUF file", MaxLength = 256, MinWidth = 300 };
        private readonly TextBlock _candidateFilterCount = new TextBlock
        { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        private readonly TextBlock _explicitDiscoveryStatus = new TextBlock
        { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly Button _explicitDiscoveryButton = new Button { Content = "Check specific repositories" };
        private readonly List<ModelSearchResult> _lastSearch = new List<ModelSearchResult>();
        private readonly Dictionary<string, string> _candidateJobIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CandidateEvaluationJobStatus> _candidateJobStatuses = new Dictionary<string, CandidateEvaluationJobStatus>(StringComparer.Ordinal);
        private readonly HashSet<string> _candidateStatusRefreshFailures = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _candidateCancelInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _candidateDownloadInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _candidateDownloadUnknown = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _candidateIgnoreInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _candidateIgnoreResultMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _candidateNoticeDecisionInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _candidateNoticeResultMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        private const int CandidateNoticePageSize = 10;
        private int _candidateNoticeVisibleCount = CandidateNoticePageSize;
#if DEBUG
        private readonly HashSet<string> _candidateIgnoreFixtureIgnored = new HashSet<string>(StringComparer.Ordinal);
#endif
        private readonly Dictionary<string, long> _candidateUtilityCompareInFlight = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _candidateUtilityResultMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _candidateJobHistoryRefreshFailed;
        private CancellationTokenSource? _refreshCts;
        private int _refreshGeneration;
        private bool _refreshBusy;
        private CancellationTokenSource? _explicitDiscoveryCts;
        private long _explicitDiscoveryOperationId;
        private bool _explicitDiscoveryDialogOpen;
        private bool _catalogStale;
        private bool _refreshAfterCandidateIgnore;
        private CancellationTokenSource? _searchCts;
        private int _searchGeneration;
        private string? _firstModelHandoffRepoId;
        private string? _firstModelHandoffQuantHint;
        private RuntimeManagerHardwareStatus? _hardware;
        private AiStorageBudgetStatus? _storageBudget;
        private ModelDiscoveryCatalog? _discoveryCatalog;
        private CandidateUtilityPolicyStatus? _candidateUtilityPolicy;
        // Scoped recommendation (Chat / Standard / Balanced). Promote is offered only for a candidate whose latest
        // comparison met the configured margin while Core reports nothing blocks a change; Roll back only on the
        // current winner's card when Core reports a previous winner. Both always ask for confirmation.
        private CandidateRecommendationStatusResponse? _recommendationStatus;
        private readonly HashSet<string> _candidatePromotable = new HashSet<string>(StringComparer.Ordinal);
        private bool _recommendationTransitionInFlight;
        private bool _candidateUtilityPolicyUnavailable = true;
        private bool _candidateUtilityPolicyMutationInFlight;
        private long _candidateUtilityStateVersion;
        private long _candidateUtilityRequestId;
        private List<ManagedModelInfo> _managedModels = new List<ManagedModelInfo>();
        private List<ModelDownloadStatus> _downloadJobs = new List<ModelDownloadStatus>();
        private bool _candidateEvaluationStartInFlight;
#if DEBUG
        private bool _candidateStagingFixtureTransferAccepted;
#endif

        /// <summary>Drives the download queue while a transfer is running. Model Hub only refreshed
        /// when it was opened or when Refresh was pressed, so a multi-gigabyte download sat at
        /// whatever figures happened to be on screen when it started - indistinguishable, to the
        /// person waiting, from a download that had silently failed.</summary>
        private DispatcherTimer? _downloadPoll;
        private bool _pollInFlight;
        private DispatcherTimer? _candidateEvaluationPoll;
        private bool _candidateEvaluationPollInFlight;

        public ModelHubView()
        {
            AutomationProperties.SetAutomationId(_hardwareSummary, "ModelHub_HardwareSummary_Text");
            foreach (string value in new[] { "All categories", "general", "coding", "reasoning", "vision", "embedding", "reranker", "audio", "uncensored-roleplay", "abliterated" })
                _category.Items.Add(value);
            _category.SelectedIndex = 0;
            _category.SelectionChanged += (_, _) => RenderSearch(FilterSearch());
            _candidateFilter.TextChanged += (_, _) => RenderCandidates();

            _isAdvancedMode = SettingsStore.Current.Data.StudioModeEnabled;
            Content = BuildUi();
            ApplyInterfaceMode(_isAdvancedMode);
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAllAsync(); };
#if DEBUG
            Loaded += (_, _) =>
            {
                if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CAPABILITY_ACQUISITION"), "1", StringComparison.Ordinal))
                {
                    SetCapabilityAcquisitionProposal(new CapabilityAcquisitionProposal
                    {
                        Outcome = CapabilityAcquisitionOutcome.ProposeWithConsent,
                        Capability = "image editing",
                        ModelName = "instruct-pix2pix-gguf",
                        RepoId = "timbrooks/instruct-pix2pix",
                        FileName = "instruct-pix2pix.gguf",
                        DownloadSizeBytes = 1536L * 1024L * 1024L,
                        StorageImpactBytes = 2048L * 1024L * 1024L,
                        LicenceDecisionNeeded = false
                    });
                }
            };
#endif
            Unloaded += (_, _) =>
            {
                InvalidatePendingRefresh();
                _downloadPoll?.Stop();
                _candidateEvaluationPoll?.Stop();
            };
        }

        public async void OnShown()
        {
            _firstModelHandoffRepoId = null;
            _firstModelHandoffQuantHint = null;
            await RefreshAllAsync();
        }

        /// <summary>
        /// Opens Model Hub already searching for a specific repository. First-run recommends one
        /// model by name; landing the user on an empty search box after they pressed a button that
        /// said "get this model" would put them back where they started.
        /// </summary>
        public async void OnShownSearching(string repoId, string? quantizationHint = null)
        {
            if (string.IsNullOrWhiteSpace(repoId)) return;

#if DEBUG
            if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal)
                && repoId.StartsWith("fixture/", StringComparison.Ordinal))
            {
                InvalidatePendingRefresh();
                _search.Text = repoId;
                _status.Text = "UI fixture: specialist search received; no network or download was started.";
                return;
            }
#endif

            Task refreshTask = RefreshAllAsync();
            int generation = _refreshGeneration;
            await refreshTask;
            if (generation != _refreshGeneration) return;
            _firstModelHandoffRepoId = repoId.Trim();
            _firstModelHandoffQuantHint = quantizationHint?.Trim();
            _search.Text = repoId;
            await SearchAsync(generation);
        }

        private UIElement BuildUi()
        {
            var root = new Grid();
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            // The page is three tab panels under one header: Text models (everything that was here before), Media, Downloads.
            // "body" stays the Text panel so the existing section ordering code keeps working unchanged.
            var outer = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(24),
                MaxWidth = 1180,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var body = new StackPanel { Spacing = 18 };
            scroll.Content = outer;
            root.Children.Add(scroll);

            outer.Children.Add(new TextBlock { Text = "Model Hub", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            outer.Children.Add(new TextBlock
            {
                Text = "Choose the right local model, not just a file. Compare GGUF quantizations against this PC's real GPU/RAM capacity, manage resumable downloads, inspect installed performance and select compatible Image / Video / Edit checkpoints from one workspace.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            // Non-modal "Tuning for your PC" status: the running job, or the latest result for a few minutes (text and media).
            outer.Children.Add(new ModelTuningStatusControl(ModelTuningStatusControl.Mode.Hub));

            var searchRow = new Grid { ColumnSpacing = 8 };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AutomationProperties.SetAutomationId(_search, "ModelHub_Search_TextBox");
            Grid.SetColumn(_search, 0); searchRow.Children.Add(_search);
            AutomationProperties.SetAutomationId(_category, "ModelHub_Category_ComboBox");
            AutomationProperties.SetName(_category, "Model category");
            Grid.SetColumn(_category, 1); searchRow.Children.Add(_category);
            var searchButton = new Button { Content = "Search" };
            AutomationProperties.SetAutomationId(searchButton, "ModelHub_Search_Button");
            searchButton.Click += async (_, _) => await SearchAsync();
            Grid.SetColumn(searchButton, 2); searchRow.Children.Add(searchButton);
            var refreshButton = new Button { Content = "Refresh" };
            AutomationProperties.SetAutomationId(refreshButton, "ModelHub_Refresh_Button");
            refreshButton.Click += async (_, _) =>
            {
                // Only a user-driven reload supersedes the explicit check; automatic refreshes do not.
                InvalidateExplicitDiscovery();
                await RefreshAllAsync();
            };
            Grid.SetColumn(refreshButton, 3); searchRow.Children.Add(refreshButton);
            body.Children.Add(searchRow);
            AutomationProperties.SetAutomationId(_discoveryRefreshStatus,
                "ModelHub_DiscoveryRefresh_Status_Text");
            body.Children.Add(_discoveryRefreshStatus);
            AutomationProperties.SetAutomationId(_status, "ModelHub_Status_Text");
            body.Children.Add(_hardwareSummary);

            _suggestionsSection = Section("SUGGESTED FOR THIS PC · CANDIDATES, NOT VALIDATED", _suggestions);
            AutomationProperties.SetAutomationId(_suggestionsSection, ModelSuggestionPresenter.SectionAutomationId);
            body.Children.Add(_suggestionsSection);
            RenderSuggestions();

            var cohortRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cohortRow.Children.Add(new TextBlock
            {
                Text = "Discovery feed:",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75
            });
            foreach (ModelCohortFilter option in Enum.GetValues<ModelCohortFilter>())
            {
                var chip = new ToggleButton
                {
                    Content = ModelCohortFilterPresenter.Label(option),
                    IsChecked = option == _cohortFilter,
                    Tag = option
                };
                AutomationProperties.SetAutomationId(chip, ModelCohortFilterPresenter.AutomationId(option));
                chip.Click += (_, _) =>
                {
                    _cohortFilter = option;
                    foreach (UIElement child in cohortRow.Children)
                        if (child is ToggleButton toggle && toggle.Tag is ModelCohortFilter tagged)
                            toggle.IsChecked = tagged == _cohortFilter;
                    RenderCandidates();
                };
                cohortRow.Children.Add(chip);
            }
            _cohortFilterRow = cohortRow;
            body.Children.Add(cohortRow);

            var candidateActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            var refreshCandidates = new Button { Content = "Refresh candidates" };
            AutomationProperties.SetAutomationId(refreshCandidates, "ModelHub_CandidateRefreshButton");
            refreshCandidates.Click += async (_, _) => await RefreshCandidateCatalogAsync();
            candidateActions.Children.Add(refreshCandidates);
            AutomationProperties.SetAutomationId(_explicitDiscoveryButton,
                "ModelHub_CandidateExplicitRefresh_Button");
            _explicitDiscoveryButton.Click += async (_, _) => await CheckSpecificRepositoriesAsync();
            candidateActions.Children.Add(_explicitDiscoveryButton);
            candidateActions.Children.Add(new TextBlock
            {
                Text = "Publisher metadata is unverified. Evaluation does not install, recommend, promote, or change routing.",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75
            });
            _candidateActions = candidateActions;
            body.Children.Add(candidateActions);
            AutomationProperties.SetAutomationId(_explicitDiscoveryStatus,
                "ModelHub_CandidateExplicitRefresh_Status_Text");
            body.Children.Add(_explicitDiscoveryStatus);
            var candidateFilterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            AutomationProperties.SetAutomationId(_candidateFilter, "ModelHub_CandidateFilter_TextBox");
            candidateFilterRow.Children.Add(_candidateFilter);
            var clearCandidateFilter = new Button { Content = "Clear filter" };
            AutomationProperties.SetAutomationId(clearCandidateFilter, "ModelHub_CandidateFilter_Clear_Button");
            clearCandidateFilter.Click += (_, _) => _candidateFilter.Text = string.Empty;
            candidateFilterRow.Children.Add(clearCandidateFilter);
            AutomationProperties.SetAutomationId(_candidateFilterCount, "ModelHub_CandidateFilter_Count_Text");
            candidateFilterRow.Children.Add(_candidateFilterCount);
            _candidateFilterRow = candidateFilterRow;
            body.Children.Add(candidateFilterRow);
            var candidateUtilityActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            AutomationProperties.SetAutomationId(_candidateUtilityConfigureButton,
                "ModelHub_CandidateUtilityConfigure_Button");
            _candidateUtilityConfigureButton.Click += async (_, _) =>
                await ConfigureCandidateUtilityPolicyAsync();
            candidateUtilityActions.Children.Add(_candidateUtilityConfigureButton);
            AutomationProperties.SetAutomationId(_candidateUtilityResetButton,
                "ModelHub_CandidateUtilityReset_Button");
            _candidateUtilityResetButton.Click += async (_, _) =>
                await ResetCandidateUtilityPolicyAsync();
            candidateUtilityActions.Children.Add(_candidateUtilityResetButton);
            _candidateUtilityActions = candidateUtilityActions;
            body.Children.Add(candidateUtilityActions);
            AutomationProperties.SetAutomationId(_candidateUtilityStatus, "ModelHub_CandidateUtilityPolicyStatus_Text");
            body.Children.Add(_candidateUtilityStatus);
            _candidatesSection = Section("DISCOVERED CANDIDATES · NOT RECOMMENDATIONS", _candidates);
            body.Children.Add(_candidatesSection);

#if DEBUG
            if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal))
            {
                var consentFixture = new Button { Content = "UI test: model licence review" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(consentFixture, "ModelHub_TestLicenseConsent_Button");
                consentFixture.Click += async (_, _) =>
                {
                    string? dataRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
                    if (string.IsNullOrWhiteSpace(dataRoot)) return;
                    string callbackMarker = System.IO.Path.Combine(dataRoot, "model-license-download-callback.txt");
                    if (System.IO.File.Exists(callbackMarker)) System.IO.File.Delete(callbackMarker);
                    string revision = new string('d', 40);
                    bool started = await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                        XamlRoot,
                        "parux-ui-test/license-review-fixture",
                        "fixture-model.gguf",
                        ModelLicenseConsentPolicy.Evaluate("custom", metadataRetrieved: true),
                        revision,
                        startDownload: (selectedRevision, licenseEvidence) =>
                        {
                            var request = new ModelDownloadRequest
                            {
                                RepoId = "parux-ui-test/license-review-fixture",
                                FileName = "fixture-model.gguf",
                                DownloadUrl = "https://huggingface.co/parux-ui-test/license-review-fixture/resolve/main/fixture-model.gguf",
                                LicenseEvidence = licenseEvidence
                            };
                            ModelDownloadClient.PinRequestToRevision(request, selectedRevision);
                            System.IO.File.WriteAllText(callbackMarker,
                                "download callback reached\n" + request.Revision + "\n" + request.DownloadUrl
                                + "\n" + request.LicenseEvidence?.MetadataStatus
                                + "\n" + request.LicenseEvidence?.PublisherDeclaredLicense
                                + "\n" + request.LicenseEvidence?.MetadataObservedUtc?.Kind
                                + "\n" + request.LicenseEvidence?.ReviewRequired
                                + "\n" + request.LicenseEvidence?.ReviewAcknowledged
                                + "\n" + request.LicenseEvidence?.ReviewAcknowledgedUtc?.Kind);
                            return Task.CompletedTask;
                        });
                    _status.Text = started ? "Test download callback reached." : "Test download was cancelled.";
                };
                AddDebugFixture(body, consentFixture);

                var missingRevisionFixture = new Button { Content = "UI test: missing model revision guard" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    missingRevisionFixture, "ModelHub_TestMissingRevision_Button");
                missingRevisionFixture.Click += async (_, _) =>
                {
                    string? dataRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
                    if (string.IsNullOrWhiteSpace(dataRoot)) return;
                    string callbackMarker = System.IO.Path.Combine(dataRoot, "model-license-download-callback.txt");
                    if (System.IO.File.Exists(callbackMarker)) System.IO.File.Delete(callbackMarker);
                    await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                        XamlRoot,
                        "parux-ui-test/license-review-fixture",
                        "fixture-model.gguf",
                        ModelLicenseConsentPolicy.Evaluate("custom", metadataRetrieved: true),
                        revision: null,
                        startDownload: (_, _) =>
                        {
                            System.IO.File.WriteAllText(callbackMarker, "unexpected download callback");
                            return Task.CompletedTask;
                        });
                };
                AddDebugFixture(body, missingRevisionFixture);

                var allowListFixture = new Button { Content = "UI test: allow-listed model evidence" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                    allowListFixture, "ModelHub_TestAllowListedLicenseEvidence_Button");
                allowListFixture.Click += async (_, _) =>
                {
                    string? dataRoot = Environment.GetEnvironmentVariable("PARUX_DATA_ROOT");
                    if (string.IsNullOrWhiteSpace(dataRoot)) return;
                    string callbackMarker = System.IO.Path.Combine(dataRoot, "model-license-download-callback.txt");
                    if (System.IO.File.Exists(callbackMarker)) System.IO.File.Delete(callbackMarker);
                    await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                        XamlRoot,
                        "parux-ui-test/license-review-fixture",
                        "fixture-model.gguf",
                        ModelLicenseConsentPolicy.Evaluate("MIT", metadataRetrieved: true),
                        new string('e', 40),
                        startDownload: (_, evidence) =>
                        {
                            System.IO.File.WriteAllText(callbackMarker,
                                evidence.MetadataStatus + "\n" + evidence.PublisherDeclaredLicense + "\n"
                                + evidence.ReviewRequired + "\n" + evidence.ReviewAcknowledged);
                            return Task.CompletedTask;
                        });
                    _status.Text = "Test download callback reached.";
                };
                AddDebugFixture(body, allowListFixture);

                if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_EVALUATION"), "1", StringComparison.Ordinal))
                {
                    var sharedMemoryHardwareFixture = new Button { Content = "UI test: shared-memory GPU profile" };
                    AutomationProperties.SetAutomationId(sharedMemoryHardwareFixture, "ModelHub_TestSharedMemoryHardwareFixture_Button");
                    sharedMemoryHardwareFixture.Click += (_, _) => SimulateSharedMemoryHardwareFixture();
                    AddDebugFixture(body, sharedMemoryHardwareFixture);

                    var ineligibleGpuHardwareFixture = new Button { Content = "UI test: detected GPU without text backend" };
                    AutomationProperties.SetAutomationId(ineligibleGpuHardwareFixture, "ModelHub_TestIneligibleGpuHardwareFixture_Button");
                    ineligibleGpuHardwareFixture.Click += (_, _) => SimulateIneligibleGpuHardwareFixture();
                    AddDebugFixture(body, ineligibleGpuHardwareFixture);

                    var candidateEvaluationFixture = new Button { Content = "UI test: Candidate evaluation flow" };
                    AutomationProperties.SetAutomationId(candidateEvaluationFixture, "ModelHub_TestCandidateEvaluationFixture_Button");
                    candidateEvaluationFixture.Click += async (_, _) => await LoadCandidateEvaluationUiFixtureAsync();
                    AddDebugFixture(body, candidateEvaluationFixture);

                    var candidateNoticeOverflowFixture = new Button { Content = "UI test: Candidate notice overflow" };
                    AutomationProperties.SetAutomationId(candidateNoticeOverflowFixture, "ModelHub_TestCandidateNoticeOverflowFixture_Button");
                    candidateNoticeOverflowFixture.Click += (_, _) => LoadCandidateNoticeOverflowUiFixture();
                    AddDebugFixture(body, candidateNoticeOverflowFixture);

                    var candidateUtilityPolicyFixture = new Button { Content = "UI test: explicit Candidate policy readiness" };
                    AutomationProperties.SetAutomationId(candidateUtilityPolicyFixture, "ModelHub_TestCandidateUtilityPolicyReady_Button");
                    candidateUtilityPolicyFixture.Click += (_, _) => SimulateCandidateUtilityPolicyReadyFixture();
                    AddDebugFixture(body, candidateUtilityPolicyFixture);

                    if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_UTILITY_POLICY"), "1", StringComparison.Ordinal))
                    {
                        var candidateUtilityConfigureFixture = new Button { Content = "UI test: open criteria editor with synthetic scope" };
                        AutomationProperties.SetAutomationId(candidateUtilityConfigureFixture,
                            "ModelHub_TestCandidateUtilityConfigureFixture_Button");
                        candidateUtilityConfigureFixture.Click += async (_, _) =>
                            await ConfigureCandidateUtilityPolicyAsync(useTestContextFixture: true);
                        AddDebugFixture(body, candidateUtilityConfigureFixture);
                    }

                    var candidateUtilityInFlightFixture = new Button { Content = "UI test: Candidate comparison in progress" };
                    AutomationProperties.SetAutomationId(candidateUtilityInFlightFixture, "ModelHub_TestCandidateUtilityInFlight_Button");
                    candidateUtilityInFlightFixture.Click += (_, _) => SimulateCandidateUtilityComparisonInFlightFixture();
                    AddDebugFixture(body, candidateUtilityInFlightFixture);

                    var candidateFitUnknownStorageFixture = new Button { Content = "UI test: unknown Candidate quota snapshot" };
                    AutomationProperties.SetAutomationId(candidateFitUnknownStorageFixture, "ModelHub_TestCandidateFitUnknownStorageFixture_Button");
                    candidateFitUnknownStorageFixture.Click += (_, _) => SimulateCandidateFitUnknownStorageFixture();
                    AddDebugFixture(body, candidateFitUnknownStorageFixture);

                    var candidateFitUnknownSizeFixture = new Button { Content = "UI test: unknown Candidate file size" };
                    AutomationProperties.SetAutomationId(candidateFitUnknownSizeFixture, "ModelHub_TestCandidateFitUnknownSizeFixture_Button");
                    candidateFitUnknownSizeFixture.Click += (_, _) => SimulateCandidateFitUnknownSizeFixture();
                    AddDebugFixture(body, candidateFitUnknownSizeFixture);

                    var candidateFitEligibleHistoryFixture = new Button { Content = "UI test: historical Candidate eligibility" };
                    AutomationProperties.SetAutomationId(candidateFitEligibleHistoryFixture, "ModelHub_TestCandidateFitEligibleHistoryFixture_Button");
                    candidateFitEligibleHistoryFixture.Click += (_, _) => SimulateCandidateFitEligibleHistoryFixture();
                    AddDebugFixture(body, candidateFitEligibleHistoryFixture);

                    var recommendationPromotableFixture = new Button { Content = "UI test: promotable recommendation" };
                    AutomationProperties.SetAutomationId(recommendationPromotableFixture, "ModelHub_TestRecommendationPromotableFixture_Button");
                    recommendationPromotableFixture.Click += (_, _) => SimulateRecommendationFixture(canPromote: true, canRollback: false);
                    AddDebugFixture(body, recommendationPromotableFixture);

                    var recommendationRollbackFixture = new Button { Content = "UI test: rollback available" };
                    AutomationProperties.SetAutomationId(recommendationRollbackFixture, "ModelHub_TestRecommendationRollbackFixture_Button");
                    recommendationRollbackFixture.Click += (_, _) => SimulateRecommendationFixture(canPromote: false, canRollback: true);
                    AddDebugFixture(body, recommendationRollbackFixture);

                    var recommendationBlockedFixture = new Button { Content = "UI test: recommendation blocked by a pin" };
                    AutomationProperties.SetAutomationId(recommendationBlockedFixture, "ModelHub_TestRecommendationBlockedFixture_Button");
                    recommendationBlockedFixture.Click += (_, _) => SimulateRecommendationFixture(canPromote: false, canRollback: false);
                    AddDebugFixture(body, recommendationBlockedFixture);

                    var candidateFitAllowlistedLicenseFixture = new Button { Content = "UI test: allow-listed Candidate licence" };
                    AutomationProperties.SetAutomationId(candidateFitAllowlistedLicenseFixture, "ModelHub_TestCandidateFitAllowlistedLicenseFixture_Button");
                    candidateFitAllowlistedLicenseFixture.Click += (_, _) => SimulateCandidateFitAllowlistedLicenseFixture();
                    AddDebugFixture(body, candidateFitAllowlistedLicenseFixture);

                    var staleStatusFixture = new Button { Content = "UI test: stale Candidate status response" };
                    AutomationProperties.SetAutomationId(staleStatusFixture, "ModelHub_TestCandidateStaleStatusFixture_Button");
                    staleStatusFixture.Click += async (_, _) => await SimulateStaleCandidateStatusFixtureAsync();
                    AddDebugFixture(body, staleStatusFixture);

                    var uncertainCandidateEvaluationFixture = new Button { Content = "UI test: uncertain Candidate evaluation start" };
                    AutomationProperties.SetAutomationId(uncertainCandidateEvaluationFixture, "ModelHub_TestCandidateUncertainEvaluationFixture_Button");
                    uncertainCandidateEvaluationFixture.Click += (_, _) => SimulateUncertainCandidateEvaluationStartFixture('a');
                    AddDebugFixture(body, uncertainCandidateEvaluationFixture);

                    var interruptedCandidateEvaluationFixture = new Button { Content = "UI test: recovered interrupted Candidate evaluation" };
                    AutomationProperties.SetAutomationId(interruptedCandidateEvaluationFixture, "ModelHub_TestCandidateInterruptedFixture_Button");
                    interruptedCandidateEvaluationFixture.Click += (_, _) => SimulateInterruptedCandidateEvaluationFixture();
                    AddDebugFixture(body, interruptedCandidateEvaluationFixture);

                    var candidateHistoryReconciliationFixture = new Button { Content = "UI test: reconcile uncertain Candidate start from history" };
                    AutomationProperties.SetAutomationId(candidateHistoryReconciliationFixture, "ModelHub_TestCandidateHistoryReconciliationFixture_Button");
                    candidateHistoryReconciliationFixture.Click += (_, _) => SimulateCandidateHistoryReconciliationFixture();
                    AddDebugFixture(body, candidateHistoryReconciliationFixture);
                }

                if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_STAGING"), "1", StringComparison.Ordinal))
                {
                    var candidateStagingFixture = new Button { Content = "UI test: stage Candidate with consent" };
                    AutomationProperties.SetAutomationId(candidateStagingFixture, "ModelHub_TestCandidateStagingFixture_Button");
                    candidateStagingFixture.Click += (_, _) => LoadCandidateStagingUiFixture();
                    AddDebugFixture(body, candidateStagingFixture);

                    var tamperedCandidateFixture = new Button { Content = "UI test: tampered Candidate source" };
                    AutomationProperties.SetAutomationId(tamperedCandidateFixture, "ModelHub_TestCandidateTamperedSourceFixture_Button");
                    tamperedCandidateFixture.Click += (_, _) => LoadCandidateStagingUiFixture(tamperPinnedUrl: true);
                    AddDebugFixture(body, tamperedCandidateFixture);

                    var uncertainUnmanagedCandidateFixture = new Button { Content = "UI test: uncertain evaluation without managed file" };
                    AutomationProperties.SetAutomationId(uncertainUnmanagedCandidateFixture, "ModelHub_TestUnmanagedCandidateUncertainEvaluationFixture_Button");
                    uncertainUnmanagedCandidateFixture.Click += (_, _) => SimulateUncertainCandidateEvaluationStartFixture('d');
                    AddDebugFixture(body, uncertainUnmanagedCandidateFixture);

                }
            }
#endif

            // On-demand capability acquisition card (BIC-16, Simple mode)
            AutomationProperties.SetAutomationId(_capabilityAcquireCard, "ModelHub_CapabilityAcquire_Card");
            AutomationProperties.SetName(_capabilityAcquireCard, "Capability Acquisition");

            AutomationProperties.SetAutomationId(_capabilityAcquireDownloadButton, "ModelHub_CapabilityAcquire_Download_Button");
            AutomationProperties.SetName(_capabilityAcquireDownloadButton, CapabilityAcquisitionPresenter.DownloadButtonLabel);

            AutomationProperties.SetAutomationId(_capabilityAcquireLaterButton, "ModelHub_CapabilityAcquire_Later_Button");
            AutomationProperties.SetName(_capabilityAcquireLaterButton, CapabilityAcquisitionPresenter.LaterButtonLabel);

            var capabilityCardContent = new StackPanel { Spacing = 8 };
            capabilityCardContent.Children.Add(_capabilityAcquireTitle);
            capabilityCardContent.Children.Add(_capabilityAcquireReason);
            capabilityCardContent.Children.Add(_capabilityAcquireSize);

            var capabilityButtonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            capabilityButtonRow.Children.Add(_capabilityAcquireDownloadButton);
            capabilityButtonRow.Children.Add(_capabilityAcquireLaterButton);
            capabilityCardContent.Children.Add(capabilityButtonRow);

            _capabilityAcquireCard.Child = capabilityCardContent;

            _capabilityAcquireDownloadButton.Click += async (_, _) => await OnCapabilityAcquireDownloadClickedAsync();
            _capabilityAcquireLaterButton.Click += (_, _) => DismissCapabilityAcquireCard();

            // Keyboard: Escape closes card without action
            _capabilityAcquireCard.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    DismissCapabilityAcquireCard();
                }
            };
            _capabilityAcquireDownloadButton.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    DismissCapabilityAcquireCard();
                }
            };
            _capabilityAcquireLaterButton.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    DismissCapabilityAcquireCard();
                }
            };

#if DEBUG
            if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TESTS"), "1", StringComparison.Ordinal))
            {
                var capabilityAcquireFixture = new Button { Content = "UI test: capability acquisition proposal" };
                AutomationProperties.SetAutomationId(capabilityAcquireFixture, "ModelHub_TestCapabilityAcquireFixture_Button");
                capabilityAcquireFixture.Click += (_, _) =>
                {
                    SetCapabilityAcquisitionProposal(new CapabilityAcquisitionProposal
                    {
                        Outcome = CapabilityAcquisitionOutcome.ProposeWithConsent,
                        Capability = "image editing",
                        ModelName = "instruct-pix2pix-gguf",
                        RepoId = "timbrooks/instruct-pix2pix",
                        FileName = "instruct-pix2pix.gguf",
                        DownloadSizeBytes = 1536L * 1024L * 1024L,
                        StorageImpactBytes = 2048L * 1024L * 1024L,
                        LicenceDecisionNeeded = false
                    });
                };
                body.Children.Add(capabilityAcquireFixture);
            }
#endif

            body.Children.Add(_capabilityAcquireCard);
            // Kept so ApplyInterfaceMode can retitle and reorder them for Simple vs Advanced.
            _body = body;
            _recommendedSection = Section("RECOMMENDED TEXT MODELS", _recommended);
            _recommendedTitle = (TextBlock)((StackPanel)_recommendedSection.Child).Children[0];
            _searchSection = Section("SEARCH / COMPARE GGUF", _results);
            _mediaSection = Section("MEDIA MODELS · IMAGE / VIDEO / EDIT", _media);
            _downloadsSection = Section("DOWNLOAD QUEUE", _downloads);
            _installedSection = Section("INSTALLED TEXT MODEL LIBRARY", _installed);
            body.Children.Add(_recommendedSection);
            body.Children.Add(_searchSection);
            body.Children.Add(_installedSection);
            BuildHubTabs(outer, body);
            return root;
        }

        private static Border Section(string title, Panel content)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            panel.Children.Add(content);
            return new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel };
        }

        public void ApplyInterfaceMode(bool advanced)
        {
            _isAdvancedMode = advanced;
            RenderSuggestions();
            if (_recommendedTitle != null)
            {
                _recommendedTitle.Text = ModelHubSimpleModePresenter.GetRecommendedSectionTitle(advanced);
            }

            Visibility advancedVisibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            if (_candidateActions != null) _candidateActions.Visibility = advancedVisibility;
            if (_cohortFilterRow != null) _cohortFilterRow.Visibility = advancedVisibility;
            _explicitDiscoveryStatus.Visibility = advancedVisibility;
            if (_candidateFilterRow != null) _candidateFilterRow.Visibility = advancedVisibility;
            if (_candidateUtilityActions != null) _candidateUtilityActions.Visibility = advancedVisibility;
            _candidateUtilityStatus.Visibility = advancedVisibility;
            if (_candidatesSection != null) _candidatesSection.Visibility = advancedVisibility;

            ReorderSections(advanced);
        }

        private void ReorderSections(bool advanced)
        {
            if (_body == null || _recommendedSection == null || _candidatesSection == null
                || _searchSection == null || _mediaSection == null || _downloadsSection == null
                || _installedSection == null || _candidateActions == null || _candidateFilterRow == null
                || _candidateUtilityActions == null)
            {
                return;
            }

            var elementsToMove = new List<UIElement>
            {
                _candidateActions,
                _explicitDiscoveryStatus,
                _candidateFilterRow,
                _candidateUtilityActions,
                _candidateUtilityStatus,
                _candidatesSection
            };
            if (_suggestionsSection != null) elementsToMove.Add(_suggestionsSection);
            if (_cohortFilterRow != null) elementsToMove.Add(_cohortFilterRow);
            elementsToMove.AddRange(_debugFixtures);
            elementsToMove.Add(_recommendedSection);
            elementsToMove.Add(_searchSection);
            elementsToMove.Add(_installedSection);

            foreach (var elem in elementsToMove)
            {
                _body.Children.Remove(elem);
            }

            if (_suggestionsSection != null) _body.Children.Add(_suggestionsSection);
            if (advanced)
            {
                if (_cohortFilterRow != null) _body.Children.Add(_cohortFilterRow);
                _body.Children.Add(_candidateActions);
                _body.Children.Add(_explicitDiscoveryStatus);
                _body.Children.Add(_candidateFilterRow);
                _body.Children.Add(_candidateUtilityActions);
                _body.Children.Add(_candidateUtilityStatus);
                _body.Children.Add(_candidatesSection);
                foreach (var debugFixture in _debugFixtures)
                {
                    _body.Children.Add(debugFixture);
                }
                _body.Children.Add(_recommendedSection);
                _body.Children.Add(_searchSection);
                _body.Children.Add(_installedSection);
            }
            else
            {
                _body.Children.Add(_recommendedSection);
                _body.Children.Add(_searchSection);
                _body.Children.Add(_installedSection);
                if (_cohortFilterRow != null) _body.Children.Add(_cohortFilterRow);
                _body.Children.Add(_candidateActions);
                _body.Children.Add(_explicitDiscoveryStatus);
                _body.Children.Add(_candidateFilterRow);
                _body.Children.Add(_candidateUtilityActions);
                _body.Children.Add(_candidateUtilityStatus);
                _body.Children.Add(_candidatesSection);
                foreach (var debugFixture in _debugFixtures)
                {
                    _body.Children.Add(debugFixture);
                }
            }
        }

        private void AddDebugFixture(Panel body, UIElement fixture)
        {
            _debugFixtures.Add(fixture);
            body.Children.Add(fixture);
        }

        private async Task RefreshAllAsync()
        {
            if (_candidateIgnoreInFlight.Count > 0)
            {
                _refreshAfterCandidateIgnore = true;
                return;
            }

            int generation = ++_refreshGeneration;
            InvalidatePendingSearch();
            _refreshBusy = true;
            InvalidateCandidateUtilityComparisons();
            _candidateUtilityPolicyUnavailable = true;
            RenderCandidates();
            RenderCandidateUtilityPolicyStatus();
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = new CancellationTokenSource();
            CancellationToken ct = _refreshCts.Token;
            SetBusy(true, "Refreshing Model Hub…");
            try
            {
                Task<List<RecommendedModel>> recommendedTask = ModelDownloadClient.Current.GetRecommendedAsync(ct);
                Task<List<ModelDownloadStatus>> downloadsTask = ModelDownloadClient.Current.ListDownloadsAsync(ct);
                Task<List<ManagedModelInfo>> installedTask = ModelDownloadClient.Current.ListManagedModelsAsync(ct);
                Task<List<MediaCheckpointStatus>> mediaTask = ModelDownloadClient.Current.ListMediaCheckpointsAsync(ct);
                Task<List<RecommendedCheckpoint>> mediaRecommendedTask = ModelDownloadClient.Current.GetRecommendedMediaCheckpointsAsync(ct);
                Task<MediaCatalogueResponse?> mediaCatalogueTask = TryGetMediaCatalogueAsync(ct);
                Task<RuntimeManagerHardwareStatus?> hardwareTask = TryGetHardwareAsync(ct);
                Task<AiStorageBudgetStatus?> storageBudgetTask = TryGetStorageBudgetAsync(ct);
                Task<ModelDiscoveryCatalog?> discoveryTask = TryGetDiscoveryCatalogAsync(ct);
                Task<List<CandidateEvaluationJobStatus>?> candidateJobHistoryTask = TryGetCandidateJobHistoryAsync(ct);
                Task<CandidateUtilityPolicyStatus?> candidateUtilityPolicyTask = TryGetCandidateUtilityPolicyAsync(ct);
                Task<CandidateRecommendationStatusResponse?> recommendationStatusTask = TryGetRecommendationStatusAsync(ct);
                Task<List<ManagedModelInfo>?> incompleteSetsTask = TryGetIncompleteSplitSetsAsync(ct);
                Task<SuggestionFeedStatusDto?> maintenanceTask = TryGetMaintenanceStatusAsync(ct);
                await Task.WhenAll(maintenanceTask, recommendedTask, downloadsTask, installedTask, mediaTask, mediaRecommendedTask, mediaCatalogueTask,
                    hardwareTask, storageBudgetTask, discoveryTask, candidateJobHistoryTask, candidateUtilityPolicyTask,
                    recommendationStatusTask, incompleteSetsTask);
                // A client can complete after cancellation (or ignore its token entirely). Only
                // the most recent refresh may publish state into the view.
                if (generation != _refreshGeneration || ct.IsCancellationRequested) return;

                if (candidateJobHistoryTask.Result is { } candidateJobHistory)
                {
                    ReconcileCandidateJobHistory(candidateJobHistory);
                    _candidateJobHistoryRefreshFailed = false;
                }
                else _candidateJobHistoryRefreshFailed = true;

                _hardware = hardwareTask.Result;
                _storageBudget = storageBudgetTask.Result;
                // A failed optional catalog fetch must not erase cards already inspected locally, but
                // the view must say that those cards are earlier data rather than a fresh catalog.
                if (discoveryTask.Result is { } refreshedCatalog)
                {
                    _discoveryCatalog = refreshedCatalog;
                    _catalogStale = false;
                }
                else if (_discoveryCatalog != null) _catalogStale = true;
                _candidateUtilityPolicy = candidateUtilityPolicyTask.Result;
                _candidateUtilityPolicyUnavailable = candidateUtilityPolicyTask.Result == null;
                _recommendationStatus = recommendationStatusTask.Result; // null = unknown: no promote/rollback is offered
                _candidatePromotable.Clear();
                _candidateUtilityResultMessages.Clear();
                _managedModels = installedTask.Result;
                _downloadJobs = downloadsTask.Result;
                _candidateDownloadUnknown.Clear();
                RenderHardwareSummary();
                _maintenanceStatus = maintenanceTask.Result;
                _maintenanceUnavailable = maintenanceTask.Result == null;
                RenderSuggestions();
                RenderRecommended(recommendedTask.Result);
                RenderDownloads(downloadsTask.Result);
                RenderInstalled(installedTask.Result);
                RenderIncompleteSplitSets(incompleteSetsTask.Result);
                RenderCandidates();
                RenderCandidateUtilityPolicyStatus();
                _mediaCatalogue = mediaCatalogueTask.Result ?? _mediaCatalogue;
                RenderMedia(mediaTask.Result, mediaRecommendedTask.Result, downloadsTask.Result);
                RenderHuggingFaceRows(downloadsTask.Result);
                RenderDiscoveryRefreshStatus();
                _status.Text = installedTask.Result.Count + " PaRuX-managed GGUF model(s) (models you linked from elsewhere are listed in Settings > Models) · "
                    + mediaTask.Result.Count + " media role(s) · " + downloadsTask.Result.Count + " download job(s) · "
                    + (_discoveryCatalog?.Candidates.Count ?? 0) + " discovered Candidate(s).";
                if (_catalogStale) _status.Text += " · " + StaleCatalogMessage;
                ShowActiveTransfer(downloadsTask.Result);
                if (_candidateJobHistoryRefreshFailed && !downloadsTask.Result.Any(IsActive))
                    _status.Text += " · Candidate evaluation history unavailable; local state was preserved.";
                UpdateDownloadPolling(downloadsTask.Result);
                UpdateCandidateEvaluationPolling();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation == _refreshGeneration)
                    _status.Text = "Model Hub refresh failed: " + ex.Message;
            }
            finally
            {
                if (generation == _refreshGeneration)
                {
                    _refreshBusy = false;
                    SetBusy(false, _status.Text);
                }
            }
        }

        private void InvalidatePendingRefresh()
        {
            ++_refreshGeneration;
            InvalidateExplicitDiscovery();
            _refreshCts?.Cancel();
            InvalidatePendingSearch();
            _refreshBusy = false;
            SetBusy(false, _status.Text);
        }

        private void InvalidateExplicitDiscovery()
        {
            ++_explicitDiscoveryOperationId;
            // The running operation owns (and disposes) its source; this field is only a cancel
            // handle, so clear it here to avoid cancelling an already-disposed source later.
            CancellationTokenSource? running = _explicitDiscoveryCts;
            _explicitDiscoveryCts = null;
            if (running != null)
            {
                running.Cancel();
                _explicitDiscoveryStatus.Text = "Specific repository check was interrupted before its outcome was shown. "
                    + "Existing cards remain; no model action was taken.";
            }
            _explicitDiscoveryButton.IsEnabled = true;
        }

        private void InvalidatePendingSearch()
        {
            ++_searchGeneration;
            _searchCts?.Cancel();
        }

        private static bool IsActive(ModelDownloadStatus job) => ModelDownloadStatusPolicy.IsActive(job.Status);

        /// <summary>Puts the running transfer in the one line that is always on screen. The queue
        /// itself sits further down the page, which is no help to somebody who has just pressed
        /// Download and is looking for any sign that something is happening.</summary>
        private void ShowActiveTransfer(IReadOnlyList<ModelDownloadStatus> jobs)
        {
            ModelDownloadStatus? active = jobs.FirstOrDefault(IsActive);
            if (active == null) return;

            string detail = active.TotalBytes > 0
                ? FormatBytes(active.BytesDownloaded) + " / " + FormatBytes(active.TotalBytes)
                  + " (" + (active.BytesDownloaded * 100 / active.TotalBytes) + "%)"
                : FormatBytes(active.BytesDownloaded) + " so far";

            string state = active.Status switch
            {
                "queued" => "Queued: ",
                "retrying" => "Retrying ",
                _ => "Downloading "
            };
            _status.Text = state
                + active.FileName + " · " + detail
                + (jobs.Count(IsActive) > 1 ? " · " + (jobs.Count(IsActive) - 1) + " more active" : string.Empty);
            string transferDetails = ModelDownloadPresentationPolicy.FormatDetails(active);
            if (transferDetails.Length > 0) _status.Text += " · " + transferDetails;
        }

        private void UpdateDownloadPolling(IReadOnlyList<ModelDownloadStatus> jobs)
        {
            if (!jobs.Any(IsActive))
            {
                _downloadPoll?.Stop();
                return;
            }

            if (_downloadPoll == null)
            {
                _downloadPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _downloadPoll.Tick += async (_, _) => await PollDownloadsAsync();
            }
            if (!_downloadPoll.IsEnabled) _downloadPoll.Start();
        }

        /// <summary>Queue-only refresh: no busy indicator and no hardware/search round trips, so a
        /// transfer can be watched without the whole workspace flickering every two seconds.</summary>
        private async Task PollDownloadsAsync()
        {
            if (_pollInFlight) return;
            _pollInFlight = true;
            try
            {
                List<ModelDownloadStatus> jobs =
                    await ModelDownloadClient.Current.ListDownloadsAsync(CancellationToken.None);
                _downloadJobs = jobs;
                RenderDownloads(jobs);
                ShowActiveTransfer(jobs);

                if (!jobs.Any(IsActive))
                {
                    // Finished, paused or failed: one full refresh so the installed-model list and
                    // the counters reflect the file that just landed.
                    _downloadPoll?.Stop();
                    await RefreshAllAsync();
                }
            }
            catch (Exception ex)
            {
                // A single failed poll is not worth tearing the timer down; the next tick retries.
                Log.Write("Model Hub download poll failed: " + ex.Message);
            }
            finally { _pollInFlight = false; }
        }

        private static async Task<RuntimeManagerHardwareStatus?> TryGetHardwareAsync(CancellationToken ct)
        {
            try { return await RuntimeManagerClient.Current.GetStatusAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task<AiStorageBudgetStatus?> TryGetStorageBudgetAsync(CancellationToken ct)
        {
            try { return await ModelDownloadClient.Current.GetBudgetAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task<ModelDiscoveryCatalog?> TryGetDiscoveryCatalogAsync(CancellationToken ct)
        {
            try { return await ModelDownloadClient.Current.GetDiscoveryCatalogAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static async Task<List<CandidateEvaluationJobStatus>?> TryGetCandidateJobHistoryAsync(CancellationToken ct)
        {
            try { return await ModelDownloadClient.Current.ListCandidateEvaluationJobsAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        private static async Task<CandidateRecommendationStatusResponse?> TryGetRecommendationStatusAsync(CancellationToken ct)
        {
            try { return await ModelDownloadClient.Current.GetCandidateRecommendationStatusAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        private static async Task<CandidateUtilityPolicyStatus?> TryGetCandidateUtilityPolicyAsync(CancellationToken ct)
        {
            try { return await ModelDownloadClient.Current.GetCandidateUtilityPolicyAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        private void RenderCandidateUtilityPolicyStatus()
        {
            bool canMutatePolicy = !_candidateUtilityPolicyUnavailable
                && !_candidateUtilityPolicyMutationInFlight
                && _candidateUtilityCompareInFlight.Count == 0;
            _candidateUtilityConfigureButton.IsEnabled = canMutatePolicy;
            _candidateUtilityResetButton.Visibility = _candidateUtilityPolicy?.Configured == true
                || _candidateUtilityPolicyUnavailable
                ? Visibility.Visible : Visibility.Collapsed;
            _candidateUtilityResetButton.IsEnabled = !_candidateUtilityPolicyMutationInFlight
                && _candidateUtilityCompareInFlight.Count == 0;

            if (_candidateUtilityPolicyUnavailable)
            {
                _candidateUtilityStatus.Text = "Candidate comparison policy readiness is unavailable. Comparison stays disabled until Core confirms the explicit policy state.";
                return;
            }

            if (_candidateUtilityPolicy is not { Configured: true, Readiness: "WellFormed", Policy: not null } status)
            {
                string? reason = _candidateUtilityPolicy?.ReasonCode;
                _candidateUtilityStatus.Text = string.IsNullOrWhiteSpace(reason) || reason == "not_configured"
                    ? "Candidate comparison policy: not configured. No thresholds are assumed; comparisons and recommendations are unavailable."
                    : "Candidate comparison policy is not ready (" + LimitDisplay(reason, 60)
                        + "). Comparison stays disabled; no thresholds are assumed.";
                return;
            }

            _candidateUtilityStatus.Text = "Explicit Candidate utility policy loaded for "
                + LimitDisplay(status.Policy.Capability, 80)
                + ". Comparison requires separate quality/reliability floors plus TTFT and final-response latency margins; any result requires human review and is not a recommendation or best-in-class claim.";
        }

        private async Task ConfigureCandidateUtilityPolicyAsync(bool useTestContextFixture = false)
        {
            bool testFixtureAllowed = false;
#if DEBUG
            testFixtureAllowed = useTestContextFixture
                && string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_UTILITY_POLICY"), "1", StringComparison.Ordinal);
#endif
            if ((_candidateUtilityPolicyUnavailable && !testFixtureAllowed) || _candidateUtilityPolicyMutationInFlight
                || _candidateUtilityCompareInFlight.Count > 0)
                return;

            InvalidatePendingRefresh();
            _candidateUtilityPolicyMutationInFlight = true;
            RenderCandidates();
            RenderCandidateUtilityPolicyStatus();
            try
            {
                string? qualityProfileId = testFixtureAllowed
                    ? CandidateEvaluationScope.ChatInstructionFollowingQualityProfileId
                    : await ChooseCandidateQualityProfileAsync(forPolicyConfiguration: true);
                if (qualityProfileId is null) return;

                CandidateUtilityPolicyCalibrationContext context;
#if DEBUG
                context = testFixtureAllowed
                    ? CreateCandidateUtilityCalibrationContextFixture()
                    : await ModelDownloadClient.Current.GetCandidateUtilityPolicyContextAsync(CancellationToken.None, qualityProfileId);
#else
                context = await ModelDownloadClient.Current.GetCandidateUtilityPolicyContextAsync(CancellationToken.None, qualityProfileId);
#endif
                CandidateUtilityPolicyDefinition? existingPolicy = _candidateUtilityPolicy?.Policy;
                bool sameScope = existingPolicy != null && PolicyMatchesContext(existingPolicy, context);
                var content = new StackPanel { Spacing = 10 };
                bool generalChatScope = context.QualityProfileId == CandidateEvaluationScope.GeneralChatQualityProfileId;
                content.Children.Add(new TextBlock
                {
                    Text = "These are explicit comparison criteria, not recommended thresholds. No values are filled in for a new policy. The selected evidence scope is "
                        + context.Capability + " (" + context.QualityProfileId + " v"
                        + context.QualityProfileVersion.ToString(CultureInfo.InvariantCulture) + "). "
                        + (generalChatScope
                            ? "It measures bounded standard × balanced general-chat tasks only; it does not establish coding, other modality or best-in-class quality. The policy must retain at least 90% quality, 95% fresh-process reliability and 5 distinct runs."
                            : "It is the fixed quick-check smoke profile for comparison evidence only; it does not establish general-chat, coding, reasoning or best-in-class quality."),
                    TextWrapping = TextWrapping.Wrap
                });
                if (existingPolicy != null && !sameScope)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "The saved criteria belong to a different machine/runtime/build/configuration scope. They will not be carried over; enter all values again for this scope.",
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                }
                else if (sameScope)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "Showing the previously saved values for this exact scope. Saving replaces that explicit policy.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8
                    });
                }

                string fingerprintText = "Exact Core-owned scope (read-only)\n"
                    + "Hardware: " + context.QualityEnvironment.HardwareFingerprint + "\n"
                    + "Runtime: " + context.QualityEnvironment.RuntimeFingerprint + "\n"
                    + "Runtime binary: " + context.QualityEnvironment.BinaryFingerprint + "\n"
                    + "Evaluation configuration: " + context.QualityEnvironment.ConfigurationFingerprint + "\n"
                    + "Context fingerprint: " + context.ContextFingerprint;
                content.Children.Add(new TextBlock { Text = fingerprintText, TextWrapping = TextWrapping.Wrap, FontSize = 12 });

                var passRate = AddPolicyCriteriaField(content, "Minimum task-quality pass rate (%)",
                    "ModelHub_CandidateUtilityQualityFloor_TextBox", sameScope ? FormatBasisPoints(existingPolicy!.MinimumQualityPassRateBasisPoints) : null);
                var reliability = AddPolicyCriteriaField(content, "Minimum fresh-process reliability (%)",
                    "ModelHub_CandidateUtilityReliabilityFloor_TextBox", sameScope ? FormatBasisPoints(existingPolicy!.MinimumQualityReliabilityBasisPoints) : null);
                var runCount = AddPolicyCriteriaField(content,
                    "Minimum distinct quality runs (2–" + context.MaximumQualityRunCount.ToString(CultureInfo.InvariantCulture) + ")",
                    "ModelHub_CandidateUtilityMinimumRuns_TextBox",
                    sameScope ? existingPolicy!.MinimumQualityRunCount?.ToString(CultureInfo.InvariantCulture) : null);
                var ttftMargin = AddPolicyCriteriaField(content,
                    "Minimum TTFT improvement over active model (%) · lower is better",
                    "ModelHub_CandidateUtilityTtftMargin_TextBox",
                    sameScope ? FormatBasisPoints(existingPolicy!.MinimumTimeToFirstTokenMarginBasisPoints) : null);
                var completionMargin = AddPolicyCriteriaField(content,
                    "Minimum final-response latency improvement (%) · lower is better",
                    "ModelHub_CandidateUtilityCompletionMargin_TextBox",
                    sameScope ? FormatBasisPoints(existingPolicy!.MinimumCompletionLatencyMarginBasisPoints) : null);
                var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed) };
                AutomationProperties.SetAutomationId(error, "ModelHub_CandidateUtilityConfigureError_Text");
                content.Children.Add(error);

                var scroll = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 560
                };
                var dialog = new ContentDialog
                {
                    Title = "Set explicit Candidate comparison criteria",
                    Content = scroll,
                    PrimaryButtonText = "Save criteria",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot,
                    MaxWidth = 900
                };
                dialog.Resources["ContentDialogMaxWidth"] = 900d;
                AutomationProperties.SetAutomationId(dialog, "ModelHub_CandidateUtilityConfigure_Dialog");
                dialog.PrimaryButtonClick += async (_, args) =>
                {
                    args.Cancel = true;
                    ContentDialogButtonClickDeferral deferral = args.GetDeferral();
                    try
                    {
                        if (!TryParsePercentBasisPoints(passRate.Text, 10000, out int passRateBasisPoints)
                            || !TryParsePercentBasisPoints(reliability.Text, 10000, out int reliabilityBasisPoints)
                            || !int.TryParse(runCount.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int minimumRuns)
                            || minimumRuns < 2 || minimumRuns > context.MaximumQualityRunCount
                            || (generalChatScope && (passRateBasisPoints < 9000 || reliabilityBasisPoints < 9500 || minimumRuns < 5))
                            || !TryParsePercentBasisPoints(ttftMargin.Text,
                                Math.Min(context.MaximumMarginBasisPoints, 10000), out int ttftBasisPoints)
                            || !TryParsePercentBasisPoints(completionMargin.Text,
                                Math.Min(context.MaximumMarginBasisPoints, 10000), out int completionBasisPoints))
                        {
                            error.Text = generalChatScope
                                ? "Enter every required value. General chat criteria cannot be lower than 90.00% task quality, 95.00% fresh-process reliability or 5 distinct runs; other percentages and run count must also be within the displayed ranges."
                                : "Enter every required value. Percentages must be between 0.01 and 100.00 with at most two decimals; run count must be within the displayed range.";
                            return;
                        }

                        var request = new CandidateUtilityPolicyConfigurationRequest(
                            context.ContextFingerprint,
                            passRateBasisPoints,
                            reliabilityBasisPoints,
                            minimumRuns,
                            ttftBasisPoints,
                            completionBasisPoints,
                            context.QualityProfileId);
                        CandidateUtilityPolicyStatus saved = await ModelDownloadClient.Current
                            .ConfigureCandidateUtilityPolicyAsync(request, CancellationToken.None);
                        if (!saved.Configured || saved.Readiness != "WellFormed" || saved.Policy == null)
                        {
                            error.Text = "Core did not confirm a complete policy. No success was assumed.";
                            return;
                        }

                        _candidateUtilityPolicy = saved;
                        _candidateUtilityPolicyUnavailable = false;
                        _candidateUtilityResultMessages.Clear();
                        _candidateUtilityStateVersion++;
                        RenderCandidateUtilityPolicyStatus();
                        RenderCandidates();
                        _status.Text = "Explicit Candidate comparison criteria saved for the displayed hardware/runtime scope. No model was promoted and routing did not change.";
                        dialog.Hide();
                    }
                    catch (Exception ex)
                    {
                        error.Text = "Criteria were not saved: " + LimitDisplay(ex.Message, 220)
                            + ". Refresh the Model Hub and retry if the hardware/runtime context changed.";
                    }
                    finally { deferral.Complete(); }
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "Comparison criteria are unavailable: " + LimitDisplay(ex.Message, 180)
                    + ". No policy or model state was changed.";
            }
            finally
            {
                _candidateUtilityPolicyMutationInFlight = false;
                RenderCandidates();
                RenderCandidateUtilityPolicyStatus();
            }
        }

        private async Task ResetCandidateUtilityPolicyAsync()
        {
            if (_candidateUtilityPolicyMutationInFlight
                || _candidateUtilityCompareInFlight.Count > 0
                || (!_candidateUtilityPolicyUnavailable && _candidateUtilityPolicy?.Configured != true))
                return;

            var confirm = new ContentDialog
            {
                Title = "Reset Candidate comparison criteria?",
                Content = "This removes the locally saved comparison policy. It does not delete models or evidence, and it does not create replacement thresholds.",
                PrimaryButtonText = "Reset criteria",
                CloseButtonText = "Keep criteria",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            InvalidatePendingRefresh();
            _candidateUtilityPolicyMutationInFlight = true;
            RenderCandidates();
            RenderCandidateUtilityPolicyStatus();
            try
            {
                CandidateUtilityPolicyStatus reset = await ModelDownloadClient.Current
                    .ResetCandidateUtilityPolicyAsync(CancellationToken.None);
                if (reset.Configured || reset.Readiness != "NotConfigured" || reset.Policy != null)
                    throw new InvalidDataException("Core did not confirm that the comparison policy was removed.");
                _candidateUtilityPolicy = reset;
                _candidateUtilityPolicyUnavailable = false;
                _candidateUtilityResultMessages.Clear();
                _candidateUtilityStateVersion++;
                _status.Text = "Candidate comparison criteria reset. No thresholds were restored or inferred; no model or routing changed.";
            }
            catch (Exception ex)
            {
                _status.Text = "Comparison criteria were not reset: " + LimitDisplay(ex.Message, 180)
                    + ". The previous displayed state is retained.";
            }
            finally
            {
                _candidateUtilityPolicyMutationInFlight = false;
                RenderCandidates();
                RenderCandidateUtilityPolicyStatus();
            }
        }

        private static TextBox AddPolicyCriteriaField(Panel content, string label, string automationId,
            string? currentValue)
        {
            var field = new TextBox
            {
                Header = label,
                PlaceholderText = "Required",
                Text = currentValue ?? string.Empty,
                Width = 360
            };
            AutomationProperties.SetAutomationId(field, automationId);
            content.Children.Add(field);
            return field;
        }

        private static string? FormatBasisPoints(int? basisPoints) => basisPoints is int value
            ? (value / 100m).ToString("0.##", CultureInfo.InvariantCulture) : null;

        private static bool TryParsePercentBasisPoints(string? text, int maximumBasisPoints, out int basisPoints)
        {
            basisPoints = 0;
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out decimal percent)
                || percent < 0.01m || percent > maximumBasisPoints / 100m)
                return false;
            decimal scaled = percent * 100m;
            if (decimal.Truncate(scaled) != scaled) return false;
            basisPoints = decimal.ToInt32(scaled);
            return basisPoints is >= 1;
        }

        private static bool PolicyMatchesContext(CandidateUtilityPolicyDefinition policy,
            CandidateUtilityPolicyCalibrationContext context) =>
            policy.SchemaVersion == CandidateUtilityPolicyDefinition.CurrentSchemaVersion
            && policy.Capability == context.Capability
            && policy.QualitySuiteId == context.QualitySuiteId
            && policy.QualitySuiteVersion == context.QualitySuiteVersion
            && policy.QualityEvidenceProfileId == context.QualityProfileId
            && policy.QualityEvidenceProfileVersion == context.QualityProfileVersion
            && policy.QualityEvidenceProfileSha256 == context.QualityProfileSha256
            && policy.PerformanceWorkloadId == context.PerformanceWorkloadId
            && policy.PerformanceWorkloadVersion == context.PerformanceWorkloadVersion
            && policy.HardwareFingerprint == context.QualityEnvironment.HardwareFingerprint
            && policy.RuntimeFingerprint == context.QualityEnvironment.RuntimeFingerprint
            && policy.BinaryFingerprint == context.QualityEnvironment.BinaryFingerprint
            && policy.ConfigurationFingerprint == context.QualityEnvironment.ConfigurationFingerprint;

        private void InvalidateCandidateUtilityComparisons()
        {
            _candidateUtilityStateVersion++;
            _candidateUtilityResultMessages.Clear();
        }

        private async Task CheckSpecificRepositoriesAsync()
        {
            if (!_explicitDiscoveryButton.IsEnabled || _explicitDiscoveryDialogOpen || !IsLoaded) return;
            var input = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                PlaceholderText = "owner/repository (one per line, at most two)",
                MaxLength = 400, MinWidth = 420, Height = 100
            };
            AutomationProperties.SetAutomationId(input, "ModelHub_CandidateExplicitRepos_TextBox");
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(error, "ModelHub_CandidateExplicitRefresh_Error_Text");
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "Checking public repository metadata contacts Hugging Face. Publisher data is unverified. "
                    + "This does not download a model or start an evaluation.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(input);
            content.Children.Add(error);
            var dialog = new ContentDialog
            {
                Title = "Check specific repositories",
                Content = content, PrimaryButtonText = "Check public metadata",
                CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            AutomationProperties.SetAutomationId(dialog, "ModelHub_CandidateExplicitRefresh_Dialog");
            string[]? repositoryIds = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!TryParseExplicitRepositoryIds(input.Text, out string[] parsed))
                {
                    error.Text = "Enter one or two distinct owner/repository IDs, one per line. "
                        + "Do not enter a URL, revision, file, query, or token.";
                    args.Cancel = true;
                    return;
                }
                repositoryIds = parsed;
            };
            ContentDialogResult dialogResult;
            _explicitDiscoveryDialogOpen = true;
            try { dialogResult = await dialog.ShowAsync(); }
            catch (Exception)
            {
                // WinUI allows one open ContentDialog per XamlRoot; another dialog wins and no request is sent.
                _explicitDiscoveryStatus.Text = "The repository check could not open because another dialog is active. "
                    + "No request was sent.";
                return;
            }
            finally { _explicitDiscoveryDialogOpen = false; }
            if (dialogResult != ContentDialogResult.Primary || repositoryIds == null || !IsLoaded
                || !_explicitDiscoveryButton.IsEnabled)
                return;

            long operationId = ++_explicitDiscoveryOperationId;
            using var cts = new CancellationTokenSource();
            _explicitDiscoveryCts = cts;
            _explicitDiscoveryButton.IsEnabled = false;
            _explicitDiscoveryStatus.Text = "Checking public metadata for " + repositoryIds.Length
                + " repository ID(s)…";
            try
            {
                var request = new ModelDiscoveryExplicitRefreshRequest
                { RepositoryIds = repositoryIds.ToList() };
                ModelDiscoveryRefreshResult result = await ModelDownloadClient.Current
                    .RefreshExplicitDiscoveryAsync(request, cts.Token);
                // Automatic refreshes may run concurrently; only a user reload, unload or a newer
                // explicit check invalidates this operation.
                if (operationId != _explicitDiscoveryOperationId || cts.IsCancellationRequested) return;
                if (result.Outcome == "refreshed")
                {
                    ModelDiscoveryCatalog? priorCatalog = _discoveryCatalog;
                    int refreshGeneration = _refreshGeneration;
                    await RefreshAllAsync();
                    if (operationId != _explicitDiscoveryOperationId || cts.IsCancellationRequested) return;
                    if (_refreshGeneration == refreshGeneration)
                        // RefreshAllAsync defers while a Candidate ignore is pending; it reloads afterwards.
                        _explicitDiscoveryStatus.Text = "Public metadata checked. The catalog will reload after the "
                            + "pending Candidate change finishes. No download or evaluation started.";
                    else if (_refreshGeneration != refreshGeneration + 1)
                        _explicitDiscoveryStatus.Text = _catalogStale
                            ? "Public metadata checked, but a newer refresh could not reload the catalog. "
                                + "Earlier cards remain. No download or evaluation started."
                            : "Public metadata checked. A newer Model Hub refresh is "
                                + "showing the latest catalog. No download or evaluation started.";
                    else _explicitDiscoveryStatus.Text = _catalogStale || ReferenceEquals(priorCatalog, _discoveryCatalog)
                        ? "Public metadata was checked, but the catalog could not be reloaded. Earlier cards remain."
                        : "Public metadata checked. The catalog now shows " + result.CandidateCount
                            + " unverified Candidate record(s). No download or evaluation started.";
                }
                else if (result.Outcome == "cooldown")
                    _explicitDiscoveryStatus.Text = "Candidate metadata was checked recently. "
                        + "No new request was made; existing cards remain.";
                else
                    _explicitDiscoveryStatus.Text = "Repository metadata check failed ("
                        + LimitDisplay(result.ErrorCode, 40) + "). Existing cards remain.";
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (operationId == _explicitDiscoveryOperationId)
                    _explicitDiscoveryStatus.Text = "Repository metadata check could not be confirmed. "
                        + "Existing cards remain; no model action was taken.";
            }
            finally
            {
                if (ReferenceEquals(_explicitDiscoveryCts, cts)) _explicitDiscoveryCts = null;
                if (operationId == _explicitDiscoveryOperationId) _explicitDiscoveryButton.IsEnabled = true;
            }
        }

        private static bool TryParseExplicitRepositoryIds(string text, out string[] ids)
        {
            ids = Array.Empty<string>();
            if (text.Length > 400) return false;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).ToArray();
            // Hugging Face repository IDs resolve case-insensitively, so case variants are duplicates.
            if (lines.Length is < 1 or > 2 || lines.Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Length)
                return false;
            foreach (string line in lines)
            {
                if (line.Length > 193) return false;
                string[] parts = line.Split('/');
                if (parts.Length != 2 || parts.Any(part => part.Length is < 1 or > 96
                    || !char.IsAsciiLetterOrDigit(part[0]) || !char.IsAsciiLetterOrDigit(part[^1])
                    || part.Contains("..", StringComparison.Ordinal)
                    || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
                    return false;
            }
            ids = lines;
            return true;
        }

        private const string StaleCatalogMessage = "Candidate catalog could not be refreshed; showing earlier data.";

        private async Task RefreshCandidateCatalogAsync()
        {
            InvalidateExplicitDiscovery();
            SetBusy(true, "Refreshing public model metadata…");
            string outcomeMessage;
            try
            {
                ModelDiscoveryRefreshResult result =
                    await ModelDownloadClient.Current.RefreshDiscoveryAsync(CancellationToken.None);
                if (string.Equals(result.Outcome, "gaming_active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(result.ErrorCode, "gaming_active", StringComparison.OrdinalIgnoreCase))
                {
                    _discoveryRefreshPausedReason = DiscoveryRefreshStatusPresenter.DefaultPausedReason;
                }
                else if (result.Outcome == "refreshed")
                {
                    _discoveryRefreshPausedReason = null;
                }
                outcomeMessage = result.Outcome switch
                {
                    "refreshed" => "Candidate catalog refreshed · " + result.CandidateCount + " metadata record(s).",
                    "cooldown" => "Candidate catalog was checked recently; no new request was made.",
                    "failed" => "Candidate metadata refresh failed (" + LimitDisplay(result.ErrorCode, 40)
                        + "). Existing catalog data remains available; no model action was taken.",
                    _ => "Candidate refresh returned an unrecognized outcome: " + LimitDisplay(result.Outcome, 40)
                };
            }
            catch (Exception)
            {
                outcomeMessage = "Candidate metadata refresh could not be confirmed. Existing catalog data, if any, was not replaced by this view.";
            }
            finally { SetBusy(false, string.Empty); }

            await RefreshAllAsync();
            _status.Text = outcomeMessage + (_catalogStale ? " " + StaleCatalogMessage : string.Empty);
        }

        private void RenderDiscoveryRefreshStatus()
        {
            string? pausedReason = _discoveryRefreshPausedReason;
            if (string.IsNullOrWhiteSpace(pausedReason) && _hardware?.GamingActive == true)
            {
                pausedReason = DiscoveryRefreshStatusPresenter.DefaultPausedReason;
            }

            bool isOffline = _catalogStale || DiscoveryRefreshStatusPresenter.IsFailedRefreshCode(_discoveryCatalog?.LastErrorCode);
            DiscoveryRefreshStatusPresentation presentation = DiscoveryRefreshStatusPresenter.Present(
                _discoveryCatalog,
                pausedReason: pausedReason,
                isOffline: isOffline);

            _discoveryRefreshStatus.Text = presentation.StatusText;
            if (!string.IsNullOrEmpty(presentation.TooltipText))
                ToolTipService.SetToolTip(_discoveryRefreshStatus, presentation.TooltipText);
            else
                ToolTipService.SetToolTip(_discoveryRefreshStatus, null);
        }

        private static async Task<SuggestionFeedStatusDto?> TryGetMaintenanceStatusAsync(CancellationToken ct)
        {
            try { return await SuggestionFeedClient.Current.GetStatusAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("Maintenance suggestions unavailable: " + ex.Message);
                return null;
            }
        }

        private void RenderSuggestions()
        {
            _suggestions.Children.Clear();
            string lane = ModelSuggestionLanePresenter.ResolveLane(SettingsStore.Current.Data.ModelHubSuggestionLane, null);
            ModelSuggestionLaneView section = ModelSuggestionLanePresenter.Present(
                _maintenanceUnavailable ? null : _maintenanceStatus ?? new SuggestionFeedStatusDto(),
                _suggestionDeferredKeys, _suggestionHiddenKeys, lane, _isAdvancedMode, DateTime.UtcNow);
            _suggestions.Children.Add(new TextBlock { Text = section.Disclaimer, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78 });
            var hardware = new TextBlock { Text = section.HardwareText, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 };
            AutomationProperties.SetAutomationId(hardware, ModelSuggestionPresenter.HardwareAutomationId);
            _suggestions.Children.Add(hardware);
            _suggestions.Children.Add(new TextBlock { Text = section.FreshnessText, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = section.IsStale ? 1.0 : 0.72 });
            var laneTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (string laneId in new[] { ModelSuggestionLanePresenter.Standard, ModelSuggestionLanePresenter.Uncensored })
            {
                int count = laneId == ModelSuggestionLanePresenter.Uncensored ? section.UncensoredCount : section.StandardCount;
                var tab = new ToggleButton
                {
                    Content = ModelSuggestionLanePresenter.LaneLabel(laneId) + " (" + count.ToString(CultureInfo.InvariantCulture) + ")",
                    IsChecked = laneId == section.SelectedLane
                };
                AutomationProperties.SetAutomationId(tab, ModelSuggestionLanePresenter.LaneAutomationId(laneId));
                string target = laneId;
                tab.Click += (_, _) =>
                {
                    SettingsStore.Current.Data.ModelHubSuggestionLane = target;
                    SettingsStore.Current.Save();
                    RenderSuggestions();
                };
                laneTabs.Children.Add(tab);
            }
            _suggestions.Children.Add(laneTabs);
            _suggestions.Children.Add(new TextBlock { Text = section.LaneNote, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 });
            if (section.EmptyText != null)
            {
                var empty = new TextBlock { Text = section.EmptyText, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(empty, ModelSuggestionPresenter.EmptyAutomationId);
                _suggestions.Children.Add(empty);
                return;
            }
            bool anyBusy = _suggestionBusyKeys.Count > 0;
            foreach (ModelSuggestionGroup group in section.Groups)
            {
                var groupTitle = new TextBlock { Text = group.Title + (group.HiddenCount > 0 ? " (top " + group.Cards.Count.ToString(CultureInfo.InvariantCulture) + "; more in Advanced)" : ""), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                AutomationProperties.SetAutomationId(groupTitle, ModelSuggestionLanePresenter.GroupAutomationId(section.SelectedLane, group.CapabilityGroup));
                _suggestions.Children.Add(groupTitle);
            foreach (ModelSuggestionCard cardModel in group.Cards)
            {
                var card = new StackPanel { Spacing = 5 };
                var title = new TextBlock { Text = cardModel.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                AutomationProperties.SetAutomationId(title, ModelSuggestionPresenter.CardAutomationId(cardModel.Key, "Title"));
                card.Children.Add(title);
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                chips.Children.Add(new TextBlock { Text = cardModel.FitText, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                foreach (string chip in cardModel.CohortChips)
                    chips.Children.Add(new TextBlock { Text = "[" + chip + "]", FontSize = 12, Opacity = 0.85 });
                card.Children.Add(chips);
                card.Children.Add(new TextBlock
                {
                    Text = cardModel.CapabilityText + " · " + cardModel.SizeText + " · " + cardModel.LicenceText,
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78
                });
                card.Children.Add(new TextBlock { Text = cardModel.ReasonText, TextWrapping = TextWrapping.Wrap });
                if (cardModel.LaneEvidenceText != null)
                    card.Children.Add(new TextBlock { Text = cardModel.LaneEvidenceText, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 });
                card.Children.Add(new TextBlock { Text = cardModel.ProvenanceText, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.65 });
                if (cardModel.RuntimeNoticeText != null)
                {
                    var notice = new TextBlock { Text = cardModel.RuntimeNoticeText, TextWrapping = TextWrapping.Wrap };
                    AutomationProperties.SetAutomationId(notice, ModelSuggestionPresenter.CardAutomationId(cardModel.Key, "RuntimeNotice"));
                    card.Children.Add(notice);
                }
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (cardModel.CanReviewAndDownload)
                {
                    var review = new Button { Content = "Review & download", IsEnabled = !anyBusy };
                    AutomationProperties.SetAutomationId(review, ModelSuggestionPresenter.CardAutomationId(cardModel.Key, "Review"));
                    review.Click += async (_, _) => await ReviewSuggestionAsync(cardModel);
                    actions.Children.Add(review);
                }
                if (cardModel.CanDefer)
                {
                    var later = new Button { Content = "Not now", IsEnabled = !anyBusy };
                    AutomationProperties.SetAutomationId(later, ModelSuggestionPresenter.CardAutomationId(cardModel.Key, "NotNow"));
                    ToolTipService.SetToolTip(later, "Hides this suggestion until PaRuX restarts. Nothing is changed.");
                    later.Click += (_, _) => { _suggestionDeferredKeys.Add(cardModel.Key); RenderSuggestions(); };
                    actions.Children.Add(later);
                }
                if (cardModel.CanIgnore)
                {
                    var ignore = new Button { Content = "Ignore", IsEnabled = !anyBusy };
                    AutomationProperties.SetAutomationId(ignore, ModelSuggestionPresenter.CardAutomationId(cardModel.Key, "Ignore"));
                    ToolTipService.SetToolTip(ignore, "Permanently hides this exact Candidate revision. Existing files are not deleted.");
                    ignore.Click += async (_, _) => await IgnoreSuggestionAsync(cardModel.Key);
                    actions.Children.Add(ignore);
                }
                card.Children.Add(actions);
                _suggestions.Children.Add(new Border
                {
                    Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = card
                });
            }
            }
        }

        /// <summary>Opens the existing revision-pinned plan + licence-consent flow; never downloads directly.</summary>
        private async Task ReviewSuggestionAsync(ModelSuggestionCard card)
        {
            if (!_suggestionBusyKeys.Add(card.Key)) return;
            RenderSuggestions();
            SetBusy(true, "Resolving a complete GGUF set for " + card.Repo + "…");
            try
            {
                List<ModelSearchResult> matches = await ModelDownloadClient.Current.SearchAsync(card.Repo, 10, CancellationToken.None);
                ModelHubSplitSetPresentation.RecommendedSetOption selected =
                    ModelHubSplitSetPresentation.PickRecommendedSet(matches, card.Repo, null)
                    ?? throw new InvalidOperationException(
                        "This repository has no complete GGUF set available right now. Nothing was downloaded.");
                SetBusy(false, "Review the quantization, licence and complete download plan.");
                await ChooseQuantChoiceAsync(selected.RepoId, selected.Choice);
            }
            catch (Exception ex) { _status.Text = "Could not open the suggestion for review: " + ex.Message; }
            finally
            {
                _suggestionBusyKeys.Remove(card.Key);
                SetBusy(false, _status.Text);
                RenderSuggestions();
            }
        }

        private async Task IgnoreSuggestionAsync(string key)
        {
            if (!_suggestionBusyKeys.Add(key)) return;
            try
            {
                await IgnoreCandidateAsync(key);
                bool stillActive = _discoveryCatalog?.Candidates.Any(candidate =>
                    candidate.Key == key && candidate.Lifecycle == "Candidate" && candidate.Disposition == "active") != false;
                if (!stillActive) _suggestionHiddenKeys.Add(key);
                else _status.Text = "The ignore decision was not confirmed; the suggestion stays visible. Refresh candidates and retry.";
            }
            finally
            {
                _suggestionBusyKeys.Remove(key);
                RenderSuggestions();
            }
        }

        private void RenderCandidates()
        {
            RenderDiscoveryRefreshStatus();
            _candidates.Children.Clear();
            if (_discoveryCatalog == null)
            {
                _candidateFilterCount.Text = "Catalog unavailable";
                _candidates.Children.Add(new TextBlock
                {
                    Text = "Candidate catalog is unavailable. Use Refresh candidates to retry; no model action was taken.",
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            ModelDiscoveryCandidate[] allActiveCandidates = _discoveryCatalog.Candidates
                .Where(candidate => candidate.Lifecycle == "Candidate" && candidate.Disposition == "active")
                .OrderBy(candidate => candidate.RepoId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.RepoFilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            RenderPendingCandidateNotices(allActiveCandidates);
            string filter = _candidateFilter.Text.Trim();
            ModelDiscoveryCandidate[] matchingCandidates = allActiveCandidates
                .Where(candidate => ModelCohortFilterPresenter.Matches(_cohortFilter, candidate.DiscoveryCohorts))
                .Where(candidate => filter.Length == 0
                    || candidate.RepoId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || candidate.RepoFilePath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            ModelDiscoveryCandidate[] activeCandidates = matchingCandidates.Take(50).ToArray();
            _candidateFilterCount.Text = (_catalogStale ? StaleCatalogMessage + " " : string.Empty)
                + "Showing " + activeCandidates.Length + " of "
                + matchingCandidates.Length + " matching Candidates (" + allActiveCandidates.Length
                + " active total).";
            if (activeCandidates.Length == 0)
            {
                _candidates.Children.Add(new TextBlock
                {
                    Text = filter.Length == 0 && _cohortFilter != ModelCohortFilter.All && allActiveCandidates.Length > 0
                        ? "No local Candidates were listed by the " + ModelCohortFilterPresenter.Label(_cohortFilter) + " discovery feed. Choose All to see the catalog."
                        : filter.Length == 0
                        ? "No active Candidates in the local catalog. Refresh candidates makes an explicit public metadata request."
                        : "No local Candidates match this filter. Clear the filter to see the catalog.",
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            bool anotherJobActive = _candidateJobStatuses.Any(pair =>
                IsCandidateJobActive(pair.Value) && !IsCandidateEvaluationFixtureKey(pair.Key));
            bool anyCandidateIgnoreInFlight = _candidateIgnoreInFlight.Count > 0;
            foreach (ModelDiscoveryCandidate candidate in activeCandidates)
            {
                string? managedFileName = CandidateManagedFileName(candidate);
                bool safeKey = IsSafeCandidateKey(candidate.Key);
                bool textGguf = managedFileName?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true;
                ManagedModelInfo? managedMatch = textGguf
                    ? FindMatchingManagedCandidate(candidate, managedFileName!)
                    : null;
                bool pinnedSourceValid = textGguf && IsCandidatePinnedSourceValid(candidate, managedFileName!);
                bool metadataTimeValid = _discoveryCatalog.LastSuccessUtc is DateTime observedUtc
                    && observedUtc.Kind == DateTimeKind.Utc;
                bool candidateDownloadActive = textGguf && _downloadJobs.Any(job => IsActive(job)
                    && string.Equals(job.FileName, managedFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(job.Revision, candidate.Revision, StringComparison.OrdinalIgnoreCase));
                bool candidateDownloadCompleted = textGguf && _downloadJobs.Any(job => job.Status == "completed"
                    && string.Equals(job.FileName, managedFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(job.Revision, candidate.Revision, StringComparison.OrdinalIgnoreCase));
                bool candidateDownloadStarting = _candidateDownloadInFlight.Contains(candidate.Key);
                bool candidateIgnoreInFlight = _candidateIgnoreInFlight.Contains(candidate.Key);
                bool candidateUtilityComparisonInFlight = _candidateUtilityCompareInFlight.ContainsKey(candidate.Key);
                bool anyCandidateUtilityComparisonInFlight = _candidateUtilityCompareInFlight.Count > 0;
                _candidateJobStatuses.TryGetValue(candidate.Key, out CandidateEvaluationJobStatus? jobStatus);
                bool candidateEvaluationActive = jobStatus != null && IsCandidateJobActive(jobStatus);
                bool candidateEvaluationUncertain = jobStatus != null
                    && !_candidateJobIds.ContainsKey(candidate.Key)
                    && jobStatus.ErrorCode is "start_unconfirmed" or "invalid_job_identity";

                var card = new StackPanel { Spacing = 6 };
                card.Children.Add(new TextBlock
                {
                    Text = LimitDisplay(candidate.RepoId, 180) + " · " + LimitDisplay(candidate.RepoFilePath, 220),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                var declaredMetadata = new List<string>
                {
                    "Unverified publisher metadata",
                    "revision " + LimitDisplay(candidate.Revision, 64)
                };
                if (!string.IsNullOrWhiteSpace(candidate.DeclaredPipelineTag))
                    declaredMetadata.Add("pipeline " + LimitDisplay(candidate.DeclaredPipelineTag, 60));
                if (candidate.DeclaredSizeBytes is > 0)
                    declaredMetadata.Add("declared size " + candidate.DeclaredSizeBytes.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes");
                if (!string.IsNullOrWhiteSpace(candidate.DeclaredSha256))
                {
                    string declaredHash = LimitDisplay(candidate.DeclaredSha256, 12);
                    declaredMetadata.Add("publisher SHA-256 claim " + declaredHash + "… (not verified)");
                }
                string[] declaredTags = (candidate.DeclaredTags ?? new List<string>())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Take(4)
                    .Select(tag => LimitDisplay(tag, 40))
                    .ToArray();
                if (declaredTags.Length > 0) declaredMetadata.Add("tags " + string.Join(", ", declaredTags));
                card.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", declaredMetadata),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.72
                });
                var cohortText = new TextBlock
                {
                    Text = ModelCohortFilterPresenter.CohortLine(candidate.DiscoveryCohorts)
                        + " (discovery provenance only, not a quality signal).",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.72
                };
                if (safeKey)
                    AutomationProperties.SetAutomationId(cohortText, "ModelHub_CandidateCohorts_" + candidate.Key);
                card.Children.Add(cohortText);
                card.Children.Add(new TextBlock
                {
                    Text = "Candidate · publisher metadata is unverified; this is not a validated model or recommendation."
                        + (string.IsNullOrWhiteSpace(candidate.DeclaredLicense)
                            ? " Licence not declared."
                            : " Publisher-declared licence: " + LimitDisplay(candidate.DeclaredLicense, 80) + "."),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.78
                });

                var provisionalFit = new TextBlock
                {
                    Text = FormatCandidateProvisionalFit(candidate, jobStatus),
                    TextWrapping = TextWrapping.Wrap
                };
                if (safeKey)
                    AutomationProperties.SetAutomationId(provisionalFit, "ModelHub_CandidateProvisionalFit_" + candidate.Key);
                card.Children.Add(provisionalFit);

                string readiness = managedMatch != null
                    ? candidateDownloadActive || candidateDownloadStarting
                        ? "A pinned Candidate transfer or byte verification is in progress. Evaluation is paused until it finishes."
                        : candidateEvaluationUncertain
                            ? "Core did not confirm whether Candidate evaluation started; a job may still be active. File repair is blocked until a later evaluation request is confirmed."
                        : "A PaRuX-managed file has matching repository, revision and file provenance. Core rechecks current bytes before evaluation; use Verify / repair pinned file if that check fails."
                    : textGguf
                        ? !pinnedSourceValid
                            ? "The Candidate's pinned source identity could not be confirmed. Refresh metadata; no download or evaluation is available."
                            : !metadataTimeValid
                                ? "Refresh Candidate metadata before downloading so the exact licence observation can be recorded."
                                : candidateDownloadActive
                                    ? "A PaRuX download for this file and revision is already active. Evaluation becomes available after exact provenance is verified."
                                    : candidateDownloadCompleted
                                        ? "A prior transfer reports completion, but this file's Candidate provenance is not currently verified. The pinned download can be retried; Core will reuse it only if its exact bytes still match."
                                    : "No managed file matches this exact revision yet. Downloading is a separate, explicit action and will use the pinned file plus the current licence decision. Evaluation stays disabled until provenance and bytes match."
                        : "This evaluation workflow currently supports text GGUF files only.";
                card.Children.Add(new TextBlock { Text = readiness, TextWrapping = TextWrapping.Wrap, FontSize = 12 });

                var jobText = new TextBlock
                {
                    Text = FormatCandidateJobStatus(jobStatus),
                    TextWrapping = TextWrapping.Wrap
                };
                if (safeKey)
                    AutomationProperties.SetAutomationId(jobText, "ModelHub_CandidateStatus_" + candidate.Key);
                card.Children.Add(jobText);

                if (managedMatch != null && safeKey)
                {
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    if (candidateDownloadActive || candidateDownloadStarting
                        || _candidateDownloadUnknown.Contains(candidate.Key))
                    {
                        var verifying = new Button
                        {
                            Content = _candidateDownloadUnknown.Contains(candidate.Key) ? "Download status unavailable"
                                : candidateDownloadStarting ? "Starting pinned verification…" : "Pinned transfer in progress…",
                            IsEnabled = false
                        };
                        actions.Children.Add(verifying);
                    }
                    else if (candidateEvaluationActive && _candidateJobIds.ContainsKey(candidate.Key))
                    {
                        bool cancelling = _candidateCancelInFlight.Contains(candidate.Key)
                            || NormalizeCandidateJobValue(jobStatus!.Status) == "cancelling";
                        var cancel = new Button
                        {
                            Content = cancelling ? "Cancelling…" : "Cancel evaluation",
                            IsEnabled = !cancelling
                        };
                        AutomationProperties.SetAutomationId(cancel, "ModelHub_CandidateCancel_" + candidate.Key);
                        cancel.Click += async (_, _) => await CancelCandidateEvaluationAsync(candidate.Key);
                        actions.Children.Add(cancel);
                    }
                    else if (candidateEvaluationActive)
                    {
                        actions.Children.Add(new Button
                        {
                            Content = "Evaluation status unavailable",
                            IsEnabled = false
                        });
                    }
                    else
                    {
                        bool startUnconfirmed = string.Equals(
                            jobStatus?.ErrorCode, "invalid_job_identity", StringComparison.Ordinal);
                        bool requestUnconfirmed = string.Equals(
                            jobStatus?.ErrorCode, "start_unconfirmed", StringComparison.Ordinal);
                        var start = new Button
                        {
                            Content = startUnconfirmed ? "Start status unavailable"
                                : requestUnconfirmed ? "Retry evaluation request"
                                : jobStatus == null ? "Evaluate locally" : "Evaluate again",
                            IsEnabled = !startUnconfirmed && !anotherJobActive && !_candidateEvaluationStartInFlight
                                && !anyCandidateUtilityComparisonInFlight && !anyCandidateIgnoreInFlight
                        };
                        AutomationProperties.SetAutomationId(start, "ModelHub_CandidateEvaluate_" + candidate.Key);
                        start.Click += async (_, _) => await StartCandidateEvaluationAsync(candidate, managedMatch.FileName);
                        actions.Children.Add(start);
                    }
                    if (!candidateDownloadActive && !candidateDownloadStarting
                        && !_candidateDownloadUnknown.Contains(candidate.Key) && !candidateEvaluationActive
                        && !candidateEvaluationUncertain && !anotherJobActive
                        && !_candidateEvaluationStartInFlight && !anyCandidateUtilityComparisonInFlight
                        && !anyCandidateIgnoreInFlight)
                    {
                        var verify = new Button { Content = "Verify / repair pinned file" };
                        AutomationProperties.SetAutomationId(verify, "ModelHub_CandidateDownload_" + candidate.Key);
                        verify.Click += async (_, _) => await DownloadCandidateAsync(candidate);
                        actions.Children.Add(verify);
                    }

                    bool completedEligibleEvaluation = jobStatus != null
                        && NormalizeCandidateJobValue(jobStatus.Status) == "completed"
                        && jobStatus.Eligibility == "EligibleForFurtherReview"
                        && string.Equals(jobStatus.CandidateKey, candidate.Key, StringComparison.Ordinal);
                    bool utilityPolicyReady = _candidateUtilityPolicy is
                        { Configured: true, Readiness: "WellFormed", Policy: not null };
                    var compare = new Button
                    {
                        Content = candidateUtilityComparisonInFlight
                            ? "Comparing stored evidence…"
                            : "Compare with active model",
                        IsEnabled = utilityPolicyReady && completedEligibleEvaluation
                            && !candidateEvaluationActive && !candidateEvaluationUncertain
                            && !candidateDownloadActive && !candidateDownloadStarting
                            && !_candidateDownloadUnknown.Contains(candidate.Key)
                            && !_candidateDownloadInFlight.Contains(candidate.Key)
                            && !_candidateEvaluationStartInFlight && !anyCandidateUtilityComparisonInFlight
                            && !_candidateUtilityPolicyUnavailable && !_candidateUtilityPolicyMutationInFlight
                            && !anyCandidateIgnoreInFlight
                    };
                    AutomationProperties.SetAutomationId(compare, "ModelHub_CandidateCompare_" + candidate.Key);
                    compare.Click += async (_, _) => await CompareCandidateUtilityAsync(candidate.Key);
                    actions.Children.Add(compare);
                    if (_candidatePromotable.Contains(candidate.Key) && _recommendationStatus is { CanPromote: true }
                        && CandidateManagedFileName(candidate) is { } promoteFile)
                    {
                        var promote = new Button
                        {
                            Content = "Make recommended for Chat…",
                            IsEnabled = !_recommendationTransitionInFlight && !anyCandidateUtilityComparisonInFlight
                        };
                        AutomationProperties.SetAutomationId(promote, "ModelHub_CandidatePromote_" + candidate.Key);
                        promote.Click += async (_, _) => await PromoteCandidateAsync(candidate.Key, promoteFile);
                        actions.Children.Add(promote);
                    }
                    if (_recommendationStatus is { CanRollback: true, ActiveCandidateKey: { } activeKey }
                        && string.Equals(activeKey, candidate.Key, StringComparison.Ordinal))
                    {
                        var rollback = new Button
                        {
                            Content = "Go back to the previous recommendation…",
                            IsEnabled = !_recommendationTransitionInFlight
                        };
                        AutomationProperties.SetAutomationId(rollback, "ModelHub_CandidateRollback_" + candidate.Key);
                        rollback.Click += async (_, _) => await RollbackRecommendationAsync(candidate.Key);
                        actions.Children.Add(rollback);
                    }
                    card.Children.Add(actions);
                }

                else if (textGguf && safeKey)
                {
                    bool canDownload = pinnedSourceValid && metadataTimeValid && !candidateDownloadActive
                        && !_candidateDownloadUnknown.Contains(candidate.Key)
                        && !candidateEvaluationActive && !candidateEvaluationUncertain
                        && !_candidateEvaluationStartInFlight && !anyCandidateIgnoreInFlight;
                    bool starting = candidateDownloadStarting;
                    var download = new Button
                    {
                        Content = starting ? "Preparing download…"
                            : _candidateDownloadUnknown.Contains(candidate.Key) ? "Download status unavailable"
                            : candidateDownloadCompleted ? "Retry pinned download"
                            : "Download pinned Candidate",
                        IsEnabled = canDownload && !starting
                    };
                    AutomationProperties.SetAutomationId(download, "ModelHub_CandidateDownload_" + candidate.Key);
                    download.Click += async (_, _) => await DownloadCandidateAsync(candidate);
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    actions.Children.Add(download);
                    card.Children.Add(actions);
                }

                if (safeKey)
                {
                    var ignore = new Button
                    {
                        Content = candidateIgnoreInFlight ? "Ignoring Candidate…" : "Ignore Candidate",
                        IsEnabled = !candidateIgnoreInFlight && !_candidateIgnoreInFlight.Any()
                            && !candidateDownloadActive && !candidateDownloadStarting
                            && !_candidateDownloadUnknown.Contains(candidate.Key)
                            && !candidateEvaluationActive && !candidateEvaluationUncertain
                            && !_candidateEvaluationStartInFlight && !candidateUtilityComparisonInFlight
                    };
                    AutomationProperties.SetAutomationId(ignore, "ModelHub_CandidateIgnore_" + candidate.Key);
                    ToolTipService.SetToolTip(ignore,
                        "Hide this Candidate from the active list. This does not delete a model file or evaluation evidence.");
                    ignore.Click += async (_, _) => await IgnoreCandidateAsync(candidate.Key);
                    var lifecycleActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    lifecycleActions.Children.Add(ignore);
                    card.Children.Add(lifecycleActions);
                }

                if (_candidateUtilityResultMessages.TryGetValue(candidate.Key, out string? comparisonMessage))
                {
                    var comparisonText = new TextBlock { Text = comparisonMessage, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    AutomationProperties.SetAutomationId(comparisonText, "ModelHub_CandidateUtilityResult_" + candidate.Key);
                    card.Children.Add(comparisonText);
                }
                if (_candidateIgnoreResultMessages.TryGetValue(candidate.Key, out string? ignoreMessage))
                {
                    var ignoreText = new TextBlock { Text = ignoreMessage, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    AutomationProperties.SetAutomationId(ignoreText, "ModelHub_CandidateIgnoreResult_" + candidate.Key);
                    card.Children.Add(ignoreText);
                }
                _candidates.Children.Add(new Border
                {
                    Padding = new Thickness(12),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Child = card
                });
            }
        }

        private void RenderPendingCandidateNotices(ModelDiscoveryCandidate[] activeCandidates)
        {
            DateTime nowUtc = DateTime.UtcNow;
            ModelDiscoveryCandidate[] allPendingNotices = activeCandidates
                .Where(candidate => IsCandidateNoticePending(candidate, nowUtc))
                .OrderByDescending(candidate => candidate.FirstSeenUtc)
                .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                .ToArray();
            if (allPendingNotices.Length == 0)
            {
                _candidateNoticeVisibleCount = CandidateNoticePageSize;
                return;
            }
            ModelDiscoveryCandidate[] notices = allPendingNotices.Take(_candidateNoticeVisibleCount).ToArray();

            var group = new StackPanel { Spacing = 10 };
            var heading = new TextBlock
            {
                Text = allPendingNotices.Length == 1
                    ? "A new model candidate was found"
                    : allPendingNotices.Length + " new model candidates were found · showing " + notices.Length,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            AutomationProperties.SetAutomationId(heading, "ModelHub_CandidateNoticeGroup");
            group.Children.Add(heading);
            group.Children.Add(new TextBlock
            {
                Text = "These are unverified publisher listings, not recommendations. PaRuX will not download, evaluate, or change routing unless you choose an action. The notice is grouped here; review each exact revision below.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8
            });

            bool anotherJobActive = _candidateJobStatuses.Any(pair =>
                IsCandidateJobActive(pair.Value) && !IsCandidateEvaluationFixtureKey(pair.Key));
            foreach (ModelDiscoveryCandidate candidate in notices)
            {
                bool safeKey = IsSafeCandidateKey(candidate.Key);
                string? managedFileName = CandidateManagedFileName(candidate);
                ManagedModelInfo? managedMatch = managedFileName?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true
                    ? FindMatchingManagedCandidate(candidate, managedFileName)
                    : null;
                bool decisionInFlight = _candidateNoticeDecisionInFlight.Contains(candidate.Key);
                var card = new StackPanel { Spacing = 6 };
                var identity = new TextBlock
                {
                    Text = LimitDisplay(candidate.RepoId, 160) + " · " + LimitDisplay(candidate.RepoFilePath, 200)
                        + " · revision " + LimitDisplay(candidate.Revision, 48),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                if (safeKey) AutomationProperties.SetAutomationId(identity, "ModelHub_CandidateNotice_" + candidate.Key);
                card.Children.Add(identity);

                string potential = string.IsNullOrWhiteSpace(candidate.DeclaredPipelineTag)
                    ? "Publisher has not declared a task type."
                    : "Publisher-declared task type: " + LimitDisplay(candidate.DeclaredPipelineTag, 60) + " (unverified).";
                string size = candidate.DeclaredSizeBytes is > 0
                    ? " Publisher-declared download size: " + candidate.DeclaredSizeBytes.Value.ToString("N0", CultureInfo.InvariantCulture) + " bytes."
                    : " Download size is not declared.";
                string license = string.IsNullOrWhiteSpace(candidate.DeclaredLicense)
                    ? " Licence is not declared; any download still requires the separate licence review."
                    : " Publisher-declared licence: " + LimitDisplay(candidate.DeclaredLicense, 80)
                        + "; verify it before any separate download.";
                card.Children.Add(new TextBlock
                {
                    Text = potential + size + license,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.78
                });
                if (managedMatch == null)
                {
                    var readiness = new TextBlock
                    {
                        Text = "This exact revision is not installed and verified yet. If you choose to proceed, use the separate pinned-download action below; its licence review remains required. This notice will not start a transfer.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12
                    };
                    if (safeKey) AutomationProperties.SetAutomationId(readiness, "ModelHub_CandidateNoticeReadiness_" + candidate.Key);
                    card.Children.Add(readiness);
                }

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (safeKey)
                {
                    var evaluate = new Button
                    {
                        Content = decisionInFlight ? "Updating notice…" : "Evaluate now",
                        IsEnabled = !decisionInFlight && !_candidateEvaluationStartInFlight
                            && !anotherJobActive && managedMatch != null
                    };
                    AutomationProperties.SetAutomationId(evaluate, "ModelHub_CandidateNoticeEvaluate_" + candidate.Key);
                    ToolTipService.SetToolTip(evaluate, managedMatch == null
                        ? "This exact revision is not installed. Use the separate pinned-download action and licence review first; this button never downloads it."
                        : "Run the explicit local evaluation for this already verified managed revision.");
                    evaluate.Click += async (_, _) => await EvaluateCandidateFromNoticeAsync(candidate.Key);
                    actions.Children.Add(evaluate);

                    var later = new Button
                    {
                        Content = decisionInFlight ? "Updating notice…" : "Later",
                        IsEnabled = !decisionInFlight && _candidateIgnoreInFlight.Count == 0
                    };
                    AutomationProperties.SetAutomationId(later, "ModelHub_CandidateNoticeLater_" + candidate.Key);
                    later.Click += async (_, _) => await DeferCandidateNoticeAsync(candidate.Key);
                    actions.Children.Add(later);

                    var ignore = new Button
                    {
                        Content = _candidateIgnoreInFlight.Contains(candidate.Key) ? "Ignoring…" : "Ignore this model",
                        IsEnabled = !decisionInFlight && _candidateIgnoreInFlight.Count == 0
                    };
                    AutomationProperties.SetAutomationId(ignore, "ModelHub_CandidateNoticeIgnore_" + candidate.Key);
                    ignore.Click += async (_, _) => await IgnoreCandidateAsync(candidate.Key);
                    actions.Children.Add(ignore);
                }
                card.Children.Add(actions);

                if (_candidateNoticeResultMessages.TryGetValue(candidate.Key, out string? resultMessage))
                {
                    var result = new TextBlock { Text = resultMessage, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    if (safeKey) AutomationProperties.SetAutomationId(result, "ModelHub_CandidateNoticeResult_" + candidate.Key);
                    card.Children.Add(result);
                }
                group.Children.Add(new Border
                {
                    Padding = new Thickness(10), CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1), Child = card
                });
            }

            if (allPendingNotices.Length > notices.Length)
            {
                int remaining = allPendingNotices.Length - notices.Length;
                var more = new Button
                {
                    Content = "Show more notices (" + remaining.ToString(CultureInfo.InvariantCulture) + " remaining)",
                    IsEnabled = _candidateNoticeVisibleCount < 1000
                };
                AutomationProperties.SetAutomationId(more, "ModelHub_CandidateNoticeShowMore_Button");
                more.Click += (_, _) =>
                {
                    _candidateNoticeVisibleCount = Math.Min(1000, _candidateNoticeVisibleCount + CandidateNoticePageSize);
                    RenderCandidates();
                };
                group.Children.Add(more);
            }
            _candidates.Children.Add(new Border
            {
                Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), Child = group
            });
        }

        private static bool IsCandidateNoticePending(ModelDiscoveryCandidate candidate, DateTime nowUtc) =>
            candidate.NoticePending
            || candidate.NoticeDeferredUntilUtc is { } deferredUntilUtc && deferredUntilUtc <= nowUtc;

        private async Task EvaluateCandidateFromNoticeAsync(string candidateKey)
        {
            if (!IsSafeCandidateKey(candidateKey)) return;
            ModelDiscoveryCandidate? candidate = _discoveryCatalog?.Candidates.FirstOrDefault(item =>
                item.Key == candidateKey && item.Lifecycle == "Candidate" && item.Disposition == "active"
                && IsCandidateNoticePending(item, DateTime.UtcNow));
            if (candidate is null) return;
            string? managedFileName = CandidateManagedFileName(candidate);
            ManagedModelInfo? managedMatch = managedFileName?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true
                ? FindMatchingManagedCandidate(candidate, managedFileName)
                : null;
            if (managedMatch is null)
            {
                _status.Text = "This exact revision is not installed and verified. Download it only through the separate pinned-download and licence-review actions below; no download or evaluation was started.";
                return;
            }
            await StartCandidateEvaluationAsync(candidate, managedMatch.FileName, acknowledgeNoticeOnStart: true);
        }

        private Task DeferCandidateNoticeAsync(string candidateKey) =>
            PersistCandidateNoticeDecisionAsync(candidateKey, defer: true);

        private Task AcknowledgeCandidateNoticeAsync(string candidateKey) =>
            PersistCandidateNoticeDecisionAsync(candidateKey, defer: false);

        private async Task PersistCandidateNoticeDecisionAsync(string candidateKey, bool defer)
        {
            if (!IsSafeCandidateKey(candidateKey) || !_candidateNoticeDecisionInFlight.Add(candidateKey)) return;
            ModelDiscoveryCandidate? currentCandidate = _discoveryCatalog?.Candidates.FirstOrDefault(candidate =>
                candidate.Key == candidateKey && candidate.Lifecycle == "Candidate" && candidate.Disposition == "active");
            if (currentCandidate is null || !IsCandidateNoticePending(currentCandidate, DateTime.UtcNow))
            {
                _candidateNoticeDecisionInFlight.Remove(candidateKey);
                return;
            }

            _candidateNoticeResultMessages.Remove(candidateKey);
            RenderCandidates();
            bool simulatedFixtureDecision = false;
            try
            {
                ModelDiscoveryCatalog authoritativeCatalog;
#if DEBUG
                if (IsCandidateEvaluationFixtureKey(candidateKey))
                {
                    simulatedFixtureDecision = true;
                    currentCandidate.NoticePending = false;
                    currentCandidate.NoticeDeferredUntilUtc = defer
                        ? DateTime.UtcNow + TimeSpan.FromHours(24)
                        : null;
                    authoritativeCatalog = _discoveryCatalog!;
                }
                else
#endif
                {
                    if (defer)
                        await ModelDownloadClient.Current.DeferCandidateNoticeAsync(candidateKey, CancellationToken.None);
                    else
                        await ModelDownloadClient.Current.MarkCandidateNoticeHandledAsync(candidateKey, CancellationToken.None);
                    authoritativeCatalog = await ModelDownloadClient.Current.GetDiscoveryCatalogAsync(CancellationToken.None);
                }

                ModelDiscoveryCandidate? confirmed = authoritativeCatalog.Candidates.FirstOrDefault(candidate =>
                    candidate.Key == candidateKey && candidate.Lifecycle == "Candidate" && candidate.Disposition == "active");
                bool confirmedDecision = confirmed != null && !confirmed.NoticePending
                    && (defer
                        ? confirmed.NoticeDeferredUntilUtc is { } deferredUntilUtc && deferredUntilUtc > DateTime.UtcNow
                        : confirmed.NoticeDeferredUntilUtc is null);
                if (confirmedDecision)
                {
                    _discoveryCatalog = authoritativeCatalog;
                    _candidateNoticeResultMessages.Remove(candidateKey);
                    _status.Text = simulatedFixtureDecision
                        ? defer
                            ? "UI fixture: the exact-revision notice was deferred for 24 hours; no download or evaluation was started."
                            : "UI fixture: the simulated evaluation was acknowledged; no model was run and no routing change was made."
                        : defer
                            ? "This exact revision will be shown again in 24 hours. A newer revision can be surfaced sooner. No download or evaluation was started."
                            : "The notice was cleared after evaluation was accepted. No recommendation, promotion, or routing change was made.";
                }
                else
                {
                    _candidateNoticeResultMessages[candidateKey] = "PaRuX Core did not confirm this notice decision. The Candidate remains visible and retryable.";
                    _status.Text = "The Candidate notice decision was not confirmed; it remains available for retry.";
                }
            }
            catch (Exception)
            {
                _candidateNoticeResultMessages[candidateKey] = "Notice decision could not be confirmed. The Candidate remains visible; retry is safe.";
                _status.Text = "PaRuX Core did not confirm the Candidate notice decision. Nothing was downloaded.";
            }
            finally
            {
                _candidateNoticeDecisionInFlight.Remove(candidateKey);
                RenderCandidates();
            }
        }

        private async Task CompareCandidateUtilityAsync(string candidateKey)
        {
            if (_candidateUtilityCompareInFlight.Count > 0 || _candidateUtilityPolicyUnavailable
                || _candidateUtilityPolicyMutationInFlight) return;
            long requestId = ++_candidateUtilityRequestId;
            long stateVersion = _candidateUtilityStateVersion;
            _candidateUtilityCompareInFlight[candidateKey] = requestId;
            _candidateUtilityResultMessages.Remove(candidateKey);
            RenderCandidates();
            RenderCandidateUtilityPolicyStatus();
            try
            {
                ModelDiscoveryCandidate? candidate = _discoveryCatalog?.Candidates.FirstOrDefault(item =>
                    string.Equals(item.Key, candidateKey, StringComparison.Ordinal)
                    && item.Lifecycle == "Candidate" && item.Disposition == "active");
                string? managedFileName = candidate == null ? null : CandidateManagedFileName(candidate);
                ManagedModelInfo? managedMatch = candidate == null || managedFileName == null
                    ? null : FindMatchingManagedCandidate(candidate, managedFileName);
                bool eligible = _candidateJobStatuses.TryGetValue(candidateKey, out CandidateEvaluationJobStatus? job)
                    && NormalizeCandidateJobValue(job.Status) == "completed"
                    && job.Eligibility == "EligibleForFurtherReview"
                    && string.Equals(job.CandidateKey, candidateKey, StringComparison.Ordinal);
                if (candidate == null || managedMatch == null || !eligible
                    || _candidateDownloadUnknown.Contains(candidateKey)
                    || _candidateDownloadInFlight.Contains(candidateKey)
                    || _candidateEvaluationStartInFlight
                    || _candidateUtilityPolicy is not { Configured: true, Readiness: "WellFormed", Policy: not null })
                {
                    if (stateVersion == _candidateUtilityStateVersion)
                        _candidateUtilityResultMessages[candidateKey] = "Comparison unavailable: current managed provenance, eligible evaluation evidence or explicit policy readiness could not be confirmed. No model action occurred.";
                    return;
                }

                CandidateUtilityComparisonResponse response = await ModelDownloadClient.Current
                    .CompareCandidateUtilityAsync(new CandidateUtilityComparisonRequest(candidateKey, managedMatch.FileName),
                        _refreshCts?.Token ?? CancellationToken.None);
                if (!response.RequiresHumanReview || response.IsRecommendation || response.PromotionPerformed
                    || response.RoutingChanged)
                    throw new InvalidDataException("Core returned an invalid Candidate comparison authority contract.");

                bool marginMet = string.Equals(response.Disposition, "CandidateMeetsReferenceMargin", StringComparison.Ordinal);
                string result = marginMet
                    ? "Configured evidence margin met for " + LimitDisplay(response.Capability, 80) + "; requires human review. This is not a recommendation."
                    : "No comparison winner (" + LimitDisplay(response.ReasonCode, 80) + "). This is not a recommendation."
                        + (CandidateReasonText.Explain(response.ReasonCode) is { Length: > 0 } why && why != response.ReasonCode.Replace('_', ' ')
                            ? " " + LimitDisplay(why, 400) : string.Empty);
                if (response.ManualPinReason is { Length: > 0 } pinReason)
                    result += " " + LimitDisplay(CandidateReasonText.Explain(pinReason), 400);
                if (stateVersion == _candidateUtilityStateVersion)
                    _candidateUtilityResultMessages[candidateKey] = result + " No model was promoted and routing did not change.";
                _candidatePromotable.Remove(candidateKey);
                if (marginMet)
                {
                    try
                    {
                        _recommendationStatus = await ModelDownloadClient.Current.GetCandidateRecommendationStatusAsync(
                            _refreshCts?.Token ?? CancellationToken.None);
                        if (_recommendationStatus.CanPromote && stateVersion == _candidateUtilityStateVersion)
                            _candidatePromotable.Add(candidateKey);
                    }
                    catch (Exception) { _recommendationStatus = null; }
                }
            }
            catch (Exception ex)
            {
                if (stateVersion == _candidateUtilityStateVersion)
                    _candidateUtilityResultMessages[candidateKey] = "Comparison unavailable: "
                        + LimitDisplay(ex.Message, 120) + ". No model was promoted and routing did not change.";
            }
            finally
            {
                if (_candidateUtilityCompareInFlight.TryGetValue(candidateKey, out long currentRequestId)
                    && currentRequestId == requestId)
                    _candidateUtilityCompareInFlight.Remove(candidateKey);
                RenderCandidates();
                RenderCandidateUtilityPolicyStatus();
            }
        }

#if DEBUG
        private void SimulateRecommendationFixture(bool canPromote, bool canRollback)
        {
            string key = new string('a', 64);
            _recommendationStatus = new CandidateRecommendationStatusResponse(2, canRollback ? "Active" : "None",
                canRollback ? key : null, canRollback ? "fixture.gguf" : null, null, null, canRollback, false,
                canPromote || canRollback ? null : "manual_pin_scoped",
                canPromote || canRollback ? null : CandidateReasonText.Explain("manual_pin_scoped"),
                false, canPromote, canRollback);
            _candidatePromotable.Clear();
            if (canPromote || !canRollback) _candidatePromotable.Add(key);
            RenderCandidates();
        }
#endif

        /// <summary>Marks a transition busy for the whole dialog so a second click cannot open a second ContentDialog;
        /// cleared afterwards (the transition itself sets it again while it runs).</summary>
        private async Task<bool> ConfirmRecommendationTransitionAsync(ContentDialog confirm)
        {
            _recommendationTransitionInFlight = true;
            try { return await confirm.ShowAsync() == ContentDialogResult.Primary; }
            finally { _recommendationTransitionInFlight = false; }
        }

        private async Task PromoteCandidateAsync(string candidateKey, string managedFileName)
        {
            if (_recommendationTransitionInFlight || _recommendationStatus is not { CanPromote: true } status) return;
            var confirm = new ContentDialog
            {
                Title = "Make this the recommended model for Chat?",
                Content = "Automatic mode (Balanced, Standard) will prefer this model. The model running now is replaced, and you can go back"
                    + " to the previous one. Your own model choices are never changed, and nothing is promoted unless every check still passes.",
                PrimaryButtonText = "Make recommended",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (!await ConfirmRecommendationTransitionAsync(confirm)) return;
            await RunRecommendationTransitionAsync(candidateKey,
                () => ModelDownloadClient.Current.PromoteCandidateRecommendationAsync(
                    new PromoteCandidateRecommendationRequest(candidateKey, managedFileName, status.Generation, true),
                    CancellationToken.None));
        }

        private async Task RollbackRecommendationAsync(string candidateKey)
        {
            if (_recommendationTransitionInFlight || _recommendationStatus is not { CanRollback: true } status) return;
            var confirm = new ContentDialog
            {
                Title = "Go back to the previous recommendation?",
                Content = "The previously recommended model is loaded again after its files and settings are re-checked."
                    + " Your own model choices are never changed.",
                PrimaryButtonText = "Go back",
                CloseButtonText = "Keep current",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (!await ConfirmRecommendationTransitionAsync(confirm)) return;
            await RunRecommendationTransitionAsync(candidateKey,
                () => ModelDownloadClient.Current.RollbackCandidateRecommendationAsync(
                    new RollbackCandidateRecommendationRequest(status.Generation, true), CancellationToken.None));
        }

        private async Task RunRecommendationTransitionAsync(string candidateKey,
            Func<Task<CandidateRecommendationTransitionResponse>> send)
        {
            _recommendationTransitionInFlight = true;
            RenderCandidates();
            string message;
            try
            {
                CandidateRecommendationTransitionResponse response = await send();
                message = string.Equals(response.Outcome, "Committed", StringComparison.Ordinal)
                    ? "Recommendation updated."
                    : LimitDisplay(response.Message, 400);
            }
            catch (Exception ex) { message = "Recommendation change unavailable: " + LimitDisplay(ex.Message, 120) + ". Nothing was changed."; }
            finally { _candidatePromotable.Clear(); }
            try { _recommendationStatus = await ModelDownloadClient.Current.GetCandidateRecommendationStatusAsync(CancellationToken.None); }
            catch (Exception) { _recommendationStatus = null; }
            _candidateUtilityResultMessages[candidateKey] = message;
            _recommendationTransitionInFlight = false;
            RenderCandidates();
        }

        private async Task IgnoreCandidateAsync(string candidateKey)
        {
            if (!IsSafeCandidateKey(candidateKey) || _candidateIgnoreInFlight.Count > 0
                || _candidateNoticeDecisionInFlight.Contains(candidateKey)
                || _discoveryCatalog?.Candidates.Any(candidate => candidate.Key == candidateKey
                    && candidate.Lifecycle == "Candidate" && candidate.Disposition == "active") != true)
                return;

            InvalidatePendingRefresh();
            _refreshCts?.Dispose();
            var requestCts = new CancellationTokenSource();
            _refreshCts = requestCts;
            CancellationToken ct = requestCts.Token;
            int generation = _refreshGeneration;
            _candidateIgnoreInFlight.Add(candidateKey);
            _candidateIgnoreResultMessages.Remove(candidateKey);
            _candidateIgnoreResultMessages[candidateKey] = "Sending the explicit ignore decision to PaRuX Core…";
            string? completionStatus = null;
            RenderCandidates();

            try
            {
                ModelDiscoveryCatalog authoritativeCatalog;
#if DEBUG
                if (IsCandidateIgnoreFixtureKey(candidateKey))
                {
                    if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_IGNORE_FAILURE"), "1", StringComparison.Ordinal))
                        throw new InvalidOperationException("Synthetic Candidate ignore failure.");

                    if (string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_IGNORE_UNCONFIRMED"), "1", StringComparison.Ordinal))
                    {
                        authoritativeCatalog = new ModelDiscoveryCatalog
                        {
                            SchemaVersion = _discoveryCatalog!.SchemaVersion
                        };
                    }
                    else
                    {
                        ModelDiscoveryCandidate fixture = _discoveryCatalog!.Candidates.Single(candidate => candidate.Key == candidateKey);
                        fixture.Disposition = "ignored";
                        fixture.IgnoredUtc = DateTime.UtcNow;
                        fixture.NoticePending = false;
                        fixture.NoticeDeferredUntilUtc = null;
                        _candidateIgnoreFixtureIgnored.Add(candidateKey);
                        authoritativeCatalog = _discoveryCatalog;
                    }
                }
                else
#endif
                {
                    await ModelDownloadClient.Current.IgnoreCandidateAsync(candidateKey, ct);
                    authoritativeCatalog = await ModelDownloadClient.Current.GetDiscoveryCatalogAsync(ct);
                }

                if (generation != _refreshGeneration || ct.IsCancellationRequested) return;
                bool confirmedIgnored = authoritativeCatalog.Candidates.Any(candidate =>
                    candidate.Key == candidateKey && candidate.Lifecycle == "Candidate" && candidate.Disposition == "ignored");
                if (confirmedIgnored)
                {
                    _discoveryCatalog = authoritativeCatalog;
                    _candidateIgnoreResultMessages.Remove(candidateKey);
                    completionStatus = "Candidate ignored and hidden from the active list. Existing model files and evaluation evidence were not deleted.";
                    _status.Text = completionStatus;
                }
                else
                {
                    _candidateIgnoreResultMessages[candidateKey] =
                        "PaRuX Core did not confirm the ignored disposition. This Candidate remains visible; retry is safe.";
                    _status.Text = "Candidate ignore was not confirmed; the active Candidate remains available.";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (generation == _refreshGeneration)
                {
                    _candidateIgnoreResultMessages[candidateKey] = "Ignore failed: " + LimitDisplay(ex.Message, 120)
                        + ". The Candidate remains visible; retrying this idempotent action is safe.";
                    _status.Text = "Candidate ignore failed. The Candidate remains available; no file or evidence was deleted.";
                }
            }
            finally
            {
                _candidateIgnoreInFlight.Remove(candidateKey);
                if (ReferenceEquals(_refreshCts, requestCts))
                {
                    requestCts.Dispose();
                    _refreshCts = null;
                }
                RenderCandidates();
                if (_refreshAfterCandidateIgnore)
                {
                    _refreshAfterCandidateIgnore = false;
                    Task refreshTask = RefreshAllAsync();
                    int refreshGeneration = _refreshGeneration;
                    await refreshTask;
                    if (completionStatus != null && refreshGeneration == _refreshGeneration)
                        _status.Text = completionStatus;
                }
            }
        }

#if DEBUG
        private static CandidateUtilityPolicyCalibrationContext CreateCandidateUtilityCalibrationContextFixture()
        {
            CandidateEvaluationEnvironment quality = new()
            {
                HardwareFingerprint = "sha256:ui-test-hardware",
                RuntimeFingerprint = "ui-test-runtime:build-1",
                BinaryFingerprint = "sha256:" + new string('a', 64),
                ConfigurationFingerprint = "sha256:" + new string('b', 64),
                WorkloadFingerprint = "ui-test-quality-workload"
            };
            CandidateEvaluationEnvironment performance = new()
            {
                HardwareFingerprint = quality.HardwareFingerprint,
                RuntimeFingerprint = quality.RuntimeFingerprint,
                BinaryFingerprint = quality.BinaryFingerprint,
                ConfigurationFingerprint = quality.ConfigurationFingerprint,
                WorkloadFingerprint = "ui-test-performance-workload"
            };
            return new CandidateUtilityPolicyCalibrationContext(
                "ui-test-context-fingerprint",
                Capability: "chat.instruction_following.smoke",
                QualitySuiteId: "ui-test-suite",
                QualitySuiteVersion: 1,
                QualityProfileId: "ui-test-profile",
                QualityProfileVersion: 1,
                QualityProfileSha256: new string('c', 64),
                PerformanceWorkloadId: "ui-test-performance-workload",
                PerformanceWorkloadVersion: 1,
                PerformanceWorkloadSha256: new string('d', 64),
                QualityEnvironment: quality,
                PerformanceEnvironment: performance,
                MaximumQualityRunCount: 127,
                MaximumMarginBasisPoints: 10000);
        }

        private void SimulateCandidateUtilityPolicyReadyFixture()
        {
            // Synthetic, in-memory UI state only. It is never saved or used as calibration evidence.
            _candidateUtilityPolicy = new CandidateUtilityPolicyStatus(
                Configured: true,
                Readiness: "WellFormed",
                ReasonCode: "ui_test_fixture",
                Policy: new CandidateUtilityPolicyDefinition
                {
                    SchemaVersion = CandidateUtilityPolicyDefinition.CurrentSchemaVersion,
                    PolicyId = "ui-test-only",
                    Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability,
                    QualitySuiteId = CandidateEvaluationScope.ChatInstructionFollowingSuiteId,
                    QualitySuiteVersion = CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion,
                    QualityEvidenceProfileId = "ui-test-only-profile",
                    QualityEvidenceProfileVersion = 1,
                    QualityEvidenceProfileSha256 = new string('a', 64),
                    PerformanceWorkloadId = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId,
                    PerformanceWorkloadVersion = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion,
                    HardwareFingerprint = new string('b', 64),
                    RuntimeFingerprint = new string('c', 64),
                    BinaryFingerprint = new string('d', 64),
                    ConfigurationFingerprint = new string('e', 64),
                    MinimumQualityPassRateBasisPoints = 10000,
                    MinimumQualityReliabilityBasisPoints = 10000,
                    QualityReliabilityEstimatorId = CandidateQualityReliabilityEstimator.FreshProcessSuitePassRateV1,
                    MinimumQualityRunCount = 5,
                    PerformanceMetricId = CandidatePerformanceMetric.DecodeTokensPerSecondId,
                    PerformanceMetricUnit = CandidatePerformanceMetric.TokensPerSecondUnit,
                    HigherPerformanceMetricIsBetter = true,
                    MinimumUtilityMarginBasisPoints = 100
                });
            _candidateUtilityPolicyUnavailable = false;
            RenderCandidateUtilityPolicyStatus();
            RenderCandidates();
            _status.Text = "UI fixture only: explicit policy readiness is synthetic; no comparison or model action occurred.";
        }

        private void SimulateCandidateUtilityComparisonInFlightFixture()
        {
            string candidateKey = new string('a', 64);
            _candidateUtilityCompareInFlight[candidateKey] = ++_candidateUtilityRequestId;
            RenderCandidates();
            _status.Text = "UI fixture only: Candidate evidence comparison is marked in progress; no Core request or model action occurred.";
        }
#endif

        private ManagedModelInfo? FindMatchingManagedCandidate(ModelDiscoveryCandidate candidate, string managedFileName)
        {
            return _managedModels.FirstOrDefault(model =>
            {
                ModelDownloadProvenance? provenance = model.Provenance;
                return provenance != null
                    && string.Equals(model.FileName, managedFileName, StringComparison.Ordinal)
                    && model.SizeBytes > 0
                    && provenance.SizeBytes == model.SizeBytes
                    && string.Equals(candidate.RepoId, provenance.RepoId, StringComparison.Ordinal)
                    && string.Equals(candidate.Revision, provenance.Revision, StringComparison.Ordinal)
                    && string.Equals(candidate.RepoFilePath, provenance.RepoFilePath, StringComparison.Ordinal)
                    && string.Equals(candidate.PinnedUrl, provenance.PinnedSourceUrl, StringComparison.Ordinal)
                    && IsSha256(provenance.ComputedSha256);
            });
        }

        private static string? CandidateManagedFileName(ModelDiscoveryCandidate candidate)
        {
            string path = candidate.RepoFilePath;
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/'))
                return null;
            string[] segments = path.Split('/');
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
                return null;
            string fileName = segments[^1];
            return fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 ? fileName : null;
        }

        private static bool IsCandidatePinnedSourceValid(ModelDiscoveryCandidate candidate, string managedFileName)
        {
            if (candidate.Provider != "huggingface" || candidate.ProviderAdapterVersion != 1
                || candidate.Lifecycle != "Candidate" || candidate.Disposition != "active"
                || candidate.IntegrityStatus != "unverified" || !IsSafeCandidateKey(candidate.Key)
                || ModelRevisionIdentity.Normalize(candidate.Revision) is not { } revision
                || !string.Equals(revision, candidate.Revision, StringComparison.Ordinal)
                || !IsSafeCandidateRepoId(candidate.RepoId) || !IsSafeCandidateRepoFilePath(candidate.RepoFilePath)
                || !string.Equals(Path.GetFileName(candidate.RepoFilePath), managedFileName, StringComparison.Ordinal)
                || !candidate.RepoFilePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                return false;

            string escapedPath = string.Join('/', candidate.RepoFilePath.Split('/').Select(Uri.EscapeDataString));
            string expectedUrl = "https://huggingface.co/" + candidate.RepoId + "/resolve/"
                + revision + "/" + escapedPath;
            return string.Equals(candidate.PinnedUrl, expectedUrl, StringComparison.Ordinal);
        }

        private static bool IsSafeCandidateRepoId(string repoId)
        {
            string[] parts = repoId.Split('/');
            return parts.Length == 2 && parts.All(part => part.Length is >= 1 and <= 96
                && char.IsAsciiLetterOrDigit(part[0]) && char.IsAsciiLetterOrDigit(part[^1])
                && !part.Contains("..", StringComparison.Ordinal)
                && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        }

        private static bool IsSafeCandidateRepoFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\')) return false;
            string[] parts = path.Split('/');
            return parts.Length is >= 1 and <= 16 && parts.All(part => part.Length is >= 1 and <= 240
                && part is not "." and not ".." && part == part.Trim() && part[^1] != '.'
                && part.All(character => !char.IsControl(character) && character is not ':' and not '<' and not '>'
                    and not '"' and not '|' and not '?' and not '*' and not '#' and not '%'));
        }

        private static bool IsSafeCandidateKey(string key) => key.Length == 64
            && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static bool IsSha256(string? value) => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

        private static string LimitDisplay(string? value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            string normalized = value.Trim();
            return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength] + "…";
        }

        private async Task DownloadCandidateAsync(ModelDiscoveryCandidate selectedCandidate)
        {
            if (_candidateUtilityCompareInFlight.Count > 0)
            {
                _status.Text = "Candidate file changes are blocked while an evidence comparison is running.";
                return;
            }
            if (IsCandidateEvaluationFileMutationBlocked(selectedCandidate.Key))
            {
                _status.Text = "Candidate file change is blocked while evaluation may be active or unconfirmed.";
                RenderCandidates();
                return;
            }
            if (!_candidateDownloadInFlight.Add(selectedCandidate.Key)) return;
            InvalidateCandidateUtilityComparisons();
            RenderCandidates();
            try
            {
                ModelDiscoveryCandidate? candidate = _discoveryCatalog?.Candidates.FirstOrDefault(item =>
                    string.Equals(item.Key, selectedCandidate.Key, StringComparison.Ordinal)
                    && item.Lifecycle == "Candidate" && item.Disposition == "active");
                string? managedFileName = candidate == null ? null : CandidateManagedFileName(candidate);
                if (candidate == null || managedFileName == null
                    || !IsCandidatePinnedSourceValid(candidate, managedFileName))
                {
                    _status.Text = "Candidate identity changed or could not be verified. Refresh candidates; no download was started.";
                    return;
                }

                if (_discoveryCatalog?.LastSuccessUtc is not DateTime metadataObservedUtc
                    || metadataObservedUtc.Kind != DateTimeKind.Utc)
                {
                    _status.Text = "Candidate licence metadata has no verified observation time. Refresh candidates before downloading.";
                    return;
                }

                ModelLicenseConsentPolicy.Decision licenseDecision = ModelLicenseConsentPolicy.Evaluate(
                    candidate.DeclaredLicense, metadataRetrieved: true, metadataObservedUtc: metadataObservedUtc);
#if DEBUG
                _candidateStagingFixtureTransferAccepted = false;
#endif
                bool queued = await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                    XamlRoot,
                    candidate.RepoId,
                    managedFileName,
                    licenseDecision,
                    candidate.Revision,
                    async (revision, licenseEvidence) =>
                    {
#if DEBUG
                        if (IsCandidateStagingFixtureKey(candidate.Key))
                        {
                            StageCandidateFixtureInMemory(candidate, managedFileName, licenseEvidence);
                            _candidateStagingFixtureTransferAccepted = true;
                            return;
                        }
#endif
                        SetBusy(true, "Starting pinned Candidate download…");
                        try
                        {
                            if (!string.Equals(revision, candidate.Revision, StringComparison.Ordinal))
                                throw new InvalidDataException("The reviewed Candidate revision changed before the download started.");
                            await ModelDownloadClient.Current.StartCandidateDownloadAsync(
                                candidate.Key, licenseEvidence, CancellationToken.None);
                        }
                        finally { SetBusy(false, string.Empty); }
                    });

                if (!queued)
                {
                    _status.Text = "Candidate download cancelled. No transfer was started.";
                    return;
                }

#if DEBUG
                if (_candidateStagingFixtureTransferAccepted)
                {
                    RenderCandidates();
                    _status.Text = "UI fixture staged a synthetic managed Candidate after consent. No network transfer or model execution occurred.";
                    return;
                }
#endif
                await RefreshAllAsync();
                _status.Text = "Pinned Candidate download queued. Evaluation remains disabled until the completed file's provenance and bytes match.";
            }
            catch (Exception ex)
            {
                bool reconciled = await ReconcileCandidateDownloadStateAsync(selectedCandidate.Key);
                if (!reconciled)
                    _candidateDownloadUnknown.Add(selectedCandidate.Key);
                _status.Text = reconciled
                    ? "Candidate start response was not confirmed. The Core queue and managed library were refreshed; retry is safe if no matching transfer is active."
                    : "Candidate start status is unknown because the Core queue could not be refreshed. PaRuX has blocked another start until Model Hub can reconcile it. Details: "
                        + LimitDisplay(ex.Message, 180);
            }
            finally
            {
                _candidateDownloadInFlight.Remove(selectedCandidate.Key);
                RenderCandidates();
            }
        }

        private async Task<bool> ReconcileCandidateDownloadStateAsync(string candidateKey)
        {
            try
            {
                List<ModelDownloadStatus> downloads = await ModelDownloadClient.Current.ListDownloadsAsync(CancellationToken.None);
                List<ManagedModelInfo> installed = await ModelDownloadClient.Current.ListManagedModelsAsync(CancellationToken.None);
                _downloadJobs = downloads;
                _managedModels = installed;
                _candidateDownloadUnknown.Remove(candidateKey);
                RenderDownloads(downloads);
                RenderInstalled(installed);
                RenderCandidates();
                UpdateDownloadPolling(downloads);
                return true;
            }
            catch
            {
                _candidateDownloadUnknown.Add(candidateKey);
                RenderCandidates();
                return false;
            }
        }

        private async Task StartCandidateEvaluationAsync(
            ModelDiscoveryCandidate candidate, string managedFileName, bool acknowledgeNoticeOnStart = false)
        {
            if (_candidateUtilityCompareInFlight.Count > 0) return;
            if (_candidateEvaluationStartInFlight) return;
            if (_candidateDownloadInFlight.Contains(candidate.Key) || _candidateDownloadUnknown.Contains(candidate.Key)
                || _downloadJobs.Any(job => IsActive(job)
                    && string.Equals(job.FileName, managedFileName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(job.Revision, candidate.Revision, StringComparison.OrdinalIgnoreCase)))
            {
                _status.Text = "Candidate evaluation is blocked while its pinned file is being transferred or reconciled.";
                RenderCandidates();
                return;
            }
            if (_candidateJobStatuses.Any(pair => IsCandidateJobActive(pair.Value)
                && !IsCandidateEvaluationFixtureKey(pair.Key)))
            {
                _status.Text = "Another local Candidate evaluation is already running. Wait for it to finish before starting another.";
                return;
            }

            bool rejectCandidateEvaluationForLicenseEvidence = false;
#if DEBUG
            bool candidateEvaluationFixture = IsCandidateEvaluationFixtureKey(candidate.Key);
            rejectCandidateEvaluationForLicenseEvidence = candidateEvaluationFixture
                && string.Equals(
                    Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_LICENSE_REJECTION"),
                    "metadata_changed", StringComparison.Ordinal);
            if (candidateEvaluationFixture && !rejectCandidateEvaluationForLicenseEvidence)
            {
                InvalidateCandidateUtilityComparisons();
                _candidateJobIds[candidate.Key] = "ui-test-candidate-job";
                _candidateJobStatuses[candidate.Key] = new CandidateEvaluationJobStatus
                {
                    JobId = "ui-test-candidate-job", CandidateKey = candidate.Key,
                    Status = "Running", Phase = "Quality"
                };
                _status.Text = "UI fixture: simulated evaluation started; no model was run.";
                if (acknowledgeNoticeOnStart)
                    await AcknowledgeCandidateNoticeAsync(candidate.Key);
                RenderCandidates();
                return;
            }
#endif

            string? qualityProfileId = rejectCandidateEvaluationForLicenseEvidence
                ? CandidateEvaluationScope.ChatInstructionFollowingQualityProfileId
                : await ChooseCandidateQualityProfileAsync(forPolicyConfiguration: false);
            if (qualityProfileId is null) return;

            InvalidateCandidateUtilityComparisons();
            _candidateEvaluationStartInFlight = true;
            DateTime attemptStartedUtc = DateTime.UtcNow;
            RenderCandidates();
            SetBusy(true, "Starting local Candidate evaluation…");
            try
            {
#if DEBUG
                if (rejectCandidateEvaluationForLicenseEvidence)
                    // Exercise the production UI rejection handler without starting Core or a model.
                    throw new CandidateEvaluationStartRejectedException("candidate_license_metadata_changed");
#endif
                CandidateEvaluationJobAccepted accepted =
                    await ModelDownloadClient.Current.StartCandidateEvaluationJobAsync(
                        candidate.Key, managedFileName, CancellationToken.None, qualityProfileId);
                if (!Guid.TryParseExact(accepted.JobId, "N", out _))
                {
                    _candidateJobIds.Remove(candidate.Key);
                    _candidateJobStatuses[candidate.Key] = new CandidateEvaluationJobStatus
                    {
                        CandidateKey = candidate.Key, Status = "unknown", Phase = "invalid_job_identity",
                        StartedUtc = attemptStartedUtc,
                        ErrorCode = "invalid_job_identity"
                    };
                    _status.Text = "Core response did not contain a usable job identity. Checking retained history before allowing a manual retry.";
                    bool foundJob = await TryReconcileCandidateJobHistoryForStartAsync(candidate.Key);
                    if (foundJob)
                        _status.Text = "Core returned an unusable job identity, but a matching recent job was found and will be tracked without retrying.";
                    else
                        _status.Text = "Core response did not contain a usable job identity; no matching recent job could be confirmed. Status remains uncertain; no automatic retry occurred.";
                    return;
                }
                _candidateJobIds[candidate.Key] = accepted.JobId;
                _candidateJobStatuses.Remove(candidate.Key);
                _candidateStatusRefreshFailures.Remove(candidate.Key);
                ApplyCandidateJobStatus(candidate.Key, new CandidateEvaluationJobStatus
                {
                    JobId = accepted.JobId, CandidateKey = candidate.Key,
                    Status = "Running", Phase = "Quality", StartedUtc = attemptStartedUtc
                });
                _status.Text = "Local Candidate evaluation started. It does not install, recommend, promote, or change routing.";
                if (acknowledgeNoticeOnStart)
                    await AcknowledgeCandidateNoticeAsync(candidate.Key);
                try { await RefreshCandidateEvaluationStatusAsync(candidate.Key, accepted.JobId); }
                catch (Exception)
                {
                    _candidateStatusRefreshFailures.Add(candidate.Key);
                    _status.Text = "Core accepted the evaluation, but its current status could not be checked. The last known state is shown and may be stale.";
                }
            }
            catch (CandidateEvaluationStartRejectedException exception)
            {
                _candidateJobIds.Remove(candidate.Key);
                _candidateJobStatuses[candidate.Key] = new CandidateEvaluationJobStatus
                {
                    CandidateKey = candidate.Key, Status = "Unavailable", Phase = "preflight",
                    StartedUtc = attemptStartedUtc, CompletedUtc = DateTime.UtcNow,
                    ErrorCode = exception.ErrorCode
                };
                _status.Text = CandidateEvaluationStartRejectionMessage(exception.ErrorCode);
            }
            catch (Exception)
            {
                _candidateJobIds.Remove(candidate.Key);
                _candidateJobStatuses[candidate.Key] = new CandidateEvaluationJobStatus
                {
                    CandidateKey = candidate.Key, Status = "unknown", Phase = "start_unconfirmed",
                    StartedUtc = attemptStartedUtc,
                    ErrorCode = "start_unconfirmed"
                };
                _status.Text = "Core did not confirm whether evaluation started. Checking retained job history before allowing a manual retry.";
                bool foundJob = await TryReconcileCandidateJobHistoryForStartAsync(candidate.Key);
                if (foundJob)
                    _status.Text = "The start response was interrupted, but a matching recent evaluation job was found and will be tracked without retrying.";
                else
                    _status.Text = "Core did not confirm whether evaluation started. No matching recent job could be confirmed; status remains unknown and no automatic retry occurred.";
            }
            finally
            {
                _candidateEvaluationStartInFlight = false;
                RenderCandidates();
                SetBusy(false, _status.Text);
                UpdateCandidateEvaluationPolling();
            }
        }

        private async Task<string?> ChooseCandidateQualityProfileAsync(bool forPolicyConfiguration)
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = forPolicyConfiguration
                    ? "Choose which evidence scope to use for explicit comparison criteria. Quick checks are comparison-only. General chat is bounded to standard × balanced text tasks and does not establish coding, other modality or best-in-class quality."
                    : "Choose the evaluation scope to run on this local model. A general-chat qualification run checks 30 held-out standard × balanced text tasks and starts the local runtime once. Promotion requires at least 5 separate fresh-process runs; those must be started individually. This does not establish coding, other modality or best-in-class quality.",
                TextWrapping = TextWrapping.Wrap
            });
            if (forPolicyConfiguration)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "General-chat criteria require at least 90% task quality, 95% fresh-process reliability and 5 distinct runs. This screen only configures criteria; it does not start a model.",
                    TextWrapping = TextWrapping.Wrap
                });
            }
            var dialog = new ContentDialog
            {
                Title = forPolicyConfiguration ? "Choose Candidate evidence scope" : "Choose Candidate evaluation scope",
                Content = content,
                PrimaryButtonText = "General chat qualification (30 cases)",
                SecondaryButtonText = "Quick checks (4-case smoke)",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                MaxWidth = 640
            };
            AutomationProperties.SetAutomationId(dialog,
                forPolicyConfiguration ? "ModelHub_CandidateUtilityScope_Dialog" : "ModelHub_CandidateEvaluationScope_Dialog");
            ContentDialogResult result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => CandidateEvaluationScope.GeneralChatQualityProfileId,
                ContentDialogResult.Secondary => CandidateEvaluationScope.ChatInstructionFollowingQualityProfileId,
                _ => null
            };
        }

        private static string CandidateEvaluationStartRejectionMessage(string errorCode) => errorCode switch
        {
            "candidate_license_evidence_missing" => "Evaluation blocked: this installed revision has no saved licence-review evidence. No evaluation started and the installed file was left unchanged. Use the explicit Candidate terms-review flow before retrying.",
            "candidate_license_metadata_changed" => "Evaluation blocked: the publisher's declared licence no longer matches the saved evidence for this revision. No evaluation started and the installed file was left unchanged. Refresh discovery and review the current pinned model-card terms before retrying.",
            "candidate_license_review_required" => "Evaluation blocked: the required review is not recorded for this exact revision. No evaluation started and the installed file was left unchanged. Review its pinned model-card terms and confirm them through the explicit Candidate flow.",
            "candidate_provenance_unavailable" => "Evaluation blocked: Core could not confirm the exact source and local bytes after restart. No evaluation started and the installed file was left unchanged.",
            _ => "Evaluation was rejected before it started. No model action occurred; refresh Model Hub and retry if appropriate."
        };

        private bool IsCandidateEvaluationFileMutationBlocked(string candidateKey)
        {
            if (_candidateEvaluationStartInFlight) return true;
            if (!_candidateJobStatuses.TryGetValue(candidateKey, out CandidateEvaluationJobStatus? status)) return false;
            return IsCandidateJobActive(status)
                || (!_candidateJobIds.ContainsKey(candidateKey)
                    && status.ErrorCode is "start_unconfirmed" or "invalid_job_identity");
        }

        private async Task CancelCandidateEvaluationAsync(string candidateKey)
        {
            if (!_candidateJobIds.TryGetValue(candidateKey, out string? jobId)) return;
            if (!_candidateJobStatuses.TryGetValue(candidateKey, out CandidateEvaluationJobStatus? currentStatus)
                || !IsCandidateJobActive(currentStatus)
                || !_candidateCancelInFlight.Add(candidateKey)) return;
#if DEBUG
            if (IsCandidateEvaluationFixtureKey(candidateKey))
            {
                currentStatus.Status = "Cancelled";
                currentStatus.Phase = "Done";
                currentStatus.CompletedUtc = DateTime.UtcNow;
                _candidateCancelInFlight.Remove(candidateKey);
                _status.Text = "UI fixture: simulated evaluation cancelled; no model was run.";
                RenderCandidates();
                return;
            }
#endif
            string previousStatus = currentStatus.Status;
            string previousPhase = currentStatus.Phase;
            currentStatus.Status = "Cancelling";
            currentStatus.Phase = "Cancelling";
            RenderCandidates();
            SetBusy(true, "Cancelling local Candidate evaluation…");
            try
            {
                CandidateEvaluationJobStatus? status =
                    await ModelDownloadClient.Current.CancelCandidateEvaluationJobAsync(jobId, CancellationToken.None);
                if (!IsCurrentCandidateJobRequest(candidateKey, jobId)) return;
                if (status == null)
                    await RefreshCandidateEvaluationStatusAsync(candidateKey, jobId);
                else
                    ApplyCandidateJobStatus(candidateKey, status);

                string confirmedState = _candidateJobStatuses.TryGetValue(candidateKey, out CandidateEvaluationJobStatus? latest)
                    ? NormalizeCandidateJobValue(latest.Status) : "unavailable";
                _status.Text = confirmedState switch
                {
                    "cancelled" => "Candidate evaluation cancelled.",
                    "cancelling" or "running" or "queued" => "Cancellation requested. Core is stopping the active evaluation safely.",
                    "completed" => "Core completed the evaluation before cancellation took effect; final results are shown.",
                    "failed" => "Core reports the evaluation had already failed; its sanitized result is shown.",
                    "unavailable" => "Core no longer has this job summary; the in-memory registry may have restarted or expired.",
                    _ => "Core returned an unrecognized state. The displayed status is authoritative for this response."
                };
            }
            catch (Exception)
            {
                if (_candidateJobStatuses.TryGetValue(candidateKey, out CandidateEvaluationJobStatus? latest)
                    && NormalizeCandidateJobValue(latest.Status) == "cancelling")
                {
                    latest.Status = previousStatus;
                    latest.Phase = previousPhase;
                }
                _candidateStatusRefreshFailures.Add(candidateKey);
                _status.Text = "Cancellation could not be confirmed. The last known state is shown and may be stale.";
            }
            finally
            {
                _candidateCancelInFlight.Remove(candidateKey);
                RenderCandidates();
                SetBusy(false, _status.Text);
                UpdateCandidateEvaluationPolling();
            }
        }

        private void UpdateCandidateEvaluationPolling()
        {
            bool hasActiveJob = _candidateJobStatuses.Any(pair =>
                IsCandidateJobActive(pair.Value) && !IsCandidateEvaluationFixtureKey(pair.Key));
            if (!hasActiveJob || Visibility != Visibility.Visible)
            {
                _candidateEvaluationPoll?.Stop();
                return;
            }

            if (_candidateEvaluationPoll == null)
            {
                _candidateEvaluationPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _candidateEvaluationPoll.Tick += async (_, _) => await PollCandidateEvaluationAsync();
            }
            if (!_candidateEvaluationPoll.IsEnabled) _candidateEvaluationPoll.Start();
        }

        private async Task PollCandidateEvaluationAsync()
        {
            if (_candidateEvaluationPollInFlight || Visibility != Visibility.Visible) return;
            KeyValuePair<string, CandidateEvaluationJobStatus> active = _candidateJobStatuses.FirstOrDefault(pair =>
                IsCandidateJobActive(pair.Value) && !IsCandidateEvaluationFixtureKey(pair.Key)
                && _candidateJobIds.ContainsKey(pair.Key));
            if (string.IsNullOrEmpty(active.Key))
            {
                _candidateEvaluationPoll?.Stop();
                return;
            }

            _candidateEvaluationPollInFlight = true;
            string polledJobId = _candidateJobIds[active.Key];
            try
            {
                await RefreshCandidateEvaluationStatusAsync(active.Key, polledJobId);
                RenderCandidates();
                if (_candidateJobStatuses.TryGetValue(active.Key, out CandidateEvaluationJobStatus? status)
                    && NormalizeCandidateJobValue(status.Status) == "unavailable"
                    && status.ErrorCode == "job_status_not_found")
                    _status.Text = "Core no longer has this job summary. The in-memory job registry may have restarted or expired; no recommendation or routing change was made.";
            }
            catch (Exception)
            {
                if (MarkCandidateStatusRefreshFailed(active.Key, polledJobId))
                {
                    RenderCandidates();
                    _status.Text = "Evaluation status could not be refreshed. The last known state is shown and may be stale.";
                }
            }
            finally
            {
                _candidateEvaluationPollInFlight = false;
                UpdateCandidateEvaluationPolling();
            }
        }

        private async Task RefreshCandidateEvaluationStatusAsync(string candidateKey, string? expectedJobId = null)
        {
            if (!_candidateJobIds.TryGetValue(candidateKey, out string? jobId)) return;
            if (expectedJobId != null && !string.Equals(jobId, expectedJobId, StringComparison.Ordinal)) return;
            CandidateEvaluationJobStatus? status =
                await ModelDownloadClient.Current.GetCandidateEvaluationJobAsync(jobId, CancellationToken.None);
            if (!IsCurrentCandidateJobRequest(candidateKey, jobId)) return;
            if (status != null)
            {
                ApplyCandidateJobStatus(candidateKey, status);
                return;
            }

            MarkCandidateJobUnavailable(candidateKey, jobId);
        }

        private bool IsCurrentCandidateJobRequest(string candidateKey, string expectedJobId) =>
            _candidateJobIds.TryGetValue(candidateKey, out string? currentJobId)
            && string.Equals(currentJobId, expectedJobId, StringComparison.Ordinal);

        private bool MarkCandidateStatusRefreshFailed(string candidateKey, string expectedJobId)
        {
            if (!IsCurrentCandidateJobRequest(candidateKey, expectedJobId)) return false;
            _candidateStatusRefreshFailures.Add(candidateKey);
            return true;
        }

        private bool MarkCandidateJobUnavailable(string candidateKey, string expectedJobId)
        {
            if (!IsCurrentCandidateJobRequest(candidateKey, expectedJobId)) return false;
            _candidateJobIds.Remove(candidateKey);
            _candidateStatusRefreshFailures.Remove(candidateKey);
            ApplyCandidateJobStatus(candidateKey, new CandidateEvaluationJobStatus
            {
                JobId = expectedJobId, CandidateKey = candidateKey,
                Status = "unavailable", Phase = "status_lost", ErrorCode = "job_status_not_found",
                CompletedUtc = DateTime.UtcNow
            });
            return true;
        }

        private void ApplyCandidateJobStatus(string candidateKey, CandidateEvaluationJobStatus incoming)
        {
            if (!string.Equals(incoming.CandidateKey, candidateKey, StringComparison.Ordinal))
                throw new InvalidDataException("Core returned a status for a different Candidate.");
            if (_candidateJobIds.TryGetValue(candidateKey, out string? expectedJobId)
                && !string.Equals(incoming.JobId, expectedJobId, StringComparison.Ordinal))
                throw new InvalidDataException("Core returned a different Candidate evaluation job.");

            if (_candidateJobStatuses.TryGetValue(candidateKey, out CandidateEvaluationJobStatus? current)
                && !CanReplaceCandidateJobStatus(current, incoming)) return;
            _candidateJobStatuses[candidateKey] = incoming;
            _candidateStatusRefreshFailures.Remove(candidateKey);
        }

        private static bool CanReplaceCandidateJobStatus(
            CandidateEvaluationJobStatus current, CandidateEvaluationJobStatus incoming)
        {
            string currentState = NormalizeCandidateJobValue(current.Status);
            string incomingState = NormalizeCandidateJobValue(incoming.Status);
            int currentRank = CandidateJobStateRank(currentState);
            int incomingRank = CandidateJobStateRank(incomingState);
            if (currentRank >= 4)
                return currentState == "unavailable"
                    && incomingState is "completed" or "cancelled" or "failed" or "interrupted";
            return incomingRank >= currentRank;
        }

        private static int CandidateJobStateRank(string state) => state switch
        {
            "queued" => 1,
            "running" => 2,
            "cancelling" => 3,
            "completed" or "cancelled" or "failed" or "unavailable" or "interrupted" => 4,
            _ => 0
        };

        private static string NormalizeCandidateJobValue(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

        private static bool IsCandidateJobActive(CandidateEvaluationJobStatus status) =>
            NormalizeCandidateJobValue(status.Status) is "queued" or "running" or "cancelling";

        private void ReconcileCandidateJobHistory(IEnumerable<CandidateEvaluationJobStatus> history)
        {
            foreach (IGrouping<string, CandidateEvaluationJobStatus> group in history
                .Where(status => IsSafeCandidateKey(status.CandidateKey)
                    && Guid.TryParseExact(status.JobId, "N", out _)
                    && status.StartedUtc.Kind == DateTimeKind.Utc)
                .GroupBy(status => status.CandidateKey, StringComparer.Ordinal))
            {
                CandidateEvaluationJobStatus incoming = group.OrderByDescending(status => status.StartedUtc).First();
                bool replaceExisting = true;
                if (_candidateJobStatuses.TryGetValue(group.Key, out CandidateEvaluationJobStatus? current))
                {
                    bool sameJob = string.Equals(current.JobId, incoming.JobId, StringComparison.Ordinal);
                    bool uncertainStartMatch = (current.ErrorCode is "start_unconfirmed" or "invalid_job_identity")
                        && incoming.StartedUtc >= current.StartedUtc;
                    if (!sameJob && !uncertainStartMatch && incoming.StartedUtc <= current.StartedUtc)
                        replaceExisting = false;
                    if (replaceExisting && !sameJob) _candidateJobStatuses.Remove(group.Key);
                }
                if (!replaceExisting) continue;
                _candidateJobIds[group.Key] = incoming.JobId;
                ApplyCandidateJobStatus(group.Key, incoming);
            }
        }

        private async Task<bool> TryReconcileCandidateJobHistoryForStartAsync(string candidateKey)
        {
            try
            {
                List<CandidateEvaluationJobStatus> history =
                    await ModelDownloadClient.Current.ListCandidateEvaluationJobsAsync(CancellationToken.None);
                ReconcileCandidateJobHistory(history);
                _candidateJobHistoryRefreshFailed = false;
                return _candidateJobIds.ContainsKey(candidateKey);
            }
            catch
            {
                _candidateJobHistoryRefreshFailed = true;
                return false;
            }
        }

        private string FormatCandidateJobStatus(CandidateEvaluationJobStatus? status)
        {
            if (status == null) return "Evaluation: not run in this app session.";
            string state = NormalizeCandidateJobValue(status.Status);
            string phase = NormalizeCandidateJobValue(status.Phase);
            if (state == "unknown" && status.ErrorCode == "invalid_job_identity")
                return "Core did not provide a usable job identity; evaluation start cannot be tracked safely.";
            if (state == "unknown" && status.ErrorCode == "start_unconfirmed")
                return "Core did not confirm whether evaluation started; a job may be active. Retrying remains protected by Core's one-job-at-a-time rule.";
            string result = state switch
            {
                "queued" => "Evaluation queued",
                "running" => "Evaluation running",
                "cancelling" => "Cancellation requested; Core is stopping the evaluation",
                "completed" => "Evaluation completed",
                "cancelled" => "Evaluation cancelled",
                "failed" => "Evaluation failed",
                "interrupted" => "Evaluation interrupted by Core restart; the outcome is unknown and the run was not resumed",
                "unavailable" => status.ErrorCode == "job_status_not_found"
                    ? "Evaluation status unavailable after Core restart or expiry"
                    : "Evaluation unavailable on this runtime or configuration",
                _ => "Evaluation status is unknown"
            };
            if (state == "interrupted") result += " · last recorded phase: " + phase;
            if (state == "running")
                result += phase switch
                {
                    "quality" => " · checking task quality",
                    "performance" => " · measuring local performance",
                    "eligibility" => " · checking evidence eligibility",
                    _ => string.Empty
                };
            if (status.QualityPassCount.HasValue && status.QualityExpectedCount.HasValue)
                result += " · quality checks " + status.QualityPassCount.Value + "/" + status.QualityExpectedCount.Value;
            result += CandidateEvaluationStatusText.FormatMetrics(status);
            result += CandidateEvaluationStatusText.FormatLaunch(status);
            result += CandidateEvaluationStatusText.FormatFailedChecks(status);
            if (status.Eligibility == "EligibleForFurtherReview")
                result += " · eligible for further review only, not a recommendation";
            if (state == "failed" && !string.IsNullOrWhiteSpace(status.ErrorCode))
                result += " · error code " + LimitDisplay(status.ErrorCode, 40);
            if (_candidateStatusRefreshFailures.Contains(status.CandidateKey))
                result += " · last status check failed; this state may be stale";
            return result;
        }

        private string FormatCandidateProvisionalFit(ModelDiscoveryCandidate candidate, CandidateEvaluationJobStatus? jobStatus)
        {
            var explanation = new List<string>
            {
                "Provisional fit · why this Candidate · not a recommendation."
            };

            long? declaredSize = candidate.DeclaredSizeBytes is > 0 ? candidate.DeclaredSizeBytes : null;
            explanation.Add(declaredSize.HasValue
                ? "Publisher-declared file size: " + FormatObservedBytes(declaredSize)
                    + " (estimate only; Core checks authoritative transfer size before download)."
                : "Publisher-declared file size: unknown; no size-based estimate is available.");

            long? quotaHeadroom = _storageBudget?.QuotaHeadroomBytes;
            long? destinationFree = _storageBudget?.DestinationFreeBytes;
            explanation.Add("Storage snapshot (not reserved): PaRuX shared AI-quota headroom: "
                + FormatObservedBytes(quotaHeadroom) + "; managed-model destination-volume free space: "
                + FormatObservedBytes(destinationFree) + ". These observations may change before a transfer.");
            explanation.Add(CompareDeclaredSizeWithResource("Quota estimate", declaredSize, quotaHeadroom));
            explanation.Add(CompareDeclaredSizeWithResource("Destination-space estimate", declaredSize, destinationFree));

            DateTime? metadataObservedUtc = _discoveryCatalog?.LastSuccessUtc is DateTime observedUtc
                && observedUtc.Kind == DateTimeKind.Utc ? observedUtc : null;
            ModelLicenseConsentPolicy.Decision license = ModelLicenseConsentPolicy.Evaluate(
                candidate.DeclaredLicense, metadataRetrieved: metadataObservedUtc.HasValue, metadataObservedUtc);
            string licenseState = !license.MetadataRetrieved
                ? "publisher licence metadata unavailable; terms review is required before download"
                : license.RequiresReview
                    ? "publisher-declared licence requires review under PaRuX's conservative policy before download"
                    : "no additional terms review is required under PaRuX's conservative allow-list";
            if (!string.IsNullOrWhiteSpace(candidate.DeclaredLicense))
                licenseState += " (publisher-declared ID: " + LimitDisplay(candidate.DeclaredLicense, 80) + ")";
            explanation.Add("License gate: " + licenseState
                + "; metadata is not independently verified and this is not a legal assessment.");

            explanation.Add("Hardware/runtime fit: " + ModelHubRowPresentation.HardwareFit(
                ModelFitVerdict.Unknown,
                "not evaluated for this exact Candidate; detected GPU/VRAM alone does not establish compatibility or performance."));
            explanation.Add(FormatCandidateEvidenceScope(jobStatus));
            if (_candidateUtilityPolicyUnavailable)
            {
                explanation.Add("Utility policy: readiness is unavailable; no capability/profile quality or reliability claim is made, and no recommendation is available.");
            }
            else if (_candidateUtilityPolicy is { Configured: true, Readiness: "WellFormed", Policy: not null } policyStatus)
            {
                explanation.Add("Utility policy: an explicit policy is configured for " + LimitDisplay(policyStatus.Policy.Capability, 80)
                    + "; a manual comparison can only report whether its margin is met, requires human review, and is not a recommendation.");
            }
            else
            {
                explanation.Add("Utility policy: no capability/profile quality and reliability floors or comparable incumbent-margin rule are configured; evidence eligibility alone cannot rank or recommend a Candidate.");
            }
            if (jobStatus != null && IsCandidateJobActive(jobStatus))
            {
                explanation.Add("Task evidence: evaluation is in progress; no completed quality/performance result is eligible for review yet.");
            }
            else if (jobStatus != null
                && NormalizeCandidateJobValue(jobStatus.Status) == "completed"
                && jobStatus.Eligibility == "EligibleForFurtherReview")
            {
                explanation.Add("Task evidence: the last retained evaluation reported eligibility for further review at run time; this explanation does not revalidate current bytes or runtime, and is not a recommendation.");
            }
            else if (jobStatus == null)
            {
                explanation.Add("Task evidence: no retained evaluation result is shown in this view; other persisted evidence may exist, and no current quality/performance conclusion is made.");
            }
            else
            {
                explanation.Add("Task evidence: the latest retained job summary shown here does not establish eligibility for further review; earlier or separately persisted evidence may exist, and no current quality/performance conclusion is made.");
            }

            return string.Join(" ", explanation);
        }

        private static string FormatCandidateEvidenceScope(CandidateEvaluationJobStatus? status)
        {
            if (status == null)
                return "Evidence scope: no retained evaluation result is shown. Quick checks cover "
                    + CandidateEvaluationScope.ChatInstructionFollowingCapability
                    + "; a separate General chat qualification profile covers standard × balanced text tasks. Neither an available profile nor this provisional fit establishes a recommendation, coding, tool-use or RAG quality.";

            bool generalChatScope = status.Capability == CandidateEvaluationScope.GeneralChatCapability
                && status.QualitySuiteId == CandidateEvaluationScope.GeneralChatSuiteId
                && status.QualitySuiteVersion == CandidateEvaluationScope.GeneralChatSuiteVersion
                && status.QualityProfileId == CandidateEvaluationScope.GeneralChatQualityProfileId
                && status.QualityProfileVersion == CandidateEvaluationScope.GeneralChatQualityProfileVersion
                && status.PerformanceWorkloadId == CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId
                && status.PerformanceWorkloadVersion == CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion;
            if (generalChatScope)
                return "Evidence scope: " + status.Capability + " suite " + status.QualitySuiteVersion
                    + " for standard × balanced only, plus the separate performance workload "
                    + status.PerformanceWorkloadId + " v" + status.PerformanceWorkloadVersion
                    + "; a single run does not establish qualification or promotion. Coding, other modalities and best-in-class quality are unverified.";

            bool knownScope = status.Capability == CandidateEvaluationScope.ChatInstructionFollowingCapability
                && status.QualitySuiteId == CandidateEvaluationScope.ChatInstructionFollowingSuiteId
                && status.QualitySuiteVersion == CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion
                && status.PerformanceWorkloadId == CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId
                && status.PerformanceWorkloadVersion == CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion;
            if (knownScope)
                return "Evidence scope: " + status.Capability + " suite " + status.QualitySuiteVersion
                    + " plus performance workload " + status.PerformanceWorkloadId + " v" + status.PerformanceWorkloadVersion
                    + "; this narrow result does not establish coding, reasoning, tool-use, RAG or profile-specific quality.";

            return "Evidence scope: this retained job does not identify a recognized capability, quality suite and performance workload; do not infer capability-wide or profile-specific results.";
        }

        private static string FormatObservedBytes(long? bytes) => bytes is >= 0
            ? bytes.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " bytes"
            : "unknown";

        private static string CompareDeclaredSizeWithResource(string label, long? declaredSize, long? availableBytes)
        {
            if (!declaredSize.HasValue)
                return label + ": unknown because the publisher-declared file size is unavailable.";
            if (!availableBytes.HasValue || availableBytes.Value < 0)
                return label + ": unknown because this resource measurement is unavailable.";

            string relation = declaredSize.Value <= availableBytes.Value ? "does not exceed" : "exceeds";
            return label + ": the publisher's size estimate " + relation + " the current observed resource value. This is not transfer admission; Core rechecks authoritative size and budget when download is explicitly requested.";
        }

        private static bool IsCandidateEvaluationFixtureKey(string candidateKey)
        {
#if DEBUG
            bool evaluationFixture = string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_EVALUATION"), "1", StringComparison.Ordinal)
                && string.Equals(candidateKey, new string('a', 64), StringComparison.Ordinal);
            bool stagingFixture = IsCandidateStagingFixtureKey(candidateKey);
            return evaluationFixture || stagingFixture;
#else
            return false;
#endif
        }

        private static bool IsCandidateIgnoreFixtureKey(string candidateKey)
        {
#if DEBUG
            return string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_IGNORE"), "1", StringComparison.Ordinal)
                && string.Equals(candidateKey, new string('a', 64), StringComparison.Ordinal);
#else
            return false;
#endif
        }

        private static bool IsCandidateStagingFixtureKey(string candidateKey)
        {
#if DEBUG
            return string.Equals(Environment.GetEnvironmentVariable("PARUX_UI_TEST_CANDIDATE_STAGING"), "1", StringComparison.Ordinal)
                && string.Equals(candidateKey, new string('d', 64), StringComparison.Ordinal);
#else
            return false;
#endif
        }

#if DEBUG
        private void SimulateSharedMemoryHardwareFixture()
        {
            InvalidatePendingRefresh();
            const long gib = 1024L * 1024 * 1024;
            // Synthetic in-memory display fixture only. It does not change host settings, start a
            // runtime, or make inference/calibration claims.
            _hardware = new RuntimeManagerHardwareStatus
            {
                RamTotalBytes = 32 * gib,
                GpuInventoryAvailable = true,
                GpuInventorySource = "ui-test-fixture",
                GpuAdapters =
                {
                    new GpuAdapterStatus
                    {
                        Index = 0,
                        Name = "AMD Radeon 780M Graphics",
                        VendorId = 0x1002,
                        DedicatedVideoMemoryBytes = 512L * 1024 * 1024,
                        SharedSystemMemoryBytes = 15 * gib
                    }
                },
                AcceleratorProfile = new RuntimeAcceleratorProfile
                {
                    GpuPresent = true,
                    AcceleratorEligible = true,
                    Backend = "text-vulkan",
                    Vendor = "amd",
                    AdapterName = "AMD Radeon 780M Graphics",
                    DedicatedVideoMemoryBytes = 0,
                    SharedSystemMemoryLimitBytes = 15 * gib,
                    SystemRamBytes = 32 * gib,
                    UsableSystemRamBytes = 24 * gib,
                    SharedMemoryPrimary = true,
                    InventorySource = "ui-test-fixture"
                }
            };
            RenderHardwareSummary();
            _status.Text = "UI fixture only: shared-memory GPU profile displayed; no runtime or model action occurred.";
        }

        private void SimulateIneligibleGpuHardwareFixture()
        {
            InvalidatePendingRefresh();
            const long gib = 1024L * 1024 * 1024;
            _hardware = new RuntimeManagerHardwareStatus
            {
                RamTotalBytes = 32 * gib,
                GpuInventoryAvailable = true,
                GpuInventorySource = "ui-test-fixture",
                GpuAdapters =
                {
                    new GpuAdapterStatus
                    {
                        Index = 0,
                        Name = "AMD discrete test adapter",
                        VendorId = 0x1002,
                        DedicatedVideoMemoryBytes = 8 * gib,
                        SharedSystemMemoryBytes = 16 * gib
                    }
                },
                AcceleratorProfile = new RuntimeAcceleratorProfile
                {
                    GpuPresent = true,
                    AcceleratorEligible = false,
                    Backend = "cpu",
                    Vendor = "amd",
                    AdapterName = "AMD discrete test adapter",
                    DedicatedVideoMemoryBytes = 8 * gib,
                    SharedSystemMemoryLimitBytes = 16 * gib,
                    SystemRamBytes = 32 * gib,
                    UsableSystemRamBytes = 24 * gib,
                    InventorySource = "ui-test-fixture"
                }
            };
            RenderHardwareSummary();
            _status.Text = "UI fixture only: physical GPU is visible, but current fit is CPU/RAM-only.";
        }

        private Task LoadCandidateEvaluationUiFixtureAsync()
        {
            InvalidatePendingRefresh();

            string key = new string('a', 64);
            string revision = new string('b', 40);
            string hash = new string('c', 64);
            string repoId = "parux-ui-test/candidate-evaluation-fixture";
            const string fileName = "fixture-model.gguf";
            string source = "https://huggingface.co/" + repoId + "/resolve/" + revision + "/" + fileName;
            bool fixtureIgnored = _candidateIgnoreFixtureIgnored.Contains(key);
            var candidate = new ModelDiscoveryCandidate
            {
                Key = key, RepoId = repoId, Revision = revision, RepoFilePath = fileName,
                PinnedUrl = source, DeclaredLicense = "custom-research-only", DeclaredSizeBytes = 1_234_567,
                Lifecycle = "Candidate",
                Disposition = fixtureIgnored ? "ignored" : "active",
                IgnoredUtc = fixtureIgnored ? DateTime.UtcNow : null,
                NoticePending = !fixtureIgnored,
                IntegrityStatus = "unverified"
            };
            var provenance = new ModelDownloadProvenance
            {
                RepoId = repoId, Revision = revision, RepoFilePath = fileName,
                PinnedSourceUrl = source, ComputedSha256 = hash, ExpectedSha256 = hash,
                IntegrityStatus = "verified", SizeBytes = 1_234_567,
                CompletedUtc = DateTime.UtcNow
            };
            _discoveryCatalog = new ModelDiscoveryCatalog
            {
                Candidates = new List<ModelDiscoveryCandidate> { candidate },
                LastSuccessUtc = DateTime.UtcNow
            };
            _storageBudget = new AiStorageBudgetStatus
            {
                QuotaHeadroomBytes = 2_000_000,
                DestinationFreeBytes = 8_000_000
            };
            _managedModels = new List<ManagedModelInfo>
            {
                new ManagedModelInfo
                {
                    FileName = fileName, SizeBytes = provenance.SizeBytes,
                    RepoId = repoId, Provenance = provenance
                }
            };
            _candidateJobIds.Clear();
            _candidateJobStatuses.Clear();
            _candidateStatusRefreshFailures.Clear();
            _candidateUtilityPolicy = null;
            _candidateUtilityPolicyUnavailable = false;
            _candidateUtilityResultMessages.Clear();
            RenderCandidates();
            RenderCandidateUtilityPolicyStatus();
            _status.Text = fixtureIgnored
                ? "UI fixture reloaded with its persisted ignore disposition; no model action occurred."
                : "UI fixture ready. No model was downloaded or evaluated.";
            return Task.CompletedTask;
        }

        private void LoadCandidateNoticeOverflowUiFixture()
        {
            InvalidatePendingRefresh();
            DateTime now = DateTime.UtcNow;
            var candidates = new List<ModelDiscoveryCandidate>();
            for (int index = 0; index < 61; index++)
            {
                string key = index.ToString("x64", CultureInfo.InvariantCulture);
                string repoId = "parux-ui-test/notice-overflow-" + index.ToString("D3", CultureInfo.InvariantCulture);
                string revision = new string('a', 40);
                string path = "models/fixture-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".gguf";
                candidates.Add(new ModelDiscoveryCandidate
                {
                    Key = key, RepoId = repoId, Revision = revision, RepoFilePath = path,
                    PinnedUrl = "https://huggingface.co/" + repoId + "/resolve/" + revision + "/" + path,
                    Lifecycle = "Candidate", Disposition = "active", IntegrityStatus = "unverified",
                    FirstSeenUtc = now, LastSeenUtc = now, NoticePending = true
                });
            }

            _discoveryCatalog = new ModelDiscoveryCatalog { LastSuccessUtc = now, Candidates = candidates };
            _managedModels.Clear();
            _downloadJobs.Clear();
            _candidateJobIds.Clear();
            _candidateJobStatuses.Clear();
            _candidateNoticeVisibleCount = CandidateNoticePageSize;
            RenderCandidates();
            _status.Text = "UI fixture only: 61 synthetic notices; no network request, download or model execution occurred.";
        }

        private void SimulateCandidateFitUnknownStorageFixture()
        {
            _storageBudget = new AiStorageBudgetStatus
            {
                QuotaHeadroomBytes = null,
                DestinationFreeBytes = 8_000_000
            };
            _status.Text = "UI fixture: quota headroom is unknown while destination free space is known. No model action occurred.";
            RenderCandidates();
        }

        private void SimulateCandidateFitUnknownSizeFixture()
        {
            ModelDiscoveryCandidate? candidate = _discoveryCatalog?.Candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, new string('a', 64), StringComparison.Ordinal));
            if (candidate != null) candidate.DeclaredSizeBytes = null;
            _status.Text = "UI fixture: Candidate file size is unknown. No model action occurred.";
            RenderCandidates();
        }

        private void SimulateCandidateFitEligibleHistoryFixture()
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            DateTime finishedUtc = DateTime.UtcNow;
            const string jobId = "55555555555555555555555555555555";
            _candidateJobIds[candidateKey] = jobId;
            _candidateJobStatuses[candidateKey] = new CandidateEvaluationJobStatus
            {
                JobId = jobId,
                CandidateKey = candidateKey,
                Status = "Completed",
                Phase = "Done",
                Eligibility = "EligibleForFurtherReview",
                Capability = CandidateEvaluationScope.ChatInstructionFollowingCapability,
                QualitySuiteId = CandidateEvaluationScope.ChatInstructionFollowingSuiteId,
                QualitySuiteVersion = CandidateEvaluationScope.ChatInstructionFollowingSuiteVersion,
                PerformanceWorkloadId = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadId,
                PerformanceWorkloadVersion = CandidateEvaluationScope.ChatInstructionFollowingPerformanceWorkloadVersion,
                StartedUtc = finishedUtc.AddMinutes(-2),
                CompletedUtc = finishedUtc
            };
            _status.Text = "UI fixture: historical eligibility is shown only as the last recorded result; no model was run.";
            RenderCandidates();
        }

        private void SimulateCandidateFitAllowlistedLicenseFixture()
        {
            ModelDiscoveryCandidate? candidate = _discoveryCatalog?.Candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, new string('a', 64), StringComparison.Ordinal));
            if (candidate != null) candidate.DeclaredLicense = "MIT";
            _status.Text = "UI fixture: allow-listed publisher-declared licence. No model action occurred.";
            RenderCandidates();
        }

        private Task SimulateStaleCandidateStatusFixtureAsync()
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string oldJobId = "11111111111111111111111111111111";
            string currentJobId = "22222222222222222222222222222222";
            _candidateJobIds[candidateKey] = oldJobId;
            _candidateJobIds[candidateKey] = currentJobId;
            _candidateJobStatuses[candidateKey] = new CandidateEvaluationJobStatus
            {
                JobId = currentJobId, CandidateKey = candidateKey, Status = "Running", Phase = "Quality"
            };
            bool staleResponseApplied = MarkCandidateJobUnavailable(candidateKey, oldJobId);
            bool staleFailureApplied = MarkCandidateStatusRefreshFailed(candidateKey, oldJobId);
            _status.Text = staleResponseApplied
                ? "UI fixture failure: an old 404 replaced the newer Candidate job."
                : staleFailureApplied
                    ? "UI fixture failure: an old polling error marked the newer Candidate job stale."
                    : "UI fixture: stale 404/error ignored; the newer job remains tracked. No model was run.";
            RenderCandidates();
            return Task.CompletedTask;
        }

        private void SimulateUncertainCandidateEvaluationStartFixture(char candidateKeySeed)
        {
            string candidateKey = new string(candidateKeySeed, 64);
            _candidateJobIds.Remove(candidateKey);
            _candidateJobStatuses[candidateKey] = new CandidateEvaluationJobStatus
            {
                CandidateKey = candidateKey, Status = "unknown", Phase = "start_unconfirmed",
                StartedUtc = DateTime.UtcNow,
                ErrorCode = "start_unconfirmed"
            };
            _status.Text = "UI fixture: evaluation start is unconfirmed; a job may still be active. No request or model execution occurred.";
            RenderCandidates();
        }

        private void SimulateInterruptedCandidateEvaluationFixture()
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string jobId = "33333333333333333333333333333333";
            DateTime completed = DateTime.UtcNow;
            _candidateJobIds[candidateKey] = jobId;
            _candidateJobStatuses[candidateKey] = new CandidateEvaluationJobStatus
            {
                JobId = jobId, CandidateKey = candidateKey, Status = "Interrupted", Phase = "Performance",
                StartedUtc = completed.AddMinutes(-2), CompletedUtc = completed,
                QualityStatus = "Completed", QualityPassCount = 4, QualityExpectedCount = 4,
                ErrorCode = "core_restart_interrupted"
            };
            _status.Text = "UI fixture: interrupted evaluation was not resumed; no model was run.";
            RenderCandidates();
        }

        private void SimulateCandidateHistoryReconciliationFixture()
        {
            const string candidateKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            DateTime attemptStarted = DateTime.UtcNow.AddSeconds(-15);
            _candidateJobIds.Remove(candidateKey);
            _candidateJobStatuses[candidateKey] = new CandidateEvaluationJobStatus
            {
                CandidateKey = candidateKey, Status = "unknown", Phase = "start_unconfirmed",
                StartedUtc = attemptStarted, ErrorCode = "start_unconfirmed"
            };
            var durableHistory = new CandidateEvaluationJobStatus
            {
                JobId = "44444444444444444444444444444444", CandidateKey = candidateKey,
                Status = "Interrupted", Phase = "Quality", StartedUtc = attemptStarted.AddSeconds(1),
                CompletedUtc = DateTime.UtcNow, ErrorCode = "core_restart_interrupted"
            };
            ReconcileCandidateJobHistory(new[] { durableHistory });
            bool adoptedDurableJob = _candidateJobIds.TryGetValue(candidateKey, out string? jobId)
                && string.Equals(jobId, durableHistory.JobId, StringComparison.Ordinal);
            _status.Text = adoptedDurableJob
                ? "UI fixture: uncertain start reconciled with durable Core history; no model was run."
                : "UI fixture failure: durable Candidate history was not adopted.";
            RenderCandidates();
        }

        private void LoadCandidateStagingUiFixture(bool tamperPinnedUrl = false)
        {
            InvalidatePendingRefresh();
            string key = new string('d', 64);
            string revision = new string('d', 40);
            const string repoId = "parux-ui-test/candidate-staging-fixture";
            const string repoFilePath = "models/fixture-model.gguf";
            string pinnedUrl = "https://huggingface.co/" + repoId + "/resolve/" + revision
                + (tamperPinnedUrl ? "/other/fixture-model.gguf" : "/models/fixture-model.gguf");
            DateTime now = DateTime.UtcNow;
            var candidate = new ModelDiscoveryCandidate
            {
                Key = key, Provider = "huggingface", ProviderAdapterVersion = 1,
                RepoId = repoId, Revision = revision, RepoFilePath = repoFilePath, PinnedUrl = pinnedUrl,
                DeclaredLicense = "custom-research-only", DeclaredSizeBytes = 1_234_567,
                Lifecycle = "Candidate", Disposition = "active", IntegrityStatus = "unverified",
                FirstSeenUtc = now, LastSeenUtc = now, NoticePending = true
            };
            _discoveryCatalog = new ModelDiscoveryCatalog
            {
                LastSuccessUtc = now,
                Candidates = new List<ModelDiscoveryCandidate> { candidate }
            };
            _managedModels.Clear();
            _downloadJobs.Clear();
            _candidateJobIds.Clear();
            _candidateJobStatuses.Clear();
            _candidateStatusRefreshFailures.Clear();
            _candidateDownloadInFlight.Clear();
            _candidateDownloadUnknown.Clear();
            RenderCandidates();
            _status.Text = "UI fixture ready. Candidate metadata is synthetic; no network request or model execution occurred.";
        }

        private void StageCandidateFixtureInMemory(
            ModelDiscoveryCandidate candidate, string managedFileName, ModelDownloadLicenseEvidence evidence)
        {
            if (!IsCandidateStagingFixtureKey(candidate.Key) || !evidence.ReviewRequired || !evidence.ReviewAcknowledged)
                throw new InvalidOperationException("The Candidate staging fixture requires explicit licence review.");

            long sizeBytes = candidate.DeclaredSizeBytes ?? throw new InvalidDataException("Fixture Candidate must declare a test size.");
            var provenance = new ModelDownloadProvenance
            {
                RepoId = candidate.RepoId,
                Revision = candidate.Revision,
                RepoFilePath = candidate.RepoFilePath,
                PinnedSourceUrl = candidate.PinnedUrl,
                LicenseMetadataStatus = evidence.MetadataStatus,
                PublisherDeclaredLicense = evidence.PublisherDeclaredLicense,
                MetadataObservedUtc = evidence.MetadataObservedUtc,
                ReviewRequired = evidence.ReviewRequired,
                ReviewAcknowledged = evidence.ReviewAcknowledged,
                ReviewAcknowledgedUtc = evidence.ReviewAcknowledgedUtc,
                ExpectedSha256 = candidate.DeclaredSha256,
                ComputedSha256 = new string('c', 64),
                IntegrityStatus = "unavailable",
                SizeBytes = sizeBytes,
                FileLastWriteUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow
            };
            _managedModels.Add(new ManagedModelInfo
            {
                FileName = managedFileName,
                SizeBytes = sizeBytes,
                RepoId = candidate.RepoId,
                Provenance = provenance
            });
        }
#endif

        private void RenderHardwareSummary()
        {
            if (_hardware == null)
            {
                _hardwareSummary.Text = "Hardware fit: unavailable. Variant size guidance remains visible, but PaRuX will not fabricate a VRAM recommendation.";
                return;
            }

            List<GpuAdapterStatus> adapters = (_hardware.GpuAdapters ?? new List<GpuAdapterStatus>())
                .Where(adapter => !adapter.IsSoftware && adapter.DedicatedVideoMemoryBytes > 0)
                .OrderByDescending(adapter => adapter.DedicatedVideoMemoryBytes)
                .ToList();
            RuntimeAcceleratorProfile? accelerator = _hardware.AcceleratorProfile;
            string gpu = accelerator is { AcceleratorEligible: true, SharedMemoryPrimary: true }
                ? (accelerator.AdapterName ?? "Integrated GPU") + " · " + FormatBytes(accelerator.SharedSystemMemoryLimitBytes)
                    + " shared system-memory limit (" + accelerator.Backend + "; not extra VRAM)"
                : accelerator is { AcceleratorEligible: true }
                    ? (accelerator.AdapterName ?? "GPU") + " · " + FormatBytes(accelerator.DedicatedVideoMemoryBytes)
                        + " dedicated (" + accelerator.Backend + ")"
                    : accelerator is { GpuPresent: true, AcceleratorEligible: false }
                        ? (accelerator.AdapterName ?? "Physical GPU") + " detected, but no compatible installed PaRuX text backend is eligible; current model fit uses CPU/RAM only"
                        : adapters.Count == 0
                        ? (_hardware.GpuDetected ? "CUDA GPU · " + _hardware.VramTotalMib + " MiB" : "no dedicated GPU capacity reported")
                        : string.Join(" · ", adapters.Select(adapter => adapter.Name + " " + FormatBytes(adapter.DedicatedVideoMemoryBytes)));
            _hardwareSummary.Text = "Hardware fit source: " + (_hardware.GpuInventoryAvailable ? _hardware.GpuInventorySource : "runtime fallback")
                + " · " + gpu + " · system RAM " + FormatBytes(_hardware.RamTotalBytes)
                + ". Independent GPU VRAM pools are never summed as an automatic single-model fit.";
        }

        private async Task SearchAsync(int? expectedRefreshGeneration = null)
        {
            if (expectedRefreshGeneration.HasValue && expectedRefreshGeneration.Value != _refreshGeneration) return;
            if (_candidateIgnoreInFlight.Count > 0) return;
            // A refresh owns the shared status/busy line. Let it finish before accepting a
            // manually-triggered search; OnShownSearching invokes us only after its refresh.
            if (_refreshBusy && !expectedRefreshGeneration.HasValue) return;

            ++_searchGeneration;
            int generation = _searchGeneration;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            CancellationToken ct = _searchCts.Token;
            string query = (_search.Text ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                _lastSearch.Clear();
                RenderSearch(Array.Empty<ModelSearchResult>());
                if (!_refreshBusy) SetBusy(false, "Enter a repository ID or search term to look for GGUF models.");
                return;
            }
            SetBusy(true, "Searching Hugging Face…");
            try
            {
                List<ModelSearchResult> results = await ModelDownloadClient.Current.SearchAsync(query, 30, ct);
                if (!IsCurrentSearch(generation, ct, expectedRefreshGeneration)) return;
                _lastSearch.Clear();
                _lastSearch.AddRange(results);
                List<ModelSearchResult> filtered = FilterSearch();
                RenderSearch(filtered);
                _status.Text = results.Count == 0
                    ? "No GGUF repositories matched."
                    : filtered.Count + " shown of " + results.Count + " repository result(s).";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsCurrentSearch(generation, ct, expectedRefreshGeneration))
                    _status.Text = "Search failed: " + ex.Message;
            }
            finally
            {
                if (IsCurrentSearch(generation, ct, expectedRefreshGeneration)) SetBusy(false, _status.Text);
            }
        }

        private bool IsCurrentSearch(int generation, CancellationToken ct, int? expectedRefreshGeneration) =>
            generation == _searchGeneration
                && !ct.IsCancellationRequested
                && (!expectedRefreshGeneration.HasValue || expectedRefreshGeneration.Value == _refreshGeneration);

        private List<ModelSearchResult> FilterSearch()
        {
            string selected = _category.SelectedItem as string ?? "All categories";
            IEnumerable<ModelSearchResult> query = _lastSearch;
            query = query.Where(r => ModelCategoryFilter.Matches(selected, r.Category));
            return query.OrderByDescending(r => r.Likes).ThenByDescending(r => r.Downloads).ToList();
        }

        private void RenderRecommended(IReadOnlyList<RecommendedModel> models)
        {
            _recommended.Children.Clear();
            foreach (RecommendedModel model in models)
            {
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(model.EstimatedSizeBytes, _hardware);
                var card = new StackPanel { Spacing = 4 };
                card.Children.Add(new TextBlock { Text = model.Title + " · " + model.Category, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                card.Children.Add(new TextBlock { Text = model.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.76 });
                card.Children.Add(new TextBlock
                {
                    Text = model.ApproxParams + " · suggested " + model.QuantHint + " · ~" + FormatBytes(model.EstimatedSizeBytes)
                        + " · " + ModelHubRowPresentation.HardwareFit(fit.Verdict, fit.Summary),
                    FontSize = 12,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                });
                var button = new Button { Content = "Review suggested quantization & download" };
                button.Click += async (_, _) => await ChooseRecommendedFileAsync(model);
                card.Children.Add(button);
                _recommended.Children.Add(card);
            }
            if (models.Count == 0) _recommended.Children.Add(new TextBlock { Text = "No curated recommendations are available." });
        }

        private void RenderSearch(IReadOnlyList<ModelSearchResult> results)
        {
            _results.Children.Clear();
            foreach (ModelSearchResult repo in results)
            {
                IReadOnlyList<ModelHubSplitSetPresentation.QuantChoiceOption> choices =
                    ModelHubSplitSetPresentation.BuildQuantChoiceOptions(repo.QuantChoices);
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = repo.RepoId, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = repo.Category + " · " + choices.Count + " quant choice(s) · ♥ " + repo.Likes + " · ↓ " + repo.Downloads, FontSize = 12, Opacity = 0.72 });
                var picker = new ComboBox { MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (ModelHubSplitSetPresentation.QuantChoiceOption option in choices)
                {
                    picker.Items.Add(new ComboBoxItem
                    {
                        Content = option.Label + " · ~" + FormatBytes(option.Choice.SizeBytes),
                        Tag = option.Choice,
                        IsEnabled = option.CanDownload
                    });
                }
                int firstDownloadable = ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(choices, null);
                bool fromFirstModelSetup = string.Equals(
                    repo.RepoId, _firstModelHandoffRepoId, StringComparison.OrdinalIgnoreCase);
                int preferred = fromFirstModelSetup
                    ? ModelHubSplitSetPresentation.PreferredQuantChoiceIndex(choices, _firstModelHandoffQuantHint)
                    : firstDownloadable;
                if (preferred >= 0) picker.SelectedIndex = preferred;
                if (fromFirstModelSetup && !string.IsNullOrWhiteSpace(_firstModelHandoffQuantHint))
                    row.Children.Add(new TextBlock
                    {
                        Text = "Setup suggestion: " + _firstModelHandoffQuantHint
                            + ". A complete matching quantization is preselected when available. Review its size and license before downloading.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.82
                    });
                row.Children.Add(picker);

                var choose = new Button { Content = "Review selected quant and download", IsEnabled = firstDownloadable >= 0 };
                picker.SelectionChanged += (_, _) =>
                {
                    choose.IsEnabled = picker.SelectedItem is ComboBoxItem { Tag: ModelQuantChoice selected } && selected.Complete;
                };
                choose.Click += async (_, _) =>
                {
                    if (picker.SelectedItem is not ComboBoxItem { Tag: ModelQuantChoice selected } || !selected.Complete) return;
                    try { await ChooseQuantChoiceAsync(repo.RepoId, selected); }
                    catch (Exception ex) { _status.Text = "Could not compare variants: " + ex.Message; }
                    finally { SetBusy(false, _status.Text); }
                };
                row.Children.Add(choose);
                if (choices.Count == 0)
                    row.Children.Add(new TextBlock { Text = "No grouped GGUF quantisation choices were returned.", FontSize = 12, Opacity = 0.72 });
                _results.Children.Add(row);
            }
            if (results.Count == 0)
                _results.Children.Add(new TextBlock { Text = _lastSearch.Count == 0 ? "Search to see live Hugging Face GGUF results." : "No result matches the selected category." });
        }

        private async Task ChooseQuantChoiceAsync(string repoId, ModelQuantChoice choice)
        {
            SetBusy(true, "Preparing " + choice.Quantization + "…");
            ModelRepoDetails? details = null;
            bool detailsRetrieved = false;
            try
            {
                details = await ModelDownloadClient.Current.GetDetailsAsync(repoId, CancellationToken.None);
                detailsRetrieved = true;
            }
            catch (Exception ex)
            {
                Log.Write("Model license metadata lookup failed for " + repoId + ": " + ex.Message);
            }

            ModelDownloadPlan plan = await ModelDownloadClient.Current.GetDownloadPlanAsync(
                repoId, string.IsNullOrWhiteSpace(choice.Quantization) ? null : choice.Quantization,
                choice.SetName, choice.Directory, choice.PartCount, CancellationToken.None);
            if (!plan.Complete)
                throw new InvalidOperationException("The selected set is incomplete at the source and cannot be downloaded.");
            if (!ModelRevisionIdentity.IsValid(plan.Revision))
                throw new InvalidOperationException("The repository did not provide a pinned revision for this set.");
            if (ModelRevisionIdentity.IsValid(details?.Revision)
                && !string.Equals(ModelRevisionIdentity.Normalize(details!.Revision), plan.Revision, StringComparison.Ordinal))
                throw new InvalidOperationException("The repository changed while the download plan was being prepared. Refresh the search and try again.");

            if (_hardware == null) _hardware = await TryGetHardwareAsync(CancellationToken.None);
            ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(plan.TotalBytes, _hardware);
            var planContent = new StackPanel { Spacing = 8 };
            planContent.Children.Add(new TextBlock
            {
                Text = plan.SetName + " · " + plan.Quantization + " · " + plan.PartCount
                    + (plan.PartCount == 1 ? " part" : " parts") + " · " + FormatBytes(plan.TotalBytes),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            planContent.Children.Add(new TextBlock
            {
                Text = "Hardware fit: " + ModelFitVerdictWords.Of(fit.Verdict) + " · " + fit.Summary,
                TextWrapping = TextWrapping.Wrap
            });
            planContent.Children.Add(new TextBlock
            {
                Text = string.Join("\n", plan.Parts.Select(part => part.FileName + " · " + FormatBytes(part.SizeBytes))),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.78
            });
            if (plan.Projector != null)
                planContent.Children.Add(new TextBlock
                {
                    Text = "Vision projector · " + plan.Projector.FileName + " · " + FormatBytes(plan.Projector.SizeBytes),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12
                });
            if (!string.IsNullOrWhiteSpace(plan.TextOnlyNote))
                planContent.Children.Add(new TextBlock { Text = plan.TextOnlyNote, TextWrapping = TextWrapping.Wrap });

            var planDialog = new ContentDialog
            {
                Title = "Review GGUF download plan",
                Content = planContent,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await planDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                SetBusy(false, "Download cancelled.");
                return;
            }

            ModelLicenseConsentPolicy.Decision licenseDecision =
                ModelLicenseConsentPolicy.Evaluate(details?.License, detailsRetrieved);
            bool queued = await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                XamlRoot,
                repoId,
                plan.Parts.OrderBy(part => part.Index).First().FileName,
                licenseDecision,
                plan.Revision,
                async (_, licenseEvidence) =>
                {
                    SetBusy(true, "Starting " + plan.SetName + "…");
                    await ModelDownloadClient.Current.StartDownloadPlanAsync(new ModelDownloadPlanRequest
                    {
                        RepoId = repoId,
                        Quantization = choice.Quantization,
                        SetName = choice.SetName,
                        Directory = choice.Directory,
                        PartCount = choice.PartCount,
                        Revision = plan.Revision,
                        LicenseEvidence = licenseEvidence
                    }, CancellationToken.None);
                });
            if (!queued)
            {
                SetBusy(false, "Download cancelled.");
                return;
            }

            _status.Text = "Queued the complete " + plan.SetName + " set.";
            await RefreshAllAsync();
        }

        private void RenderMedia(
            IReadOnlyList<MediaCheckpointStatus> current,
            IReadOnlyList<RecommendedCheckpoint> recommendations,
            IReadOnlyList<ModelDownloadStatus> downloads)
        {
            if (RenderMediaCatalogue(current)) return;
            _media.Children.Clear();
            _media.Children.Add(new TextBlock
            {
                Text = "PaRuX only recommends checkpoint variants verified compatible with the workflow family already wired for that role. An arbitrary diffusion model is not presented as a safe drop-in replacement.",
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

            foreach (RecommendedCheckpoint checkpoint in recommendations)
            {
                bool active = current.Any(s => string.Equals(s.Type, checkpoint.Type, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.CurrentFile, checkpoint.FileName, StringComparison.OrdinalIgnoreCase));
                ModelDownloadStatus? latest = downloads
                    .Where(d => string.Equals(d.FileName, checkpoint.FileName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.CreatedUtc)
                    .FirstOrDefault();

                var card = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                card.Children.Add(new TextBlock
                {
                    Text = checkpoint.Title + " · " + checkpoint.Type + " · ~" + FormatBytes(checkpoint.EstimatedSizeBytes),
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                card.Children.Add(new TextBlock { Text = checkpoint.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.76 });
                if (latest != null)
                    card.Children.Add(new TextBlock { Text = "Latest transfer: " + latest.Status + (latest.VerificationStatus == null ? string.Empty : " · SHA-256 " + latest.VerificationStatus), FontSize = 12, Opacity = 0.72 });

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var download = new Button { Content = "1. Download checkpoint", IsEnabled = !active };
                download.Click += async (_, _) => await DownloadMediaCheckpointAsync(checkpoint, download);
                buttons.Children.Add(download);

                var activate = new Button { Content = active ? "Active" : "2. Activate downloaded", IsEnabled = !active };
                activate.Click += async (_, _) => await ActivateMediaCheckpointAsync(checkpoint);
                buttons.Children.Add(activate);
                card.Children.Add(buttons);
                _media.Children.Add(card);
            }

            if (recommendations.Count == 0)
                _media.Children.Add(new TextBlock { Text = "No additional verified same-family media checkpoint is currently recommended. The active workflow defaults remain available above." });
        }

        private async Task DownloadMediaCheckpointAsync(RecommendedCheckpoint checkpoint, Button trigger)
        {
            // CreateDownloadAsync on the server awaits a real HEAD request to Hugging Face (to
            // learn the SHA-256/size) before it returns - confirmed live, this alone can take
            // several seconds. Without disabling immediately, the button stayed clickable that
            // whole time with no visible change, and an impatient re-click queued a second,
            // fully duplicate download of the same file. Disable and show status synchronously,
            // before the await, not after it.
            trigger.IsEnabled = false;
            SetBusy(true, "Checking repository terms for " + checkpoint.FileName + "…");
            try
            {
                async Task StartPinnedDownload(string revision, ModelDownloadLicenseEvidence licenseEvidence)
                {
                    var download = new ModelDownloadRequest
                    {
                        RepoId = checkpoint.HuggingFaceRepoId,
                        Revision = revision,
                        FileName = checkpoint.FileName,
                        DownloadUrl = checkpoint.DownloadUrl,
                        SizeBytes = checkpoint.EstimatedSizeBytes,
                        ExpectedSha256 = string.IsNullOrWhiteSpace(checkpoint.Sha256) ? null : checkpoint.Sha256.Trim(),
                        MediaCheckpointType = checkpoint.Type,
                        LicenseEvidence = licenseEvidence
                    };
                    ModelDownloadClient.PinRequestToRevision(download, revision);
                    await ModelDownloadClient.Current.StartDownloadAsync(download, CancellationToken.None);
                }

                bool queued;
                if (ModelRevisionIdentity.IsValid(checkpoint.Revision))
                {
                    // The catalogue's licence hint is not live metadata fetched at this commit.
                    // Treat it as unavailable so the exact-revision UI always requires review.
                    var decision = ModelLicenseConsentPolicy.Evaluate(checkpoint.License, metadataRetrieved: false);
                    queued = await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                        XamlRoot, checkpoint.HuggingFaceRepoId, checkpoint.FileName,
                        decision, checkpoint.Revision, StartPinnedDownload);
                }
                else
                {
                    queued = await ModelLicenseConsentPrompt.ConfirmAndStartRepositoryDownloadAsync(
                        XamlRoot, checkpoint.HuggingFaceRepoId, checkpoint.FileName, StartPinnedDownload);
                }
                if (!queued)
                {
                    trigger.IsEnabled = true;
                    SetBusy(false, "Download cancelled.");
                    return;
                }
                _status.Text = "Queued " + checkpoint.FileName + ". Wait for it to finish downloading (see Downloads below), then use step 2, Activate downloaded.";
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "Media checkpoint download failed: " + ex.Message;
                trigger.IsEnabled = true;
                SetBusy(false, _status.Text);
            }
        }

        private async Task ActivateMediaCheckpointAsync(RecommendedCheckpoint checkpoint)
        {
            try
            {
                SetBusy(true, "Activating " + checkpoint.Title + "…");
                MediaCheckpointStatus status = await ModelDownloadClient.Current.ActivateMediaCheckpointAsync(checkpoint, CancellationToken.None);
                _status.Text = checkpoint.Title + " is now active for " + status.Type + ".";
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                _status.Text = "Activation failed. Download the checkpoint completely first. " + ex.Message;
                SetBusy(false, _status.Text);
            }
        }

        private async Task ChooseRecommendedFileAsync(RecommendedModel recommendation)
        {
            SetBusy(true, "Resolving a complete GGUF set for " + recommendation.Title + "…");
            try
            {
                List<ModelSearchResult> matches = await ModelDownloadClient.Current.SearchAsync(
                    recommendation.HuggingFaceRepoId, 10, CancellationToken.None);
                ModelHubSplitSetPresentation.RecommendedSetOption selected =
                    ModelHubSplitSetPresentation.PickRecommendedSet(
                        matches, recommendation.HuggingFaceRepoId, recommendation.QuantHint)
                    ?? throw new InvalidOperationException(
                        "The exact recommended repository has no complete GGUF set available. "
                        + "Search Model Hub to inspect other models or refresh the recommendations.");

                // Use the same revision-pinned multipart download planner and license review
                // as a manually selected GGUF set, including projector and storage checks.
                SetBusy(false, "Review the recommended quantization and complete download plan.");
                await ChooseQuantChoiceAsync(selected.RepoId, selected.Choice);
            }
            catch (Exception ex) { _status.Text = "Could not resolve recommendation: " + ex.Message; }
            finally { SetBusy(false, _status.Text); }
        }

        private async Task ChooseFileAsync(string repoId, IReadOnlyList<ModelFileOption> files, string? quantHint)
        {
            List<ModelFileOption> choices = files
                .Where(f => !f.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => FilePreference(f.FileName, quantHint))
                .ThenBy(f => f.SizeBytes)
                .ToList();
            if (choices.Count == 0) throw new InvalidOperationException("No base-model GGUF files are available in this repository.");

            if (_hardware == null) _hardware = await TryGetHardwareAsync(CancellationToken.None);

            ModelRepoDetails? details = null;
            bool detailsRetrieved = false;
            try
            {
                details = await ModelDownloadClient.Current.GetDetailsAsync(repoId, CancellationToken.None);
                detailsRetrieved = true;
            }
            catch (Exception ex) { Log.Write("Model license metadata lookup failed for " + repoId + ": " + ex.Message); }
            ModelLicenseConsentPolicy.Decision licenseDecision = ModelLicenseConsentPolicy.Evaluate(details?.License, detailsRetrieved);

            var detailByName = (details?.Files ?? new List<ModelFileDetails>())
                .ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Width = 960,
                Height = 350,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            foreach (ModelFileOption file in choices)
            {
                detailByName.TryGetValue(file.FileName, out ModelFileDetails? meta);
                string quant = ModelHubRowPresentation.Quantization(file.FileName, meta?.Quantization);
                // SearchAsync's Hugging Face endpoint never returns a sibling size (confirmed live:
                // the list/search API omits size/lfs entirely, even with full=true), so file.SizeBytes
                // is always 0 here. GetDetailsAsync's single-repo lookup (now requesting blobs=true)
                // does return real sizes - prefer that when it has one, falling back to the search
                // result only if the detail lookup failed or didn't cover this file.
                // Corrected in place (not just read into a local) so the request this dialog's
                // "Download selected" eventually sends - and the download job's TotalBytes, which
                // RenderDownloads uses to show transfer progress at all - carry the real size too,
                // not just this row's display.
                if (meta?.SizeBytes is > 0 && file.SizeBytes <= 0) file.SizeBytes = meta.SizeBytes.Value;
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(file.SizeBytes, _hardware);
                var listItem = new ListViewItem
                {
                    Tag = file,
                    Content = VariantRow(
                        file.FileName,
                        quant,
                        file.SizeBytes,
                        HardwareFitLabel(fit),
                        VariantGuidance(file.FileName, quant, quantHint))
                };
                list.Items.Add(listItem);
            }
            list.SelectedIndex = 0;

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = repoId, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock
            {
                Text = licenseDecision.MetadataStatus
                    + " · revision: " + (details?.Revision ?? "unknown")
                    + (string.IsNullOrWhiteSpace(details?.PipelineTag) ? string.Empty : " · pipeline: " + details.PipelineTag),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.82
            });
            Uri? modelCardUri = ModelLicenseConsentPolicy.GetModelCardUri(repoId, details?.Revision);
            if (modelCardUri != null)
            {
                content.Children.Add(new HyperlinkButton
                {
                    Content = "Review model card and repository terms",
                    NavigateUri = modelCardUri,
                    HorizontalAlignment = HorizontalAlignment.Left
                });
            }
            if (!string.IsNullOrWhiteSpace(quantHint))
                content.Children.Add(new TextBlock { Text = "Curated recommendation: " + quantHint + ". Variants are ordered with the closest match first.", TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
            content.Children.Add(VariantHeader());
            content.Children.Add(list);
            content.Children.Add(new TextBlock
            {
                Text = "Hardware fit is a conservative pre-download capacity estimate: GGUF weight size + runtime/KV/buffer headroom against the largest physical GPU and reserved system RAM. It intentionally does not invent an exact gpu-layer count. If aggregate VRAM across multiple adapters would fit but no single adapter does, PaRuX reports an explicit multi-GPU split requirement instead of treating VRAM as one pool.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });

            content.Children.Add(new TextBlock
            {
                Text = "Real throughput/context limits still depend on model architecture, selected runtime/backend and benchmark evidence. The transfer may be several GB; PaRuX verifies SHA-256 when Hugging Face exposes a digest and preserves partial bytes for Pause/Resume.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });

            var dialog = new ContentDialog
            {
                Title = "Compare GGUF variants",
                Content = content,
                PrimaryButtonText = licenseDecision.RequiresReview ? "Review terms & download" : "Download selected",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                MaxWidth = 1040
            };
            // ContentDialog's own themed template clamps its content to a default MaxWidth (~548
            // effective px) regardless of what width the content inside asks for - confirmed live
            // via UI Automation: the variant list's row Grid measured a genuine 996 PHYSICAL px
            // (matching that ~548 epx default at this machine's 200% scale), not the 900-1060 the
            // ListView itself requested. That left barely enough of the row's actual width for
            // FILE/QUANT/SIZE, and the HARDWARE FIT/GUIDANCE columns (2.6 and 1.8 of 9.7 stars)
            // collapsed to a genuinely zero-size layout rect despite holding correct text. The
            // dialog's own ContentDialogMaxWidth theme resource, not just its MaxWidth property,
            // has to be overridden - the default template binds to that resource ahead of the
            // instance property.
            dialog.Resources["ContentDialogMaxWidth"] = 1040d;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (list.SelectedItem is not ListViewItem item || item.Tag is not ModelFileOption selected) return;

            bool queued = await ModelLicenseConsentPrompt.ConfirmAndStartAtRevisionAsync(
                XamlRoot,
                repoId,
                selected.FileName,
                licenseDecision,
                details?.Revision,
                async (revision, licenseEvidence) =>
                {
                    // The chooser dialog is already gone, and CreateDownloadAsync may await a real
                    // HEAD request; show progress only after any required licence review is accepted.
                    SetBusy(true, "Starting " + selected.FileName + "…");
                    var download = new ModelDownloadRequest
                    {
                        RepoId = repoId,
                        FileName = selected.FileName,
                        DownloadUrl = selected.DownloadUrl,
                        SizeBytes = selected.SizeBytes,
                        LicenseEvidence = licenseEvidence
                    };
                    ModelDownloadClient.PinRequestToRevision(download, revision);
                    await ModelDownloadClient.Current.StartDownloadAsync(download, CancellationToken.None);
                });
            if (!queued)
            {
                SetBusy(false, "Download cancelled.");
                return;
            }
            _status.Text = "Queued " + selected.FileName + ".";
            await RefreshAllAsync();
        }

        private static Grid VariantHeader() => VariantRow("FILE", "QUANT", 0, "HARDWARE FIT", "GUIDANCE", true);

        private static Grid VariantRow(
            string fileName,
            string quant,
            long sizeBytes,
            string hardwareFit,
            string guidance,
            bool header = false)
        {
            // HorizontalAlignment must be explicit Stretch, not left at its Grid default: a
            // ListViewItem's Content is measured for its own desired size first, and a Grid with
            // only Star columns reports back a desired width using each column's own content need
            // (not a true proportional split) unless it is told to actually stretch and fill
            // whatever width the ListViewItem arranges it at. Confirmed live via UI Automation
            // without this: FILE/QUANT/SIZE (columns 0-2) measured at their content width and ate
            // nearly the whole ~996px row, leaving HARDWARE FIT and GUIDANCE (columns 3-4) a
            // genuinely zero-size layout rect despite holding perfectly correct text.
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3.3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });

            AddCell(grid, 0, fileName, header);
            AddCell(grid, 1, quant, header);
            AddCell(grid, 2, header ? "SIZE" : FormatBytes(sizeBytes), header);
            AddCell(grid, 3, hardwareFit, header);
            AddCell(grid, 4, guidance, header);
            return grid;
        }

        private static void AddCell(Grid grid, int column, string text, bool header)
        {
            // TextWrapping.Wrap here used to leave the last two columns (HARDWARE FIT, GUIDANCE)
            // invisible with a genuinely zero-size layout rect - confirmed live via UI Automation:
            // their TextBlocks held perfectly correct text ("Excellent full-GPU fit...",
            // "Recommended match") but Grid's star-sizing pass let the earlier Wrap-enabled columns
            // measure at their full unwrapped single-line width instead of respecting their star
            // share, so FILE/QUANT/SIZE alone consumed ~900 of the row's ~996px and starved the
            // remaining two columns to nothing. Single-line ellipsis truncation (with the full text
            // still available as a tooltip) sidesteps that Grid/Wrap interaction entirely instead of
            // trying to out-guess it.
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                FontSize = header ? 11 : 12,
                FontWeight = header ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = header ? 0.7 : 1
            };
            if (!header) ToolTipService.SetToolTip(block, text);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        private static string HardwareFitLabel(ModelHardwareFitEstimate fit)
        {
            string fitLabel = ModelHubRowPresentation.HardwareFit(fit.Verdict, fit.Summary);
            if (fit.Tier == ModelHardwareFitTier.Unknown) return fitLabel;
            string adapter = string.IsNullOrWhiteSpace(fit.PreferredGpuName) ? string.Empty : " · " + fit.PreferredGpuName;
            string footprint = fit.EstimatedRuntimeBytes > 0 ? " · est. " + FormatBytes(fit.EstimatedRuntimeBytes) : string.Empty;
            string capacity = fit.ApproximateGpuCapacityPercent > 0 && fit.Tier is ModelHardwareFitTier.PartialOffload or ModelHardwareFitTier.MultiGpuSplitPossible
                ? " · ~" + fit.ApproximateGpuCapacityPercent + (fit.UsesSharedSystemMemory
                    ? "% shared-memory aperture capacity"
                    : "% single-GPU capacity")
                : string.Empty;
            return fitLabel + adapter + footprint + capacity;
        }

        private static string VariantGuidance(string name, string quant, string? hint)
        {
            if (FilePreference(name, hint) >= 80) return "Recommended match";
            string q = (quant ?? string.Empty).ToUpperInvariant();
            if (q.Contains("Q8") || q.Contains("Q6")) return "Higher precision · larger";
            if (q.Contains("Q5")) return "Quality-biased balance";
            if (q.Contains("Q4") || q.Contains("IQ4")) return "Balanced local default";
            if (q.Contains("Q3") || q.Contains("IQ3")) return "Lower memory";
            if (q.Contains("Q2") || q.Contains("IQ2")) return "Minimum memory · more loss";
            if (q.Contains("F16") || q.Contains("BF16")) return "Full precision · very large";
            return "Check model card / benchmark";
        }

        private void RenderDownloads(IReadOnlyList<ModelDownloadStatus> jobs)
        {
            _downloads.Children.Clear();
            foreach (ModelDownloadStatus job in jobs.OrderByDescending(j => j.CreatedUtc).Take(30))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string progress = job.TotalBytes > 0 ? " · " + FormatBytes(job.BytesDownloaded) + " / " + FormatBytes(job.TotalBytes) : string.Empty;
                string state = ModelDownloadPresentationPolicy.DescribeStatus(job);
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock
                {
                    Text = job.FileName + " · " + state + progress
                        + (string.IsNullOrWhiteSpace(job.VerificationStatus) ? string.Empty : " · SHA-256 " + job.VerificationStatus),
                    TextWrapping = TextWrapping.Wrap
                });
                string details = ModelDownloadPresentationPolicy.FormatDetails(job);
                if (details.Length > 0)
                    text.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.78 });
                Grid.SetColumn(text, 0); row.Children.Add(text);
                if (PaRuX.Services.ModelDownloadStatusPolicy.CanPause(job.Status))
                {
                    var pause = new Button { Content = "Pause" };
                    AutomationProperties.SetAutomationId(pause,
                        ModelDownloadPresentationPolicy.GetActionAutomationId("ModelHub", job.Id, "Pause"));
                    pause.Click += async (_, _) =>
                    {
                        try
                        {
                            await ModelDownloadClient.Current.PauseDownloadAsync(job.Id, CancellationToken.None);
                            _status.Text = "Paused " + job.FileName + "; downloaded bytes are kept for resume.";
                            await RefreshAllAsync();
                        }
                        catch (Exception ex) { _status.Text = "Pause failed: " + ex.Message; }
                    };
                    Grid.SetColumn(pause, 1); row.Children.Add(pause);
                }
                else if (PaRuX.Services.ModelDownloadStatusPolicy.CanResume(job.Status))
                {
                    var resume = new Button { Content = "Resume" };
                    AutomationProperties.SetAutomationId(resume,
                        ModelDownloadPresentationPolicy.GetActionAutomationId("ModelHub", job.Id, "Resume"));
                    resume.Click += async (_, _) =>
                    {
                        try
                        {
                            await ModelDownloadClient.Current.StartDownloadAsync(new ModelDownloadRequest { ResumeFromDownloadId = job.Id }, CancellationToken.None);
                            _status.Text = "Re-queued " + job.FileName + " from its preserved partial download.";
                            await RefreshAllAsync();
                        }
                        catch (Exception ex) { _status.Text = "Resume failed: " + ex.Message; }
                    };
                    Grid.SetColumn(resume, 1); row.Children.Add(resume);

                    if (PaRuX.Services.ModelDownloadStatusPolicy.CanRemove(job.Status, job.Id))
                    {
                        // Confirm first: this deletes the .partial bytes for real, unlike Pause which keeps them.
                        var remove = new Button { Content = "Remove" };
                        AutomationProperties.SetAutomationId(remove,
                            ModelDownloadPresentationPolicy.GetActionAutomationId("ModelHub", job.Id, "Remove"));
                        remove.Click += async (_, _) =>
                        {
                            var confirm = new ContentDialog
                            {
                                Title = "Remove this download?",
                                Content = job.FileName + " and its partially downloaded bytes will be deleted. This cannot be undone.",
                                PrimaryButtonText = "Remove",
                                CloseButtonText = "Cancel",
                                DefaultButton = ContentDialogButton.Close,
                                XamlRoot = XamlRoot
                            };
                            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                            try
                            {
                                await ModelDownloadClient.Current.RemoveDownloadAsync(job.Id, CancellationToken.None);
                                _status.Text = "Removed " + job.FileName + ".";
                                await RefreshAllAsync();
                            }
                            catch (Exception ex) { _status.Text = "Remove failed: " + ex.Message; }
                        };
                        Grid.SetColumn(remove, 2); row.Children.Add(remove);
                    }
                }
                _downloads.Children.Add(row);
            }
            if (jobs.Count == 0) _downloads.Children.Add(new TextBlock { Text = "No downloads yet." });
        }

        /// <summary>The incomplete split sets in the managed library, or null when the library could not be asked.
        /// A null is shown as "could not be checked", never as "none".</summary>
        private static async Task<List<ManagedModelInfo>?> TryGetIncompleteSplitSetsAsync(CancellationToken ct)
        {
            try { return await ModelDownloadClient.Current.ListIncompleteSplitSetsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; }
        }

        /// <summary>Shows incomplete sets as actionable rows. Resume replans the exact recorded choice;
        /// Remove uses the server's whole-set deletion behavior.</summary>
        private void RenderIncompleteSplitSets(List<ManagedModelInfo>? incompleteSets)
        {
            if (incompleteSets == null)
            {
                _installed.Children.Add(new TextBlock
                {
                    Text = "Incomplete split models could not be checked. Refresh to try again.",
                    FontSize = 12,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }
            foreach (ManagedModelInfo incomplete in incompleteSets)
            {
                ModelHubSplitSetPresentation.IncompleteSetRow? presentation =
                    ModelHubSplitSetPresentation.BuildIncompleteSetRow(incomplete);
                if (presentation == null) continue;

                var row = new StackPanel { Spacing = 5, Margin = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = "Incomplete split model · " + presentation.Title,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(new TextBlock { Text = presentation.Note, FontSize = 12, Opacity = 0.82, TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (presentation.CanResume)
                {
                    var resume = new Button { Content = "Resume" };
                    resume.Click += async (_, _) =>
                    {
                        resume.IsEnabled = false;
                        try
                        {
                            await ChooseQuantChoiceAsync(presentation.RepoId!, new ModelQuantChoice
                            {
                                Quantization = presentation.Quantization ?? string.Empty,
                                SetName = presentation.SetName,
                                Directory = presentation.Directory ?? string.Empty,
                                IsSplit = true,
                                PartCount = incomplete.SplitSet!.PartCount,
                                Complete = true,
                                FilePaths = incomplete.SplitSet.PartFileNames
                            });
                        }
                        catch (Exception ex) { _status.Text = "Resume failed: " + ex.Message; }
                        finally { resume.IsEnabled = true; SetBusy(false, _status.Text); }
                    };
                    actions.Children.Add(resume);
                }
                else
                {
                    actions.Children.Add(new TextBlock
                    {
                        Text = "Resume requires a recorded repository and set identity.",
                        FontSize = 12,
                        Opacity = 0.72,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                var remove = new Button { Content = "Remove set" };
                remove.Click += async (_, _) =>
                {
                    var confirm = new ContentDialog
                    {
                        Title = "Remove incomplete split set?",
                        Content = "This removes every part already present for " + presentation.Title + " from the PaRuX Managed Model Library.",
                        PrimaryButtonText = "Remove set",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot
                    };
                    if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                    remove.IsEnabled = false;
                    try
                    {
                        await ModelDownloadClient.Current.DeleteManagedModelAsync(presentation.RemoveFileName, CancellationToken.None);
                        _status.Text = "Removed incomplete set " + presentation.Title + ".";
                        await RefreshAllAsync();
                    }
                    catch (Exception ex) { _status.Text = "Remove failed: " + ex.Message; }
                    finally { remove.IsEnabled = true; }
                };
                actions.Children.Add(remove);
                row.Children.Add(actions);
                _installed.Children.Add(row);
            }
        }

        private void RenderInstalled(IReadOnlyList<ManagedModelInfo> models)
        {
            _installed.Children.Clear();
            foreach (ManagedModelInfo model in models.OrderBy(m => m.Alias ?? m.FileName, StringComparer.OrdinalIgnoreCase))
            {
                ModelHardwareFitEstimate fit = ModelHardwareFitEstimator.Estimate(model.SizeBytes, _hardware);
                var row = new StackPanel { Spacing = 3 };
                row.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(model.Alias) ? model.FileName : model.Alias + "  (" + model.FileName + ")", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock
                {
                    Text = FormatBytes(model.SizeBytes) + " · " + model.Quantization
                        + " · " + ModelFitVerdictWords.Of(fit.Verdict) + " · " + fit.Summary
                        + (model.LastBenchmarkTokensPerSecond.HasValue ? " · " + model.LastBenchmarkTokensPerSecond.Value.ToString("0.0") + " tok/s" : string.Empty)
                        + (string.IsNullOrWhiteSpace(model.RepoId) ? string.Empty : " · " + model.RepoId),
                    FontSize = 12,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.Wrap
                });
                row.Children.Add(new TextBlock
                {
                    Text = FormatModelProvenance(model.Provenance),
                    FontSize = 12,
                    Opacity = 0.82,
                    TextWrapping = TextWrapping.Wrap
                });
                if (Uri.TryCreate(model.Provenance?.ModelCardUrl, UriKind.Absolute, out Uri? modelCardUri)
                    && string.Equals(modelCardUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(modelCardUri.Host, "huggingface.co", StringComparison.OrdinalIgnoreCase))
                {
                    row.Children.Add(new HyperlinkButton
                    {
                        Content = "Open the model card for this pinned revision",
                        NavigateUri = modelCardUri,
                        HorizontalAlignment = HorizontalAlignment.Left
                    });
                }
                _installed.Children.Add(row);
            }
            if (models.Count == 0) _installed.Children.Add(new TextBlock { Text = "No PaRuX-managed GGUF model is installed yet." });
        }

        private void SetBusy(bool value, string message)
        {
            _busy.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(message)) _status.Text = message;
        }

        private static int FilePreference(string name, string? hint)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(hint))
            {
                if (hint.Contains("IQ4_XS", StringComparison.OrdinalIgnoreCase) && name.Contains("IQ4_XS", StringComparison.OrdinalIgnoreCase)) score += 100;
                if (hint.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase) && name.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase)) score += 90;
                if (hint.Contains("Q8_0", StringComparison.OrdinalIgnoreCase) && name.Contains("Q8_0", StringComparison.OrdinalIgnoreCase)) score += 80;
                if (hint.Contains("Q4_0", StringComparison.OrdinalIgnoreCase) && name.Contains("Q4_0", StringComparison.OrdinalIgnoreCase)) score += 70;
            }
            if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) score += 5;
            return score;
        }

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        private static string FormatModelProvenance(ModelDownloadProvenance? provenance)
        {
            if (provenance == null)
                return "Provenance unknown — imported, installed before evidence tracking, or not recorded.";

            string identity = string.IsNullOrWhiteSpace(provenance.Revision)
                ? provenance.RepoId + " · revision unknown"
                : provenance.RepoId + " @ " + provenance.Revision[..Math.Min(8, provenance.Revision.Length)];
            string license = provenance.LicenseMetadataStatus switch
            {
                ModelLicenseMetadataStatus.Declared => "publisher declares " + provenance.PublisherDeclaredLicense + " (not independently verified)",
                ModelLicenseMetadataStatus.NotDeclared => "publisher licence not declared",
                ModelLicenseMetadataStatus.Unavailable => "publisher licence metadata unavailable at download time",
                _ => "licence metadata not recorded"
            };
            string review = provenance.ReviewRequired
                ? provenance.ReviewAcknowledged ? "review acknowledged for this download" : "review acknowledgement not recorded"
                : provenance.LicenseMetadataStatus == ModelLicenseMetadataStatus.NotProvided
                    ? "review state not recorded"
                    : "no extra review required";
            string integrity = provenance.IntegrityStatus == "verified"
                ? "provider SHA-256 matched at download time"
                : "no provider SHA-256 was available at download time";
            return "Download record (historical; current bytes are not cryptographically re-verified): "
                + identity + " · " + license + " · " + review + " · " + integrity;
        }

        public void SetCapabilityAcquisitionProposal(CapabilityAcquisitionProposal? proposal)
        {
            _currentCapabilityAcquisitionProposal = proposal;
            if (proposal == null || proposal.Outcome == CapabilityAcquisitionOutcome.DoNotOffer)
            {
                DismissCapabilityAcquireCard();
                return;
            }

            CapabilityAcquisitionPresentation presentation = CapabilityAcquisitionPresenter.Present(proposal);
            _capabilityAcquireTitle.Text = presentation.Title;
            AutomationProperties.SetName(_capabilityAcquireCard, presentation.Title);

            _capabilityAcquireReason.Text = presentation.Reason;
            _capabilityAcquireSize.Text = presentation.SizeText;

            _capabilityAcquireDownloadButton.Visibility = presentation.CanDownload ? Visibility.Visible : Visibility.Collapsed;
            _capabilityAcquireLaterButton.Visibility = Visibility.Visible;

            _capabilityAcquireCard.Visibility = Visibility.Visible;
            OverlayEscapeManager.Instance.Push(new OverlayDescriptor(
                "ModelHub_CapabilityAcquire_Card",
                OverlayKind.Dialog,
                isDismissibleOnEscape: true));
        }

        public void DismissCapabilityAcquireCard()
        {
            _capabilityAcquireCard.Visibility = Visibility.Collapsed;
            OverlayEscapeManager.Instance.Pop("ModelHub_CapabilityAcquire_Card");
        }

        private async Task OnCapabilityAcquireDownloadClickedAsync()
        {
            CapabilityAcquisitionProposal? proposal = _currentCapabilityAcquisitionProposal;
            if (proposal == null) return;

            if (string.IsNullOrWhiteSpace(proposal.RepoId) || string.IsNullOrWhiteSpace(proposal.FileName))
            {
                _status.Text = "No download repository or file is specified for this capability.";
                return;
            }

            _capabilityAcquireDownloadButton.IsEnabled = false;
            try
            {
                // Download runs only through the existing consent-before-start path; the card never starts a transfer on its own.
                bool queued = await ModelLicenseConsentPrompt.ConfirmAndStartRepositoryDownloadAsync(
                    XamlRoot,
                    proposal.RepoId,
                    proposal.FileName,
                    async (revision, licenseEvidence) =>
                    {
                        var download = new ModelDownloadRequest
                        {
                            RepoId = proposal.RepoId,
                            FileName = proposal.FileName,
                            DownloadUrl = !string.IsNullOrWhiteSpace(proposal.DownloadUrl)
                                ? proposal.DownloadUrl
                                : "https://huggingface.co/" + proposal.RepoId + "/resolve/" + revision + "/" + proposal.FileName,
                            SizeBytes = proposal.DownloadSizeBytes.GetValueOrDefault(),
                            LicenseEvidence = licenseEvidence
                        };
                        ModelDownloadClient.PinRequestToRevision(download, revision);
                        await ModelDownloadClient.Current.StartDownloadAsync(download, CancellationToken.None);
                    });

                if (queued)
                {
                    DismissCapabilityAcquireCard();
                    _status.Text = "Queued " + proposal.FileName + " for download after licence review.";
                    await RefreshAllAsync();
                }
            }
            catch (Exception ex)
            {
                _status.Text = "Capability model download failed: " + ex.Message;
            }
            finally
            {
                _capabilityAcquireDownloadButton.IsEnabled = true;
            }
        }
    }
}
