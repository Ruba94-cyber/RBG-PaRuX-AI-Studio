namespace PaRuX.Services
{
    public enum ChatHeaderDot { Ok, Warn, Off }

    /// <summary>
    /// Pure text and dot for the chat header. The header used to read only the text runtime, so it said
    /// "Model idle" while a native coding task was already running (indexing the repository before the
    /// model is asked for anything). The running coding task's own stage is now part of the answer.
    /// </summary>
    public static class ChatHeaderStatusPresenter
    {
        public readonly record struct Header(string Text, ChatHeaderDot Dot);

        public static Header Describe(LocalAiState state, string? runningCodingTaskStage)
        {
            bool coding = runningCodingTaskStage != null;
            if (state.Healthy)
                return new Header(state.ShortProfileLabel + (coding ? " · coding task" : string.Empty), ChatHeaderDot.Ok);
            if (state.GameActive)
                return new Header("Paused for gaming", ChatHeaderDot.Warn);
            if (state.ServerProcessPresent)
                return new Header("Loading model…" + (coding ? " · coding task" : string.Empty), ChatHeaderDot.Warn);
            if (coding)
            {
                string stage = string.IsNullOrWhiteSpace(runningCodingTaskStage) ? "Starting" : runningCodingTaskStage.Trim();
                return new Header("Coding task · " + stage, ChatHeaderDot.Warn);
            }
            return new Header("Model idle", ChatHeaderDot.Off);
        }
    }
}
