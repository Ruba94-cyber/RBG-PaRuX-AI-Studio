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
    /// <summary>
    /// Provides a neutral first-run route to Model Hub and optional task-based model exploration.
    /// </summary>
    public sealed partial class OnboardingView
    {
        private FirstModelAdvice? _modelAdvice;
        private AiStorageBudgetStatus? _modelBudget;
        private string? _selectedFirstModelId;

        /// <summary>Raised when the user picks a model here, carrying the Hugging Face repo id so
        /// the shell can open Model Hub already searching for it.</summary>
        public event Action<string>? FindModelRequested;

        /// <summary>Quantization hint for a model the user explicitly selected from task options.</summary>
        public string? SuggestedQuantHintForHandoff { get; private set; }

        private async Task RefreshFirstModelAdviceAsync()
        {
            if (FirstModelHardwareText == null) return;

#if DEBUG
            string? storageFixture = Environment.GetEnvironmentVariable("PARUX_UI_TEST_FIRST_MODEL_STORAGE");
            if (storageFixture is "1" or "zero" or "selection")
            {
                const long gb = 1024L * 1024 * 1024;
                var model = new RecommendedModel
                {
                    Id = "storage-fixture",
                    Title = "Storage fixture model",
                    HuggingFaceRepoId = "fixture/general",
                    ApproxParams = "7B",
                    QuantHint = "Q4_K_M",
                    EstimatedSizeBytes = 5 * gb,
                    CapabilityIds = new List<string> { ModelCapabilityIds.GeneralChat }
                };
                RecommendedModel codingModel = new()
                {
                    Id = "coding-fixture",
                    Title = "Coding fixture model",
                    Category = "Coding",
                    HuggingFaceRepoId = "fixture/coding",
                    ApproxParams = "14B",
                    QuantHint = "Q4_K_M",
                    EstimatedSizeBytes = 7 * gb,
                    CapabilityIds = new List<string> { ModelCapabilityIds.GeneralChat, ModelCapabilityIds.Coding }
                };
                RecommendedModel uncensoredModel = new()
                {
                    Id = "uncensored-fixture",
                    Title = "Uncensored fixture model",
                    Category = "General chat, uncensored",
                    HuggingFaceRepoId = "fixture/uncensored",
                    ApproxParams = "14B",
                    QuantHint = "Q4_K_M",
                    EstimatedSizeBytes = 7 * gb,
                    CapabilityIds = new List<string> { ModelCapabilityIds.GeneralChat },
                    ContentProfileId = ModelContentProfileIds.Uncensored
                };
                var generalCandidate = new FirstModelCandidate
                {
                    Model = model,
                    Suitability = "FitsOnGpu",
                    Reason = "UI test hardware fit only.",
                    Rank = 3
                };
                var codingCandidate = new FirstModelCandidate
                {
                    Model = codingModel,
                    Suitability = "FitsOnGpu",
                    Reason = "UI test candidate; coding quality is unmeasured.",
                    Rank = 2
                };
                var uncensoredCandidate = new FirstModelCandidate
                {
                    Model = uncensoredModel,
                    Suitability = "FitsOnGpu",
                    Reason = "UI test explicit content preference only.",
                    Rank = 1
                };
                FirstModelCandidate? largerGeneralCandidate = null;
                if (storageFixture == "selection")
                {
                    largerGeneralCandidate = new FirstModelCandidate
                    {
                        Model = new RecommendedModel
                        {
                            Id = "larger-general-fixture",
                            Title = "Larger general-chat fixture",
                            ApproxParams = "14B",
                            QuantHint = "Q4_K_M",
                            EstimatedSizeBytes = 7 * gb,
                            CapabilityIds = new List<string> { ModelCapabilityIds.GeneralChat }
                        },
                        Suitability = "FitsOnGpu",
                        Reason = "UI test larger general-chat candidate; task quality is unmeasured.",
                        Rank = 4
                    };
                }
                var candidates = new List<FirstModelCandidate> { generalCandidate, codingCandidate, uncensoredCandidate };
                if (largerGeneralCandidate != null) candidates.Add(largerGeneralCandidate);
                _modelAdvice = new FirstModelAdvice
                {
                    HardwareSummary = "UI test hardware fixture",
                    Headline = "Choose a compatible chat model in Model Hub.",
                    RecommendedId = model.Id,
                    Candidates = candidates,
                    CapabilityGroups = CreateFirstModelCapabilityFixture(generalCandidate, codingCandidate,
                        uncensoredCandidate, largerGeneralCandidate)
                };
                _modelBudget = new AiStorageBudgetStatus
                {
                    UsedBytes = 577 * gb,
                    LimitBytes = 700 * gb,
                    QuotaHeadroomBytes = storageFixture == "zero" ? 0 : 123 * gb,
                    DestinationFreeBytes = storageFixture switch
                    {
                        "zero" => 0,
                        "selection" => 6 * gb,
                        _ => 456 * gb
                    }
                };
                ApplyFirstModelAdvice(_modelAdvice);
                ApplyFirstModelStorage(_modelBudget);
                return;
            }
#endif

            _modelAdvice = null;
            _modelBudget = null;
            try
            {
                _modelAdvice = await ModelDownloadClient.Current.GetAdviceForThisPcAsync(CancellationToken.None);
                // The recommendation is independently useful. Do not make it wait for a second
                // Core request which can be slow while walking managed storage directories.
                ApplyFirstModelAdvice(_modelAdvice);
            }
            catch (Exception ex)
            {
                Log.Write("RefreshFirstModelAdviceAsync failed: " + ex.Message);
                FirstModelHardwareText.Text =
                    "PaRuX couldn't read this machine's hardware, so it can't estimate model fit. "
                    + "You can still choose a compatible model in Model Hub.";
                ShowFirstModelActions(installable: true);
            }

            try
            {
                _modelBudget = await ModelDownloadClient.Current.GetBudgetAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Write("RefreshFirstModelStorageAsync failed: " + ex.Message);
            }

            ApplyFirstModelStorage(_modelBudget);
        }

        private void ApplyFirstModelAdvice(FirstModelAdvice advice)
        {
            FirstModelHardwareText.Text = advice.HardwareSummary;

            if (_selectedFirstModelId != null
                && !advice.Candidates.Any(candidate => candidate.Model.Id == _selectedFirstModelId))
                _selectedFirstModelId = null;

            FirstModelCandidate? selected = advice.Candidates
                .FirstOrDefault(candidate => candidate.Model.Id == _selectedFirstModelId);
            FirstModelHeadlineText.Text = selected == null
                ? "Choose a compatible chat model in Model Hub."
                : "Selected model: " + selected.Model.Title + ". " + selected.Reason
                  + " This is a fit estimate, not a task-quality winner; coding, vision, image and video tasks may use different models.";
            FirstModelHeadlineText.Visibility = Visibility.Visible;

            if (selected != null)
            {
                FirstModelDetailText.Text = string.Join("  ·  ", new[]
                {
                    selected.Model.ApproxParams + " parameters",
                    selected.Model.QuantHint,
                    "About " + Gigabytes(selected.Model.EstimatedSizeBytes) + " download; the selected file may differ"
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
                FirstModelDetailText.Visibility = Visibility.Visible;
                if (FirstModelInstallButton != null)
                    FirstModelInstallButton.Content = "Open selected model in Model Hub";
            }
            else
            {
                FirstModelDetailText.Visibility = Visibility.Collapsed;
            }

            if (FirstModelInstallButton != null)
                FirstModelInstallButton.Content = selected == null
                    ? "Browse models in Model Hub"
                    : "Open selected model in Model Hub";
            ShowFirstModelActions(installable: true);
            ApplyFirstModelStorage(_modelBudget);
            RenderBundledOverview(BundledRuntimeOverviewPresenter.BuildOverview(_lastComponents, _modelAdvice));
        }

        private void ApplyFirstModelStorage(AiStorageBudgetStatus? budget)
        {
            if (FirstModelStorageText == null) return;
            if (budget == null)
            {
                FirstModelStorageText.Text =
                    "PaRuX quota headroom and model-destination free space are unavailable. "
                    + "No storage is reserved by this estimate.";
                return;
            }

            string quota = budget.QuotaHeadroomBytes is long headroom
                ? "PaRuX AI quota headroom: " + StorageGigabytes(headroom) + " of " + StorageGigabytes(budget.LimitBytes)
                + " (shared across managed AI storage)."
                : "PaRuX AI quota headroom: unavailable.";
            string destination = budget.DestinationFreeBytes is long freeBytes
                ? "Free space on the model destination volume: " + StorageGigabytes(freeBytes) + "."
                : "Free space on the model destination volume: unavailable.";
            FirstModelStorageText.Text = quota + "\n" + destination
                + "\nThese are current snapshots, not reservations; the exact model file may differ from the estimate.";

            FirstModelCandidate? selected = _modelAdvice?.Candidates
                .FirstOrDefault(candidate => candidate.Model.Id == _selectedFirstModelId);
            if (selected != null && budget.DestinationFreeBytes is long available
                && selected.Model.EstimatedSizeBytes > 0 && available < selected.Model.EstimatedSizeBytes)
            {
                FirstModelStorageText.Text += " Available volume space is below the approximate selected download size.";
            }
        }

        private void ShowFirstModelActions(bool installable)
        {
            if (FirstModelActions != null) FirstModelActions.Visibility = Visibility.Visible;
            if (FirstModelInstallButton != null)
                FirstModelInstallButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
            if (FirstModelOtherButton != null)
                FirstModelOtherButton.Visibility = _modelAdvice == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void FirstModelInstall_Click(object sender, RoutedEventArgs e)
        {
            FirstModelCandidate? best = _modelAdvice?.Candidates
                .FirstOrDefault(c => c.Model.Id == _selectedFirstModelId);
            if (best == null)
            {
                SuggestedQuantHintForHandoff = null;
                if (FirstModelStatusText != null)
                {
                    FirstModelStatusText.Text = "Opening Model Hub. Choose a compatible model for your tasks and hardware; nothing downloads until you choose a file.";
                    FirstModelStatusText.Visibility = Visibility.Visible;
                }
                FindModelRequested?.Invoke(string.Empty);
                return;
            }

            string repoId = best.Model.HuggingFaceRepoId;
            if (!_stateMachine.TryHandoffToModelHub(repoId))
            {
                if (FirstModelStatusText != null)
                {
                    FirstModelStatusText.Text =
                        "This model does not have a Model Hub repository yet. Choose another model to continue.";
                    FirstModelStatusText.Visibility = Visibility.Visible;
                }

                return;
            }

            // Handing off to Model Hub rather than downloading from here on purpose: the repository
            // publishes several quantisations at different sizes, and picking one silently is
            // exactly the decision a first-time user should get to see being made.
            if (FirstModelStatusText != null)
            {
                FirstModelStatusText.Text =
                    "Opening Model Hub with your selected model searched. "
                    + "Choose a compatible file and select Download. Return to Setup when ready.";
                FirstModelStatusText.Visibility = Visibility.Visible;
            }
            SuggestedQuantHintForHandoff = best.Model.QuantHint;
            FindModelRequested?.Invoke(repoId);
        }

        /// <summary>
        /// Candidate shortlists by task, with uncensored content preference kept separate. The fit
        /// calculation is not evidence that one model is better at a task than another.
        /// </summary>
        private async void FirstModelOther_Click(object sender, RoutedEventArgs e)
        {
            if (_modelAdvice == null) return;
            FirstModelAdvice advice = _modelAdvice;

            var body = new StackPanel { Spacing = 14 };
            body.Children.Add(new TextBlock
            {
                Text = "These are task-specific candidates, not validated winners. For text models, memory fit is estimated; task quality and local speed need matching measurements. Uncensored is a separate content preference, not an automatic mode.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Model candidates by task",
                CloseButtonText = "Close"
            };

            foreach (FirstModelCapabilityGroup group in advice.CapabilityGroups)
            {
                var section = new StackPanel { Spacing = 5 };
                section.Children.Add(new TextBlock
                {
                    Text = group.Title,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"]
                });
                section.Children.Add(new TextBlock
                {
                    Text = group.EvidenceNote,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
                });

                foreach (FirstModelCandidate candidate in group.TextCandidates)
                {
                    bool canSelect = candidate.Suitability is "FitsOnGpu" or "SplitWithRam" or "CpuOnly";
                    bool isStandardGeneralChat = group.CapabilityId == ModelCapabilityIds.GeneralChat
                        && group.ContentProfileId == ModelContentProfileIds.Standard;
                    string routeRole = group.ContentProfileId == ModelContentProfileIds.Uncensored
                        ? "uncensored"
                        : group.CapabilityId switch
                        {
                            ModelCapabilityIds.Reasoning => "reasoning",
                            ModelCapabilityIds.Coding => "coding",
                            ModelCapabilityIds.Vision => "vision",
                            _ => "standard"
                        };
                    var entry = new StackPanel { Spacing = 3, Margin = new Thickness(8, 2, 0, 7) };
                    entry.Children.Add(new TextBlock
                    {
                        Text = candidate.Model.Title + "  ·  " + DescribeSuitability(candidate.Suitability),
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"]
                    });
                    entry.Children.Add(new TextBlock
                    {
                        Text = candidate.Reason,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
                    });
                    entry.Children.Add(new TextBlock
                    {
                        Text = candidate.Model.Category + "  ·  " + candidate.Model.HuggingFaceRepoId
                              + "  ·  " + group.EvidenceState,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                    });

                    var choose = new Button
                    {
                        Content = !canSelect
                            ? "Not a fit for this PC"
                            : isStandardGeneralChat
                                ? "Select for general chat"
                                : "Find " + routeRole + " candidate in Model Hub",
                        IsEnabled = canSelect && (isStandardGeneralChat
                            || !string.IsNullOrWhiteSpace(candidate.Model.HuggingFaceRepoId)),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 3, 0, 0)
                    };
                    AutomationProperties.SetAutomationId(choose,
                        (isStandardGeneralChat ? "FirstModelUseCandidateButton_" : "FirstModelFindCandidateButton_")
                        + candidate.Model.Id);
                    choose.Click += (_, _) =>
                    {
                        if (!isStandardGeneralChat)
                        {
                            if (FirstModelStatusText != null)
                            {
                                FirstModelStatusText.Text = "Opening Model Hub to inspect " + candidate.Model.Title + ". "
                                    + "This does not download the model or change your general-chat selection. "
                                    + "After choosing a file, add it to the " + routeRole
                                    + " role in Model Intelligence & Routing if you want to use it there.";
                                FirstModelStatusText.Visibility = Visibility.Visible;
                            }
                            SuggestedQuantHintForHandoff = candidate.Model.QuantHint;
                            FindModelRequested?.Invoke(candidate.Model.HuggingFaceRepoId);
                            dialog.Hide();
                            return;
                        }

                        _selectedFirstModelId = candidate.Model.Id;
                        ApplyFirstModelAdvice(advice);
                        dialog.Hide();
                    };
                    entry.Children.Add(choose);
                    section.Children.Add(entry);
                }

                foreach (RecommendedCheckpoint checkpoint in group.MediaCandidates)
                {
                    section.Children.Add(new TextBlock
                    {
                        Text = checkpoint.Title + "  ·  " + checkpoint.FileName + "  ·  " + group.EvidenceState,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXBodyStrongTextStyle"]
                    });
                    section.Children.Add(new TextBlock
                    {
                        Text = checkpoint.Description,
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXCaptionTextStyle"]
                    });
                }

                if (group.TextCandidates.Count == 0 && group.MediaCandidates.Count == 0)
                    section.Children.Add(new TextBlock
                    {
                        Text = "No candidate is listed; PaRuX will not substitute a general-chat model for this task.",
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["PaRuXSubtleCaptionStyle"]
                    });

                body.Children.Add(section);
            }

            dialog.Content = new ScrollViewer { Content = body, MaxHeight = 520 };
            await dialog.ShowAsync();
        }

        private static List<FirstModelCapabilityGroup> CreateFirstModelCapabilityFixture(
            FirstModelCandidate generalCandidate, FirstModelCandidate codingCandidate,
            FirstModelCandidate uncensoredCandidate, FirstModelCandidate? largerGeneralCandidate = null)
        {
            var groups = new List<FirstModelCapabilityGroup>
            {
                new()
                {
                    CapabilityId = ModelCapabilityIds.GeneralChat,
                    Title = "General chat",
                    ContentProfileId = ModelContentProfileIds.Standard,
                    EvidenceState = ModelAdviceEvidenceStates.Provisional,
                    EvidenceNote = "UI test: task quality and speed have not been measured.",
                    TextCandidates = new List<FirstModelCandidate> { generalCandidate }
                },
                new()
                {
                    CapabilityId = ModelCapabilityIds.Coding,
                    Title = "Coding",
                    ContentProfileId = ModelContentProfileIds.Standard,
                    EvidenceState = ModelAdviceEvidenceStates.Provisional,
                    EvidenceNote = "UI test: coding quality has not been measured.",
                    TextCandidates = new List<FirstModelCandidate> { codingCandidate }
                },
                new()
                {
                    CapabilityId = ModelCapabilityIds.GeneralChat,
                    Title = "General chat — uncensored preference",
                    ContentProfileId = ModelContentProfileIds.Uncensored,
                    EvidenceState = ModelAdviceEvidenceStates.Provisional,
                    EvidenceNote = "Explicit preference only; never selected implicitly.",
                    TextCandidates = new List<FirstModelCandidate> { uncensoredCandidate }
                },
                new()
                {
                    CapabilityId = ModelCapabilityIds.ImageGenerate,
                    Title = "Image generation",
                    EvidenceState = ModelAdviceEvidenceStates.Provisional,
                    EvidenceNote = "Image quality and generation time are unmeasured.",
                    MediaCandidates = new List<RecommendedCheckpoint>
                    {
                        new() { Id = "image-fixture", Type = "image", Title = "Image fixture checkpoint", FileName = "image-fixture.safetensors" }
                    }
                },
                new()
                {
                    CapabilityId = ModelCapabilityIds.ImageEdit,
                    Title = "Image editing",
                    EvidenceState = ModelAdviceEvidenceStates.Provisional,
                    EvidenceNote = "Edit fidelity and generation time are unmeasured.",
                    MediaCandidates = new List<RecommendedCheckpoint>
                    {
                        new() { Id = "edit-fixture", Type = "edit", Title = "Edit fixture checkpoint", FileName = "edit-fixture.safetensors" }
                    }
                },
                new()
                {
                    CapabilityId = ModelCapabilityIds.VideoGenerate,
                    Title = "Video generation",
                    EvidenceState = ModelAdviceEvidenceStates.Provisional,
                    EvidenceNote = "No validated video candidate is available."
                }
            };
            if (largerGeneralCandidate != null)
                groups.First(group => group.CapabilityId == ModelCapabilityIds.GeneralChat
                    && group.ContentProfileId == ModelContentProfileIds.Standard)
                    .TextCandidates.Add(largerGeneralCandidate);
            return groups;
        }

        private static string DescribeSuitability(string suitability) => suitability switch
        {
            "FitsOnGpu" => "fits on your GPU",
            "SplitWithRam" => "partly in system RAM, slower",
            "CpuOnly" => "runs on the processor, much slower",
            "TooLarge" => "too large for this PC",
            _ => "fit unknown"
        };

        private static string Gigabytes(long bytes) =>
            bytes <= 0
                ? "unknown size"
                : (bytes / 1024d / 1024d / 1024d).ToString("0.#",
                    System.Globalization.CultureInfo.InvariantCulture) + " GB";

        private static string StorageGigabytes(long bytes) =>
            (Math.Max(0, bytes) / 1024d / 1024d / 1024d).ToString("0.#",
                System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }
}
