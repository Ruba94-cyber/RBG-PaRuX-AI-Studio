using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class UniversalDiffFormatterTests
{
    [Fact]
    public void Format_emits_copyable_unified_prefixes_in_server_order()
    {
        var result = new DiffResult
        {
            Lines =
            {
                new DiffLine { Kind = "unchanged", Text = "same" },
                new DiffLine { Kind = "removed", Text = "old" },
                new DiffLine { Kind = "added", Text = "new" }
            }
        };

        Assert.Equal("  same\n- old\n+ new", UniversalDiffFormatter.Format(result));
    }

    [Fact]
    public void Format_of_an_empty_result_is_empty_and_has_no_phantom_line()
    {
        Assert.Equal(string.Empty, UniversalDiffFormatter.Format(new DiffResult()));
    }
}
