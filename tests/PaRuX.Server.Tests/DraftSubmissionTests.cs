using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DraftSubmissionTests
{
    [Fact]
    public async Task Accepted_submit_reports_success_so_the_box_can_be_cleared()
    {
        int calls = 0;

        DraftSubmitResult result = await DraftSubmission.SubmitAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.True(result.Accepted);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Refused_submit_is_not_accepted_so_the_typed_draft_stays_in_the_box()
    {
        DraftSubmitResult result = await DraftSubmission.SubmitAsync(
            _ => Task.FromException(new InvalidOperationException("the memory store is read-only")),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("the memory store is read-only", result.Error);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_looking_like_a_refused_save()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DraftSubmission.SubmitAsync(
            ct => Task.FromCanceled(ct),
            cts.Token));
    }
}
