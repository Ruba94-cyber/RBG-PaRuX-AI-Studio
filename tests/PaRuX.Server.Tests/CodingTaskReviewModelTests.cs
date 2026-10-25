using System;
using System.Collections.Generic;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public class CodingTaskReviewModelTests
    {
        [Fact]
        public void Parse_EmptyOrNull_ReturnsEmptyModel()
        {
            var m1 = CodingTaskReviewModel.Parse(null);
            var m2 = CodingTaskReviewModel.Parse("");
            var m3 = CodingTaskReviewModel.Parse("   ");

            Assert.Equal(0, m1.TotalFiles);
            Assert.Equal(0, m2.TotalFiles);
            Assert.Equal(0, m3.TotalFiles);
            Assert.Equal("No changes found to review.", m1.FormatReviewStatus());
        }

        [Fact]
        public void Parse_DiffStatOutput_ExtractsFilesAndChangeKinds()
        {
            string diffStat =
                " src/PaRuX.App/Views/ChatView.cs | 14 ++++++++------\r\n"
                + " README.md                       |  2 +-\r\n"
                + " deleted.txt                     | 10 ----------\r\n"
                + " new_added.cs                    | 25 +++++++++++++++++++++++++\r\n"
                + " 4 files changed, 28 insertions(+), 23 deletions(-)";

            var model = CodingTaskReviewModel.Parse(diffStat);

            Assert.Equal(4, model.TotalFiles);
            Assert.True(model.AllAccepted);
            Assert.Equal(4, model.AcceptedCount);
            Assert.Equal(0, model.RejectedCount);

            Assert.Equal("src/PaRuX.App/Views/ChatView.cs", model.Files[0].RelativePath);
            Assert.Equal("Modified", model.Files[0].ChangeKind);

            Assert.Equal("README.md", model.Files[1].RelativePath);
            Assert.Equal("Modified", model.Files[1].ChangeKind);

            Assert.Equal("deleted.txt", model.Files[2].RelativePath);
            Assert.Equal("Deleted", model.Files[2].ChangeKind);

            Assert.Equal("new_added.cs", model.Files[3].RelativePath);
            Assert.Equal("Added", model.Files[3].ChangeKind);
        }

        [Fact]
        public void Parse_UntrackedFileList_ExtractsAddedFiles()
        {
            string untracked =
                "New untracked files to apply (2):\n"
                + "- created-by-agent.txt\n"
                + "- scripts/helper.py\n"
                + "Review their contents in the task worktree before confirming.";

            var model = CodingTaskReviewModel.Parse(untracked);

            Assert.Equal(2, model.TotalFiles);
            Assert.Equal("created-by-agent.txt", model.Files[0].RelativePath);
            Assert.Equal("Added", model.Files[0].ChangeKind);
            Assert.Equal("scripts/helper.py", model.Files[1].RelativePath);
            Assert.Equal("Added", model.Files[1].ChangeKind);
        }

        [Fact]
        public void Parse_PorcelainFormat_ExtractsStatus()
        {
            string porcelain =
                "M src/Core.cs\n"
                + "A tests/CoreTests.cs\n"
                + "D old_component.cs\n"
                + "?? untracked.py";

            var model = CodingTaskReviewModel.Parse(porcelain);

            Assert.Equal(4, model.TotalFiles);
            Assert.Equal("Modified", model.Files[0].ChangeKind);
            Assert.Equal("Added", model.Files[1].ChangeKind);
            Assert.Equal("Deleted", model.Files[2].ChangeKind);
            Assert.Equal("Added", model.Files[3].ChangeKind);
        }

        [Fact]
        public void PerFileAcceptReject_UpdatesCountsAndPathLists()
        {
            string input =
                " file1.cs | 5 +++++\n"
                + " file2.cs | 3 +--\n"
                + " file3.cs | 2 --";

            var model = CodingTaskReviewModel.Parse(input);
            Assert.Equal(3, model.TotalFiles);
            Assert.True(model.AllAccepted);

            // Reject file2
            bool ok = model.SetFileAccepted("file2.cs", false);
            Assert.True(ok);
            Assert.False(model.AllAccepted);
            Assert.Equal(2, model.AcceptedCount);
            Assert.Equal(1, model.RejectedCount);
            Assert.True(model.HasAcceptedFiles);

            IReadOnlyList<string> accepted = model.GetAcceptedPaths();
            IReadOnlyList<string> rejected = model.GetRejectedPaths();
            Assert.Equal(new[] { "file1.cs", "file3.cs" }, accepted);
            Assert.Equal(new[] { "file2.cs" }, rejected);

            // Toggle file2 back to accepted
            model.ToggleFile("file2.cs");
            Assert.True(model.AllAccepted);
            Assert.Equal(3, model.AcceptedCount);

            // Reject all
            model.RejectAll();
            Assert.True(model.NoneAccepted);
            Assert.False(model.HasAcceptedFiles);
            Assert.Equal(0, model.AcceptedCount);
            Assert.Equal(3, model.RejectedCount);

            // Accept all
            model.AcceptAll();
            Assert.True(model.AllAccepted);
            Assert.Equal(3, model.AcceptedCount);
        }

        [Fact]
        public void FormatConfirmationSummary_IncludesNoCommitOrPushAndListsChanges()
        {
            string input =
                " fileA.cs | 5 +++++\n"
                + " fileB.cs | 3 +--";

            var model = CodingTaskReviewModel.Parse(input);
            model.SetFileAccepted("fileB.cs", false);

            string summary = model.FormatConfirmationSummary("MyProject", multiMode: false, isShadow: false);

            Assert.Contains("No git commit or push will be executed", summary);
            Assert.Contains("PaRuX will apply 1 accepted file(s) to repository 'MyProject'", summary);
            Assert.Contains("1 rejected file(s) skipped", summary);
            Assert.Contains("+ fileA.cs", summary);
            Assert.Contains("- fileB.cs", summary);
        }

        [Fact]
        public void AutomationIdSafePath_SanitizesSpecialCharacters()
        {
            var item = new CodingReviewFileItem
            {
                RelativePath = @"src\PaRuX.App/Views:file.name.cs"
            };

            Assert.Equal("src_PaRuX_App_Views_file_name_cs", item.AutomationIdSafePath);
        }
    }
}
