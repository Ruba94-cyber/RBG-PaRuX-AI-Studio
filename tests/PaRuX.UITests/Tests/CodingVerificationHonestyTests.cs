using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// The coding-task card must never say "Verified" for a completed task whose verification is
/// unverified, inconclusive or failed. The Debug-only UI fixture takes its VerificationStatus from
/// PARUX_UI_TEST_CODING_VERIFICATION (inherited by the launched app).
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class CodingVerificationHonestyTests
{
    [Theory, Trait("Category", "Regression"), Trait("Category", "CodingAgent"), Trait("Category", "FlaUI")]
    [InlineData("pass", "Completed and verified")]
    [InlineData("unverified", "Completed, not verified")]
    [InlineData("inconclusive", "Completed, verification not confirmed")]
    public void Completed_card_badge_reflects_only_what_core_verified(string verificationStatus, string expectedBadge)
    {
        string testName = nameof(Completed_card_badge_reflects_only_what_core_verified) + "-" + verificationStatus;
        string? previous = Environment.GetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION");
        Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", verificationStatus);
        try
        {
            using var app = new UiTestApplication(testName, enableCompletedCodingTaskCardFixture: true);
            try
            {
                var badge = UiWait.ById(app.MainWindow, "Chat_CodingTask_Status_Text", ControlType.Text);
                UiWait.For(() => badge.Properties.Name.ValueOrDefault == expectedBadge, "badge text '" + expectedBadge + "'");
                if (verificationStatus != "pass")
                    Assert.NotEqual("Completed and verified", badge.Properties.Name.ValueOrDefault);
                app.CaptureSnapshot("badge-" + verificationStatus);
            }
            catch (Exception ex)
            {
                app.CaptureFailure(testName, ex);
                throw;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PARUX_UI_TEST_CODING_VERIFICATION", previous);
        }
    }
}
