using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Model Hub's words for a split set that is still missing parts. A whole set gets no note. A set with parts
/// missing names exactly which parts are absent and says that it cannot be loaded, so the user knows what
/// to download.
/// </summary>
public sealed class ManagedSplitSetNotesTests
{
    [Fact]
    public void A_whole_set_gets_no_incomplete_note()
    {
        var split = new ManagedModelSplitInfo { SetName = "model", PartCount = 2, Complete = true };

        Assert.Null(ManagedSplitSetNotes.Describe(split));
    }

    [Fact]
    public void One_missing_part_is_named_with_its_index_and_the_total()
    {
        var split = new ManagedModelSplitInfo
        {
            SetName = "model-Q4_K_M",
            PartCount = 3,
            Complete = false,
            MissingPartIndexes = new List<int> { 2 }
        };

        string? note = ManagedSplitSetNotes.Describe(split);

        Assert.NotNull(note);
        Assert.Contains("missing part 2 of 3", note);
        Assert.Contains("cannot be loaded", note);
    }

    [Fact]
    public void Several_missing_parts_are_listed_in_index_order()
    {
        var split = new ManagedModelSplitInfo
        {
            SetName = "model",
            PartCount = 5,
            Complete = false,
            MissingPartIndexes = new List<int> { 2, 4 }
        };

        Assert.Contains("missing parts 2, 4 of 5", ManagedSplitSetNotes.Describe(split));
    }
}
