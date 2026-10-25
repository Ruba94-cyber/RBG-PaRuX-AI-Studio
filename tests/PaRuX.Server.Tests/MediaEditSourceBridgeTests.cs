using System;
using System.IO;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    [Collection("MediaEditSourceBridge singleton")]
    public sealed class MediaEditSourceBridgeTests
    {
        [Fact]
        public void SetEditSource_returns_false_with_message_when_file_is_missing()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            string missingPath = Path.Combine(Path.GetTempPath(), $"parux_nonexistent_{Guid.NewGuid():N}.png");
            Assert.False(File.Exists(missingPath));

            var result = bridge.SetEditSource(missingPath, "Sample prompt");

            Assert.False(result.Success);
            Assert.False((bool)result);
            Assert.Contains("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(bridge.HasPendingSource);
            Assert.Null(bridge.PendingEditSourcePath);
            Assert.Equal(result.Message, bridge.LastMessage);
        }

        [Fact]
        public void SetEditSource_returns_false_with_message_when_path_is_empty_or_whitespace()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            var emptyResult = bridge.SetEditSource("");
            Assert.False(emptyResult.Success);
            Assert.Contains("empty", emptyResult.Message, StringComparison.OrdinalIgnoreCase);

            var wsResult = bridge.SetEditSource("   ");
            Assert.False(wsResult.Success);
            Assert.Contains("empty", wsResult.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SetEditSource_returns_false_with_message_for_unsupported_extensions()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            string tempVideo = Path.Combine(Path.GetTempPath(), $"parux_video_{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(tempVideo, new byte[] { 1, 2, 3 });

            try
            {
                var result = bridge.SetEditSource(tempVideo, "A video prompt");
                Assert.False(result.Success);
                Assert.Contains("unsupported", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(bridge.HasPendingSource);
            }
            finally
            {
                if (File.Exists(tempVideo)) File.Delete(tempVideo);
            }
        }

        [Fact]
        public void SetEditSource_returns_true_with_message_when_file_exists_and_is_supported_image()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            string tempImage = Path.Combine(Path.GetTempPath(), $"parux_edit_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempImage, new byte[] { 137, 80, 78, 71 });

            try
            {
                var result = bridge.SetEditSource(tempImage, "Scenic mountain vista");
                Assert.True(result.Success);
                Assert.True((bool)result);
                Assert.Contains("Selected", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True(bridge.HasPendingSource);
                Assert.Equal(tempImage, bridge.PendingEditSourcePath);
                Assert.Equal("Scenic mountain vista", bridge.PendingPrompt);
                Assert.NotNull(bridge.TimestampUtc);
            }
            finally
            {
                if (File.Exists(tempImage)) File.Delete(tempImage);
            }
        }

        [Fact]
        public void SetEditSource_out_parameter_overload_reports_failure_and_message_when_file_missing()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            string missingPath = Path.Combine(Path.GetTempPath(), $"parux_missing_out_{Guid.NewGuid():N}.png");
            bool success = bridge.SetEditSource(missingPath, "prompt", out string message);

            Assert.False(success);
            Assert.NotEmpty(message);
            Assert.Contains("does not exist", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SetEditSource_out_parameter_overload_reports_success_and_message_when_file_exists()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            string tempImage = Path.Combine(Path.GetTempPath(), $"parux_valid_out_{Guid.NewGuid():N}.jpg");
            File.WriteAllBytes(tempImage, new byte[] { 255, 216, 255 });

            try
            {
                bool success = bridge.SetEditSource(tempImage, "Portrait prompt", out string message);

                Assert.True(success);
                Assert.NotEmpty(message);
                Assert.Contains("Selected", message, StringComparison.OrdinalIgnoreCase);
                Assert.True(bridge.HasPendingSource);
            }
            finally
            {
                if (File.Exists(tempImage)) File.Delete(tempImage);
            }
        }

        [Fact]
        public void SetEditSource_dispatches_EditSourceRequested_only_on_success_not_on_missing_file()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();

            bool eventFired = false;
            string? receivedPath = null;
            EventHandler<MediaEditSourceEventArgs> handler = (_, args) =>
            {
                eventFired = true;
                receivedPath = args.FilePath;
            };

            bridge.EditSourceRequested += handler;
            string tempImage = Path.Combine(Path.GetTempPath(), $"parux_event_{Guid.NewGuid():N}.webp");
            File.WriteAllBytes(tempImage, new byte[] { 82, 73, 70, 70 });

            try
            {
                // Missing file: event must not fire
                string missing = Path.Combine(Path.GetTempPath(), "parux_ghost.webp");
                var failResult = bridge.SetEditSource(missing, "ghost");
                Assert.False(failResult.Success);
                Assert.False(eventFired);

                // Existing file: event fires
                var successResult = bridge.SetEditSource(tempImage, "real image");
                Assert.True(successResult.Success);
                Assert.True(eventFired);
                Assert.Equal(tempImage, receivedPath);
            }
            finally
            {
                bridge.EditSourceRequested -= handler;
                if (File.Exists(tempImage)) File.Delete(tempImage);
            }
        }

        [Fact]
        public void Clear_resets_pending_edit_source_and_last_message()
        {
            var bridge = MediaEditSourceBridge.Current;
            string tempImage = Path.Combine(Path.GetTempPath(), $"parux_clear_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempImage, new byte[] { 1, 2 });

            try
            {
                bridge.SetEditSource(tempImage, "p");
                Assert.True(bridge.HasPendingSource);
                Assert.NotNull(bridge.PendingEditSourcePath);
                Assert.NotNull(bridge.LastMessage);

                bridge.Clear();
                Assert.False(bridge.HasPendingSource);
                Assert.Null(bridge.PendingEditSourcePath);
                Assert.Null(bridge.PendingPrompt);
                Assert.Null(bridge.TimestampUtc);
                Assert.Null(bridge.LastMessage);
            }
            finally
            {
                if (File.Exists(tempImage)) File.Delete(tempImage);
            }
        }

        [Fact]
        public void TryApplyToCreateView_handles_null_safely()
        {
            Assert.False(MediaEditSourceBridge.TryApplyToCreateView(null));
            Assert.False(MediaEditSourceBridge.TryApplyToCreateView(new object()));
        }
    }
}
