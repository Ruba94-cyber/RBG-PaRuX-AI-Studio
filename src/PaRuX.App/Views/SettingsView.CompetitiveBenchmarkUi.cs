using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PaRuX.Views
{
    public sealed partial class SettingsView
    {
        private bool _competitiveBenchmarkUiApplied;
        private bool _rewritingCompetitorStatus;

        /// <summary>
        /// The original XAML card predates PaRuX-owned inference and called these endpoints an
        /// "external backend". Runtime routing no longer honors them. Re-label the retained controls
        /// as what they now are: competitor benchmark targets. Keeping the existing x:Names/wire
        /// contracts is intentional migration compatibility; the UI must not imply Ollama/LM Studio
        /// can replace PaRuX's managed runtime.
        /// </summary>
        private void ApplyCompetitiveBenchmarkUi()
        {
            if (_competitiveBenchmarkUiApplied || ExternalBackendStatusText == null) return;
            _competitiveBenchmarkUiApplied = true;

            if (ClearExternalBackendButton != null)
                ClearExternalBackendButton.Content = "Clear benchmark target";
            if (DetectBackendsButton != null)
                DetectBackendsButton.Content = "Detect competitors";
            if (ManualBackendNameBox != null)
                ManualBackendNameBox.PlaceholderText = "Competitor label (e.g. Ollama)";
            if (ManualBackendEndpointBox != null)
                ManualBackendEndpointBox.PlaceholderText = "Benchmark endpoint, e.g. http://127.0.0.1:11434";

            // Retitle the existing card without duplicating a second copy of the large Settings XAML.
            StackPanel? content = FindVisualAncestor<StackPanel>(ExternalBackendStatusText);
            if (content != null && content.Children.Count > 0 && content.Children[0] is TextBlock explanation)
            {
                explanation.Text = "Ollama, LM Studio and other OpenAI-compatible launchers are competitor targets only. "
                    + "PaRuX sends benchmark/probe traffic to the selected endpoint under controlled workloads; "
                    + "chat, coding and model lifecycle always stay on the PaRuX-managed runtime.";
            }
            Border? card = content == null ? null : FindVisualAncestor<Border>(content);
            StackPanel? section = card == null ? null : FindVisualAncestor<StackPanel>(card);
            if (section != null && section.Children.Count > 0 && section.Children[0] is TextBlock heading)
                heading.Text = "COMPETITIVE BENCHMARK TARGET";

            // The manual action is intentionally "Set target", never "Use".
            if (ManualBackendEndpointBox != null
                && FindVisualAncestor<Grid>(ManualBackendEndpointBox) is Grid manualRow)
            {
                foreach (UIElement child in manualRow.Children)
                    if (child is Button button) button.Content = "Set target";
            }

            ExternalBackendStatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
                RewriteCompetitorStatusText());
            if (DetectedBackendsList != null)
                DetectedBackendsList.LayoutUpdated += (_, _) => RelabelDetectedCompetitorButtons();

            // OpenCode is no longer an execution dependency. VS Code remains an optional review
            // surface for the SAME PaRuX task/worktree, not a separate agent host.
            if (VsCodeCommandBox != null)
            {
                VsCodeCommandBox.Header = "VS Code / editor command";
                if (FindVisualAncestor<Grid>(VsCodeCommandBox) is Grid codingRow)
                {
                    if (codingRow.Children.Count > 0 && codingRow.Children[0] is StackPanel copy)
                    {
                        if (copy.Children.Count > 0 && copy.Children[0] is TextBlock title)
                            title.Text = "Editor for PaRuX Coding Agent worktrees";
                        if (copy.Children.Count > 1 && copy.Children[1] is TextBlock detail)
                            detail.Text = "The native PaRuX agent owns execution, sandboxing, checkpoints and verification. This command only opens its reviewable worktree.";
                    }
                    if (codingRow.ColumnDefinitions.Count > 2)
                        codingRow.ColumnDefinitions[2].Width = new GridLength(0);
                }
            }
            if (OpenCodeCommandBox != null)
            {
                OpenCodeCommandBox.Visibility = Visibility.Collapsed;
                OpenCodeCommandBox.IsEnabled = false;
            }

            RewriteCompetitorStatusText();
            RelabelDetectedCompetitorButtons();
        }

        private void RewriteCompetitorStatusText()
        {
            if (_rewritingCompetitorStatus || ExternalBackendStatusText == null) return;
            string current = ExternalBackendStatusText.Text ?? string.Empty;
            string rewritten = current;

            if (string.Equals(current, "Using PaRuX's own local model.", StringComparison.Ordinal))
            {
                rewritten = "No competitor target selected. PaRuX remains the active managed runtime.";
            }
            else if (current.StartsWith("Using ", StringComparison.Ordinal))
            {
                rewritten = "Benchmark target: " + current.Substring("Using ".Length);
            }
            else if (current.StartsWith("Couldn't load backend status:", StringComparison.Ordinal))
            {
                rewritten = "Couldn't load benchmark-target status:" + current.Substring("Couldn't load backend status:".Length);
            }
            else if (current.StartsWith("Couldn't set that backend:", StringComparison.Ordinal))
            {
                rewritten = "Couldn't set that benchmark target:" + current.Substring("Couldn't set that backend:".Length);
            }
            else if (current.StartsWith("Couldn't clear that backend:", StringComparison.Ordinal))
            {
                rewritten = "Couldn't clear the benchmark target:" + current.Substring("Couldn't clear that backend:".Length);
            }

            if (string.Equals(rewritten, current, StringComparison.Ordinal)) return;
            try
            {
                _rewritingCompetitorStatus = true;
                ExternalBackendStatusText.Text = rewritten;
            }
            finally { _rewritingCompetitorStatus = false; }
        }

        private void RelabelDetectedCompetitorButtons()
        {
            if (DetectedBackendsList == null) return;
            foreach (UIElement element in DetectedBackendsList.Children)
            {
                if (element is not Grid row) continue;
                foreach (UIElement child in row.Children)
                    if (child is Button button) button.Content = "Set target";
            }
        }

        private static T? FindVisualAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            DependencyObject? current = node == null ? null : VisualTreeHelper.GetParent(node);
            while (current != null)
            {
                if (current is T typed) return typed;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
