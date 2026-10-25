using System;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure policy for the Windows chat visual tree. Conversation persistence and model context are
    /// deliberately outside this class: only the number of XAML message rows materialized at once is
    /// bounded. This keeps opening a long conversation O(window) instead of O(total history) while
    /// still allowing the user to expand older history explicitly in predictable pages.
    /// </summary>
    public static class ChatRenderWindowPolicy
    {
        public const int InitialMessageBudget = 120;
        public const int HistoryPageSize = 80;
        public const int LiveRenderSlack = 24;

        public static int ClampBudget(int totalMessages, int requestedBudget)
        {
            if (totalMessages <= 0) return 0;
            return Math.Min(totalMessages, Math.Max(1, requestedBudget));
        }

        public static int InitialBudget(int totalMessages) =>
            ClampBudget(totalMessages, InitialMessageBudget);

        public static int GrowBudget(int totalMessages, int currentBudget) =>
            ClampBudget(totalMessages, checked(Math.Max(0, currentBudget) + HistoryPageSize));

        public static int StartIndex(int totalMessages, int budget)
        {
            int effective = ClampBudget(totalMessages, budget);
            return Math.Max(0, totalMessages - effective);
        }

        public static int HiddenCount(int totalMessages, int budget) =>
            StartIndex(totalMessages, budget);

        public static bool NeedsLiveCompaction(int renderedMessages, int budget) =>
            renderedMessages > Math.Max(0, budget) + LiveRenderSlack;
    }
}
