namespace PaRuX.Models
{
    /// <summary>
    /// The destination the rule router picked for a user turn. Execution-capable routes are
    /// handled by their dedicated local services; chat routes stream through PaRuX Core.
    /// </summary>
    public enum RouteKind
    {
        ChatFast,
        ChatNormal,
        DeepReasoning,
        /// <summary>
        /// Legacy/direct vision route. Kept for Computer Use and compatibility paths that require
        /// the currently serving resident itself to accept pixels.
        /// </summary>
        Vision,
        /// <summary>
        /// Normal attached-image chat. Core may use a temporary local vision helper to convert the
        /// pixels into bounded observation evidence, then route the resulting text-only turn back
        /// through the user's main model. This intentionally does not trigger ChatSession's direct
        /// vision-profile preload.
        /// </summary>
        VisionDelegated,
        BinaryAnalysis,
        ComputerUse,
        Image,
        ImageEdit,
        Video,
        /// <summary>Local music generation (ACE-Step) from a natural request such as "compose a song about the sea".</summary>
        Music,
        CodeHandoff
    }
}
