using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Models;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Inline media job status under an assistant reply: progress, cancelled label and Retry.</summary>
    public sealed partial class ChatView
    {
        private FrameworkElement BuildMediaPanel(ChatMessage message, MessageVisuals visuals)
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

            var progress = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(progress, "Chat_Media_Progress");

            var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(cancel, "Chat_Media_Cancel_Button");
            cancel.Click += (s, e) =>
            {
                if (ChatSession.Current.IsMediaReattached(message)) ChatSession.Current.CancelMediaJob(message);
                else ChatSession.Current.Cancel();
                cancel.IsEnabled = false;
            };

            var cancelled = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
            AutomationProperties.SetAutomationId(cancelled, "Chat_Media_Cancelled_Text");

            var retry = new Button { Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(retry, "Chat_Media_Retry_Button");
            retry.Click += async (s, e) =>
            {
                retry.IsEnabled = false;
                await ChatSession.Current.RetryMediaAsync(message);
            };

            panel.Children.Add(progress);
            panel.Children.Add(cancel);
            panel.Children.Add(cancelled);
            panel.Children.Add(retry);
            visuals.MediaProgress = progress;
            visuals.MediaCancel = cancel;
            visuals.MediaCancelled = cancelled;
            visuals.MediaRetry = retry;
            visuals.MediaPanel = panel;
            return panel;
        }

        private void SyncMediaPanel(ChatMessage message, MessageVisuals visuals)
        {
            if (visuals.MediaPanel == null || visuals.MediaProgress == null || visuals.MediaCancelled == null
                || visuals.MediaRetry == null || visuals.MediaCancel == null) return;
            ChatMediaPresentation p = ChatMediaJobReconciler.Present(message);
            visuals.MediaProgress.Text = p.ProgressText ?? string.Empty;
            visuals.MediaProgress.Visibility = p.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            visuals.MediaCancel.Visibility = p.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            if (p.ShowProgress) visuals.MediaCancel.IsEnabled = true;
            visuals.MediaCancelled.Text = p.CancelledText ?? string.Empty;
            visuals.MediaCancelled.Visibility = p.CancelledText != null ? Visibility.Visible : Visibility.Collapsed;
            visuals.MediaRetry.Content = p.RetryLabel;
            visuals.MediaRetry.Visibility = p.ShowRetry ? Visibility.Visible : Visibility.Collapsed;
            if (p.ShowRetry) visuals.MediaRetry.IsEnabled = !ChatSession.Current.IsBusy;
            visuals.MediaPanel.Visibility = p.ShowProgress || p.ShowRetry || p.CancelledText != null
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
