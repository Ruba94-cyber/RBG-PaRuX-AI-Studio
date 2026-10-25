using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The words Model Hub shows for a split model that is still missing parts. It is plain text with no
    /// window in it, so the test project can check the wording and the part list directly.
    /// </summary>
    public static class ManagedSplitSetNotes
    {
        /// <summary>The note for an incomplete set, or null when the set is whole.</summary>
        public static string? Describe(ManagedModelSplitInfo split)
        {
            if (split.Complete || split.MissingPartIndexes.Count == 0) return null;
            string noun = split.MissingPartIndexes.Count == 1 ? "part" : "parts";
            return "Incomplete split model " + split.SetName + ": missing " + noun + " "
                + string.Join(", ", split.MissingPartIndexes) + " of " + split.PartCount
                + ". It cannot be loaded until every part is in the library.";
        }
    }
}
