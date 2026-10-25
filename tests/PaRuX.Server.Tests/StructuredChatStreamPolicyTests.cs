using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class StructuredChatStreamPolicyTests
{
    [Theory]
    [InlineData("stop")]
    [InlineData("length")]
    [InlineData("tool_calls")]
    public void Terminal_choice_finishes_even_without_done_sentinel(string reason)
    {
        string frame = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"" + reason + "\"}]}";

        Assert.True(StructuredChatStreamPolicy.TryReadFinishReason(frame, out string? actual));
        Assert.Equal(reason, actual);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"delta\":{\"content\":\"still streaming\"},\"finish_reason\":null}]}")]
    [InlineData("{\"parux_event\":\"structured_output\",\"applied\":true}")]
    [InlineData("not-json")]
    public void Non_terminal_frames_do_not_finish(string frame)
    {
        Assert.False(StructuredChatStreamPolicy.TryReadFinishReason(frame, out string? reason));
        Assert.Null(reason);
    }
}
