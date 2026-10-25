using System;
using System.IO;
using System.Net.Http;
using System.Security;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class UserFailureRecoveryMapperTests
    {
        [Fact]
        public void Map_OperationCanceledException_ReturnsClearCauseAndNextAction()
        {
            var ex = new OperationCanceledException("Operation was aborted by thread");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex, "Chat request");

            Assert.Contains("cancelled or timed out", guidance.Cause);
            Assert.Contains("Nothing was changed", guidance.DiskState);
            Assert.Contains("Retry", guidance.NextAction);
            Assert.NotNull(guidance.TechnicalDetails);

            string status = guidance.FormatStatusLine();
            Assert.DoesNotContain("aborted by thread", status);
            Assert.Contains("cancelled or timed out", status);
        }

        [Fact]
        public void Map_TimeoutException_ReturnsTimeoutGuidance()
        {
            var ex = new TimeoutException("HTTP client timeout after 30000ms");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("cancelled or timed out", guidance.Cause);
            Assert.Contains("Nothing was changed", guidance.DiskState);
            Assert.DoesNotContain("30000ms", guidance.FormatStatusLine());
        }

        [Fact]
        public void Map_UnauthorizedAccessException_ReturnsPermissionGuidance()
        {
            var ex = new UnauthorizedAccessException("Access to path C:\\Protected is denied.");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("permission", guidance.Cause, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No changes were written", guidance.DiskState);
            Assert.Contains("permissions", guidance.NextAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Protected", guidance.FormatStatusLine());
        }

        [Fact]
        public void Map_SecurityException_ReturnsPermissionGuidance()
        {
            var ex = new SecurityException("Security check failed.");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("permission", guidance.Cause, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No changes were written", guidance.DiskState);
        }

        [Fact]
        public void Map_FileNotFoundException_ReturnsNotFoundGuidance()
        {
            var ex = new FileNotFoundException("Could not find file model.gguf", "model.gguf");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("could not be found", guidance.Cause);
            Assert.Contains("Nothing was changed", guidance.DiskState);
            Assert.Contains("Verify", guidance.NextAction);
        }

        [Fact]
        public void Map_DirectoryNotFoundException_ReturnsNotFoundGuidance()
        {
            var ex = new DirectoryNotFoundException("Could not find part of path");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("could not be found", guidance.Cause);
            Assert.Contains("Nothing was changed", guidance.DiskState);
        }

        [Fact]
        public void Map_IOException_DiskFull_ReturnsStorageCapacityGuidance()
        {
            var ex = new IOException("There is not enough space on the disk.");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("Storage capacity error", guidance.Cause);
            Assert.Contains("Free disk space", guidance.NextAction);
        }

        [Fact]
        public void Map_IOException_FileLock_ReturnsLockedFileGuidance()
        {
            var ex = new IOException("The process cannot access the file because it is being used by another process.");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("disk or file system error", guidance.Cause);
            Assert.Contains("locked", guidance.NextAction);
        }

        [Fact]
        public void Map_HttpRequestException_ReturnsLocalServiceGuidance()
        {
            var ex = new HttpRequestException("No connection could be made because the target machine actively refused it.");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("Communication with the local AI service failed", guidance.Cause);
            Assert.Contains("No files were modified", guidance.DiskState);
            Assert.Contains("Ensure PaRuX service is running", guidance.NextAction);
            Assert.DoesNotContain("actively refused it", guidance.FormatStatusLine());
        }

        [Fact]
        public void Map_FallbackGenericException_ReturnsSafeIntactGuidance()
        {
            var ex = new InvalidOperationException("Internal null pointer in engine router line 42");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.Map(ex);

            Assert.Contains("unexpected error occurred", guidance.Cause);
            Assert.Contains("left intact", guidance.DiskState);
            Assert.Contains("Settings", guidance.NextAction);
            Assert.DoesNotContain("pointer", guidance.FormatStatusLine());
            Assert.DoesNotContain("line 42", guidance.FormatStatusLine());
            Assert.Contains("pointer", guidance.TechnicalDetails);
        }

        [Theory]
        [InlineData(404, "not found", "exists")]
        [InlineData(401, "rejected", "token")]
        [InlineData(403, "rejected", "token")]
        [InlineData(500, "internal error", "active")]
        [InlineData(503, "internal error", "active")]
        public void MapHttp_StandardStatusCodes_MapsProperly(int status, string causeWord, string actionWord)
        {
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapHttp(status, "Raw server stack trace");

            Assert.Contains(causeWord, guidance.Cause, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(actionWord, guidance.NextAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stack trace", guidance.FormatStatusLine());
            Assert.Equal("Raw server stack trace", guidance.TechnicalDetails);
        }

        [Fact]
        public void MapCodingTaskLaunch_NeverExposesRawException()
        {
            var ex = new Exception("SIGSEGV in agent child process");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingTaskLaunch(ex);

            Assert.Equal("Unable to start coding task.", guidance.Cause);
            Assert.Contains("unchanged", guidance.DiskState);
            Assert.Contains("Select a valid repository", guidance.NextAction);
            Assert.DoesNotContain("SIGSEGV", guidance.FormatStatusLine());
        }

        [Fact]
        public void MapCodingTaskCancel_IncludesTaskIdInSafeFormat()
        {
            var ex = new Exception("RPC failed with error 14");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingTaskCancel(ex, "task-42");

            Assert.Contains("task-42", guidance.Cause);
            Assert.Contains("unchanged", guidance.DiskState);
            Assert.Contains("restart the service", guidance.NextAction);
            Assert.DoesNotContain("RPC failed", guidance.FormatStatusLine());
        }

        [Fact]
        public void MapCodingTaskApply_ProvidesRepositoryIntactGuidance()
        {
            var ex = new Exception("Git merge-base returned non-zero code 128");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingTaskApply(ex);

            Assert.Equal("Could not apply reviewed changes.", guidance.Cause);
            Assert.Contains("not modified", guidance.DiskState);
            Assert.Contains("workspace clean status", guidance.NextAction);
            Assert.DoesNotContain("code 128", guidance.FormatStatusLine());
        }

        [Fact]
        public void MapCodingApplyResult_Conflict_ExplainsResolution()
        {
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapCodingApplyResult("Conflict detected in file Program.cs");

            Assert.Contains("conflict", guidance.Cause, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Stash or commit", guidance.NextAction);
            Assert.DoesNotContain("Program.cs", guidance.FormatStatusLine());
        }

        [Fact]
        public void MapFolderSelection_ReturnsClearSelectionGuidance()
        {
            var ex = new Exception("Access violation reading memory at 0x00000008");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapFolderSelection(ex);

            Assert.Equal("Couldn't select code folder.", guidance.Cause);
            Assert.Contains("Existing folder selection was kept", guidance.DiskState);
            Assert.Contains("permissions", guidance.NextAction);
            Assert.DoesNotContain("0x00000008", guidance.FormatStatusLine());
        }

        [Fact]
        public void MapModelSelector_ReturnsSafeSelectionGuidance()
        {
            var ex = new Exception("Failed to bind port 8080");
            UserFailureGuidance guidance = UserFailureRecoveryMapper.MapModelSelector(ex);

            Assert.Equal("Couldn't refresh model options.", guidance.Cause);
            Assert.Contains("Current model selection remains active", guidance.DiskState);
            Assert.Contains("Ensure local server is running", guidance.NextAction);
            Assert.DoesNotContain("port 8080", guidance.FormatStatusLine());
        }
    }
}
