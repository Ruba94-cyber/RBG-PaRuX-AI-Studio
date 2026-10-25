using Microsoft.UI.Xaml;

namespace PaRuX.Views
{
    public sealed partial class ChatView
    {
        /// <summary>
        /// ChatView is a plain UserControl with no Style/ControlTemplate of its own, so WinUI never
        /// actually invokes this override in practice - confirmed live (a temporary diagnostic log
        /// showed it never ran on startup or on theme/window changes). The real, guaranteed call site
        /// for these idempotent initializers is ChatView.OnLoaded in ChatView.xaml.cs, the first point
        /// where SkillSelector.Parent is populated. This override is kept only as a harmless defensive
        /// safety net in case a future WinUI version does apply a template to this control.
        /// </summary>
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            InitializeBoundedChatRendering();
            InitializeNativeCodingHandoffUpgrade();
            InitializeConversationBranchHistory();
        }
    }
}