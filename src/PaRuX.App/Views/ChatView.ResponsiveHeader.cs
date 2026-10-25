using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        private const double ChatHeaderTitleMinimumWidth = 148;
        private bool _profileSelectorIsStacked = true;

        private void ChatHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Use the viewport cap, not the child DesiredSize: while this selector is scrollable
            // its content can report a clipped/zero desired width during an initial measure pass.
            double inlineWidth = ChatHeaderTitleMinimumWidth
                + ProfileSelectorScrollViewer.MaxWidth
                + ChatHeaderGrid.ColumnSpacing;
            bool stackSelector = e.NewSize.Width < inlineWidth;
            if (_profileSelectorIsStacked == stackSelector) return;

            _profileSelectorIsStacked = stackSelector;
            Grid.SetRow(ChatHeaderTitlePanel, 0);
            Grid.SetColumn(ChatHeaderTitlePanel, 0);
            Grid.SetColumnSpan(ChatHeaderTitlePanel, stackSelector ? 2 : 1);

            Grid.SetRow(ProfileSelectorScrollViewer, stackSelector ? 1 : 0);
            Grid.SetColumn(ProfileSelectorScrollViewer, stackSelector ? 0 : 1);
            Grid.SetColumnSpan(ProfileSelectorScrollViewer, stackSelector ? 2 : 1);
            ProfileSelectorScrollViewer.HorizontalAlignment = HorizontalAlignment.Left;
        }

        private void OnChatViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        }

        internal void ApplyResponsiveLayout(double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            var profile = WindowResponsivenessPolicy.Evaluate(width, height);

            if (InputBox != null)
            {
                InputBox.MaxHeight = profile.ComposerMaxInputHeight;
            }

            if (CodeFolderButton != null)
            {
                CodeFolderButton.MaxWidth = profile.CodeFolderButtonMaxWidth;
            }

            if (ModelSelector != null)
            {
                ModelSelector.MinWidth = profile.ModelSelectorMinWidth;
            }

            if (SkillSelector != null)
            {
                SkillSelector.MinWidth = profile.SkillSelectorMinWidth;
            }
        }
    }
}
