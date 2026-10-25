using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The guided half of Training Lab: check the run before committing to it, expose every
    /// PaRuX-managed training method and keep method-specific parameters attached to the same Core
    /// job boundary used by the historical Lifecycle Lab start button.
    ///
    /// A training run loads a model, allocates VRAM and trains for minutes or hours. Every way it
    /// fails for a knowable reason - file shape, model family, memory fit, invalid GRPO reward mode,
    /// media paths or sequence budget - should therefore be decided before allocating the GPU.
    /// Blocking findings and warnings remain intentionally distinct.
    /// </summary>
    public sealed partial class LifecycleLabView
    {
        private readonly StackPanel _planFindings = new() { Spacing = 4 };
        private readonly TextBlock _planSummary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly TextBlock _planRecommendation = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        private readonly Button _applyRecommended = new() { Content = "Use recommended settings", IsEnabled = false };

        private readonly NumberBox _learningRate = new()
        {
            Value = 0.0002, Minimum = 0.00000001, Maximum = 0.01,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 150
        };
        private readonly NumberBox _maxSequenceLength = new()
        {
            Value = 2048, Minimum = 128, Maximum = 1_048_576,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 145
        };
        private readonly NumberBox _dpoBeta = new()
        {
            Value = 0.1, Minimum = 0.000001, Maximum = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120
        };
        private readonly ComboBox _grpoReward = new() { MinWidth = 190 };
        private readonly NumberBox _grpoGenerations = new()
        {
            Value = 4, Minimum = 2, Maximum = 16,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 110
        };
        private readonly TextBox _textField = new() { Text = "text", Header = "Text field", MinWidth = 150 };
        private readonly TextBox _imageField = new() { Text = "image", Header = "Image field", MinWidth = 150 };
        private readonly TextBox _audioField = new() { Text = "audio", Header = "Audio field", MinWidth = 150 };
        private readonly TextBlock _methodRequirements = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };

        private TrainingPlanResult? _lastPlan;

        private UIElement BuildTrainingWizardControls()
        {
            EnsureAllTrainingMethods();
            EnsureGrpoRewards();
            _trainingPath.PlaceholderText = "JSONL: messages | prompt/chosen/rejected | text | prompt/reference | image+messages | audio+text";

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "METHOD-SPECIFIC SETTINGS",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.72
            });

            var general = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            general.Children.Add(Caption("Learning rate"));
            general.Children.Add(_learningRate);
            general.Children.Add(Caption("Max sequence"));
            general.Children.Add(_maxSequenceLength);
            panel.Children.Add(general);

            var preference = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            preference.Children.Add(Caption("DPO beta"));
            preference.Children.Add(_dpoBeta);
            preference.Children.Add(Caption("GRPO reward"));
            preference.Children.Add(_grpoReward);
            preference.Children.Add(Caption("Generations"));
            preference.Children.Add(_grpoGenerations);
            panel.Children.Add(preference);

            var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            fields.Children.Add(_textField);
            fields.Children.Add(_imageField);
            fields.Children.Add(_audioField);
            panel.Children.Add(fields);
            panel.Children.Add(_methodRequirements);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var check = new Button { Content = "Check this run" };
            Id(check, "LifecycleLab_CheckRun_Button");
            check.Click += async (_, _) => await CheckTrainingRunAsync();
            actions.Children.Add(check);

            _applyRecommended.Click += (_, _) => ApplyRecommendedSettings();
            actions.Children.Add(_applyRecommended);
            panel.Children.Add(actions);

            panel.Children.Add(_planSummary);
            panel.Children.Add(_planRecommendation);
            panel.Children.Add(_planFindings);

            _trainingMethod.SelectionChanged += (_, _) => RefreshMethodControlsAndDraft();
            _learningRate.ValueChanged += (_, _) => SyncInteractiveDraft();
            _maxSequenceLength.ValueChanged += (_, _) => SyncInteractiveDraft();
            _dpoBeta.ValueChanged += (_, _) => SyncInteractiveDraft();
            _grpoReward.SelectionChanged += (_, _) => SyncInteractiveDraft();
            _grpoGenerations.ValueChanged += (_, _) => SyncInteractiveDraft();
            _textField.TextChanged += (_, _) => SyncInteractiveDraft();
            _imageField.TextChanged += (_, _) => SyncInteractiveDraft();
            _audioField.TextChanged += (_, _) => SyncInteractiveDraft();

            RefreshMethodControlsAndDraft();
            return panel;
        }

        private static TextBlock Caption(string text) => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.78
        };

        private void EnsureAllTrainingMethods()
        {
            var existing = _trainingMethod.Items
                .OfType<ComboBoxItem>()
                .Select(item => item.Tag?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);

            void Add(string title, string tag)
            {
                if (!existing.Contains(tag))
                    _trainingMethod.Items.Add(new ComboBoxItem { Content = title, Tag = tag });
            }

            Add("SFT", TrainingMethod.Sft);
            Add("DPO", TrainingMethod.Dpo);
            Add("Continued pretraining", TrainingMethod.ContinuedPretraining);
            Add("GRPO", TrainingMethod.Grpo);
            Add("Vision SFT", TrainingMethod.VisionSft);
            Add("TTS SFT", TrainingMethod.TtsSft);
            if (_trainingMethod.SelectedIndex < 0) _trainingMethod.SelectedIndex = 0;
        }

        private void EnsureGrpoRewards()
        {
            if (_grpoReward.Items.Count > 0) return;
            _grpoReward.Items.Add(new ComboBoxItem { Content = "Exact reference", Tag = GrpoRewardMode.ExactReference });
            _grpoReward.Items.Add(new ComboBoxItem { Content = "Contains reference", Tag = GrpoRewardMode.ContainsReference });
            _grpoReward.Items.Add(new ComboBoxItem { Content = "XML format", Tag = GrpoRewardMode.XmlFormat });
            _grpoReward.Items.Add(new ComboBoxItem { Content = "JSON format", Tag = GrpoRewardMode.JsonFormat });
            _grpoReward.SelectedIndex = 0;
        }

        private void RefreshMethodControlsAndDraft()
        {
            string method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft;
            _dpoBeta.IsEnabled = method == TrainingMethod.Dpo;
            _grpoReward.IsEnabled = method == TrainingMethod.Grpo;
            _grpoGenerations.IsEnabled = method == TrainingMethod.Grpo;
            _textField.IsEnabled = method is TrainingMethod.ContinuedPretraining or TrainingMethod.TtsSft;
            _imageField.IsEnabled = method == TrainingMethod.VisionSft;
            _audioField.IsEnabled = method == TrainingMethod.TtsSft;

            _methodRequirements.Text = method switch
            {
                TrainingMethod.Dpo => "DPO · every row needs prompt, chosen and rejected. Beta controls preference strength.",
                TrainingMethod.ContinuedPretraining => "Continued pretraining · every row needs the selected text field. Use conservative learning rates for domain adaptation.",
                TrainingMethod.Grpo => "GRPO · prompt is required. Reference reward modes also require reference; XML/JSON rewards are deterministic format checks and never execute dataset code.",
                TrainingMethod.VisionSft => "Vision SFT · each row needs the selected image field plus messages (or prompt/response); the base model must be vision-language capable.",
                TrainingMethod.TtsSft => "TTS SFT · each row needs the selected audio + text fields. The current real PaRuX trainer is SpeechT5-family only.",
                _ => "SFT · each row needs a messages[] conversation."
            };
            SyncInteractiveDraft();
        }

        private void SyncInteractiveDraft()
        {
            try
            {
                TrainingLabClient.Current.SetInteractiveOptions(new TrainingInteractiveOptions
                {
                    Method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft,
                    LearningRate = SafeDouble(_learningRate.Value, 0.0002),
                    DpoBeta = SafeDouble(_dpoBeta.Value, 0.1),
                    MaxSequenceLength = SafeWholeNumber(_maxSequenceLength.Value, 2048),
                    GrpoRewardMode = SelectedTag(_grpoReward) ?? GrpoRewardMode.ExactReference,
                    GrpoGenerations = SafeWholeNumber(_grpoGenerations.Value, 4),
                    TextField = (_textField.Text ?? "text").Trim(),
                    ImageField = (_imageField.Text ?? "image").Trim(),
                    AudioField = (_audioField.Text ?? "audio").Trim()
                });
            }
            catch (InvalidDataException ex)
            {
                _planSummary.Text = "Training settings need attention: " + ex.Message;
            }
        }

        private async Task CheckTrainingRunAsync()
        {
            string path = (_trainingPath.Text ?? string.Empty).Trim();
            string model = (_baseModel.Text ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                _planSummary.Text = "Enter the training file first.";
                return;
            }

            _planFindings.Children.Clear();
            _planSummary.Text = "Checking…";
            _planRecommendation.Text = string.Empty;
            _applyRecommended.IsEnabled = false;

            try
            {
                SyncInteractiveDraft();
                TrainingPlanResult plan = await TrainingLabClient.Current.PlanAsync(new TrainingPlanRequest
                {
                    TrainJsonlPath = path,
                    BaseModel = model,
                    Method = SelectedTag(_trainingMethod) ?? TrainingMethod.Sft,
                    MaxSteps = SafeWholeNumber(_steps.Value, 60),
                    LoraRank = SafeWholeNumber(_rank.Value, 16),
                    LearningRate = SafeDouble(_learningRate.Value, 0.0002),
                    MaxSequenceLength = SafeWholeNumber(_maxSequenceLength.Value, 2048),
                    GrpoRewardMode = SelectedTag(_grpoReward) ?? GrpoRewardMode.ExactReference
                }, CancellationToken.None);

                _lastPlan = plan;
                RenderPlan(plan);
            }
            catch (Exception ex)
            {
                _planSummary.Text = "Could not check this run: " + ex.Message;
            }
        }

        private void RenderPlan(TrainingPlanResult plan)
        {
            _planSummary.Text = (plan.CanStart ? "Ready to start. " : "This run cannot succeed as configured. ")
                + plan.DatasetSummary
                + (plan.DatasetSampleTruncated
                    ? " (Only the bounded shape sample was read; the runner still validates the real file.)"
                    : string.Empty)
                + (plan.EstimatedGib > 0
                    ? $" Needs roughly {plan.EstimatedGib:0.#} GB against {plan.AvailableGib:0.#} GB available."
                    : string.Empty);

            int recommendedSequence = plan.RecommendedMaxSequenceLength > 0
                ? plan.RecommendedMaxSequenceLength
                : RecommendedSequenceFor(plan.DetectedMethod);
            _planRecommendation.Text = plan.RecommendedMaxSteps > 0
                ? $"Suggested: rank {plan.RecommendedLoraRank}, {plan.RecommendedMaxSteps:N0} steps, "
                    + $"learning rate {plan.RecommendedLearningRate:G}, max sequence {recommendedSequence:N0}. "
                    + plan.RecommendationRationale
                : string.Empty;
            _applyRecommended.IsEnabled = plan.RecommendedMaxSteps > 0;

            _planFindings.Children.Clear();
            foreach (TrainingPlanFinding finding in plan.Findings
                .OrderBy(item => item.Severity == "Blocking" ? 0 : 1))
            {
                bool blocking = finding.Severity == "Blocking";
                _planFindings.Children.Add(new TextBlock
                {
                    Text = (blocking ? "Must fix · " : "Worth knowing · ") + finding.Field + ": " + finding.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = blocking
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                    Opacity = blocking ? 1.0 : 0.75
                });
            }
        }

        private void ApplyRecommendedSettings()
        {
            if (_lastPlan == null) return;
            _steps.Value = _lastPlan.RecommendedMaxSteps;
            _rank.Value = _lastPlan.RecommendedLoraRank;
            if (_lastPlan.RecommendedLearningRate > 0) _learningRate.Value = _lastPlan.RecommendedLearningRate;
            _maxSequenceLength.Value = _lastPlan.RecommendedMaxSequenceLength > 0
                ? _lastPlan.RecommendedMaxSequenceLength
                : RecommendedSequenceFor(_lastPlan.DetectedMethod);
            SyncInteractiveDraft();
            _planSummary.Text = "Applied the suggested settings. Check the run again to confirm.";
        }

        private static int RecommendedSequenceFor(string? method) => method switch
        {
            TrainingMethod.ContinuedPretraining => 4096,
            TrainingMethod.VisionSft => 2048,
            TrainingMethod.TtsSft => 1024,
            TrainingMethod.Grpo => 2048,
            TrainingMethod.Dpo => 2048,
            _ => 2048
        };

        private static double SafeDouble(double value, double fallback) =>
            double.IsFinite(value) && value > 0 ? value : fallback;

        /// <summary>The live numbers a run is judged by. Loss alone is not enough: a gradient norm
        /// spiking is divergence and shows there before the loss does, and a learning rate at zero
        /// explains a loss that has stopped moving without anything being wrong.</summary>
        private static string DescribeTrainingMetrics(TrainingJobStatus job)
        {
            var parts = new List<string>();
            if (job.LastLoss.HasValue) parts.Add("loss " + job.LastLoss.Value.ToString("0.####"));
            if (job.LastGradientNorm.HasValue) parts.Add("grad " + job.LastGradientNorm.Value.ToString("0.###"));
            if (job.LastLearningRate.HasValue) parts.Add("lr " + job.LastLearningRate.Value.ToString("G3"));
            if (job.LastEpoch.HasValue) parts.Add("epoch " + job.LastEpoch.Value.ToString("0.##"));
            return parts.Count == 0 ? string.Empty : " · " + string.Join(" · ", parts);
        }
    }
}
