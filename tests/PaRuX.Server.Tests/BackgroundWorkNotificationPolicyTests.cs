using System;
using System.Collections.Generic;
using PaRuX.Contracts;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class BackgroundWorkNotificationPolicyTests
    {
        [Fact]
        public void Evaluate_NullOrMissingId_Suppressed()
        {
            var prefs = new BackgroundNotificationPreferences();
            var d1 = BackgroundWorkNotificationPolicy.Evaluate(null, true, prefs, null);
            Assert.False(d1.ShouldNotify);
            Assert.Equal("NullItem", d1.Reason);

            var itemEmpty = new BackgroundWorkItem(
                Id: "",
                Kind: BackgroundWorkKind.Download,
                Outcome: BackgroundWorkOutcome.Completed,
                Title: "Title",
                Message: "Msg",
                OpenTarget: BackgroundWorkOpenTarget.ModelHub);

            var d2 = BackgroundWorkNotificationPolicy.Evaluate(itemEmpty, true, prefs, null);
            Assert.False(d2.ShouldNotify);
            Assert.Equal("MissingJobId", d2.Reason);
        }

        [Fact]
        public void Evaluate_UserCancelled_Suppressed()
        {
            var prefs = new BackgroundNotificationPreferences();
            var item = new BackgroundWorkItem(
                Id: "cancel-1",
                Kind: BackgroundWorkKind.Download,
                Outcome: BackgroundWorkOutcome.Failed,
                Title: "Download cancelled",
                Message: "Cancelled",
                OpenTarget: BackgroundWorkOpenTarget.ModelHub,
                IsUserCancelled: true);

            var decision = BackgroundWorkNotificationPolicy.Evaluate(item, true, prefs, null);
            Assert.False(decision.ShouldNotify);
            Assert.Equal("UserCancelled", decision.Reason);
        }

        [Fact]
        public void Evaluate_AlreadyNotified_Suppressed()
        {
            var prefs = new BackgroundNotificationPreferences();
            var already = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "job-42" };

            var item = new BackgroundWorkItem(
                Id: "job-42",
                Kind: BackgroundWorkKind.Generation,
                Outcome: BackgroundWorkOutcome.Completed,
                Title: "Image ready",
                Message: "Done",
                OpenTarget: BackgroundWorkOpenTarget.Chat);

            var decision = BackgroundWorkNotificationPolicy.Evaluate(item, true, prefs, already);
            Assert.False(decision.ShouldNotify);
            Assert.Equal("AlreadyNotified", decision.Reason);
        }

        [Fact]
        public void Evaluate_WindowNotHidden_Suppressed()
        {
            var prefs = new BackgroundNotificationPreferences();
            var item = new BackgroundWorkItem(
                Id: "job-vis",
                Kind: BackgroundWorkKind.Install,
                Outcome: BackgroundWorkOutcome.Completed,
                Title: "Runtime install completed",
                Message: "Ready",
                OpenTarget: BackgroundWorkOpenTarget.RuntimeManager);

            // Window is visible (isWindowHidden = false)
            var decision = BackgroundWorkNotificationPolicy.Evaluate(item, isWindowHidden: false, prefs, null);
            Assert.False(decision.ShouldNotify);
            Assert.Equal("WindowNotHidden", decision.Reason);
        }

        [Theory]
        [InlineData(BackgroundWorkKind.Generation)]
        [InlineData(BackgroundWorkKind.Download)]
        [InlineData(BackgroundWorkKind.Install)]
        public void Evaluate_DisabledByType_Suppressed(BackgroundWorkKind kind)
        {
            var prefs = new BackgroundNotificationPreferences(
                NotifyOnGeneration: kind != BackgroundWorkKind.Generation,
                NotifyOnDownloads: kind != BackgroundWorkKind.Download,
                NotifyOnInstalls: kind != BackgroundWorkKind.Install);

            var item = new BackgroundWorkItem(
                Id: "disabled-test",
                Kind: kind,
                Outcome: BackgroundWorkOutcome.Completed,
                Title: "Done",
                Message: "Ready",
                OpenTarget: BackgroundWorkOpenTarget.Chat);

            var decision = BackgroundWorkNotificationPolicy.Evaluate(item, isWindowHidden: true, prefs, null);
            Assert.False(decision.ShouldNotify);
            Assert.Equal("DisabledByType:" + kind, decision.Reason);
        }

        [Fact]
        public void Evaluate_EligibleJobs_RaiseNotifications()
        {
            var prefs = new BackgroundNotificationPreferences();

            var gen = new BackgroundWorkItem(
                Id: "gen-1",
                Kind: BackgroundWorkKind.Generation,
                Outcome: BackgroundWorkOutcome.Completed,
                Title: "Image ready",
                Message: "Generation finished.",
                OpenTarget: BackgroundWorkOpenTarget.Chat);

            var d1 = BackgroundWorkNotificationPolicy.Evaluate(gen, isWindowHidden: true, prefs, null);
            Assert.True(d1.ShouldNotify);
            Assert.Equal("Eligible", d1.Reason);
            Assert.Equal(BackgroundWorkOpenTarget.Chat, d1.Item?.OpenTarget);

            var dl = new BackgroundWorkItem(
                Id: "dl-1",
                Kind: BackgroundWorkKind.Download,
                Outcome: BackgroundWorkOutcome.Failed,
                Title: "Download failed",
                Message: "Network error",
                OpenTarget: BackgroundWorkOpenTarget.ModelHub);

            var d2 = BackgroundWorkNotificationPolicy.Evaluate(dl, isWindowHidden: true, prefs, null);
            Assert.True(d2.ShouldNotify);
            Assert.Equal(BackgroundWorkOutcome.Failed, d2.Item?.Outcome);
            Assert.Equal(BackgroundWorkOpenTarget.ModelHub, d2.Item?.OpenTarget);
        }

        [Fact]
        public void FormatGeneration_SuccessAndFailure()
        {
            var success = BackgroundWorkNotificationPolicy.FormatGeneration(
                "Image", false, "A cat in space", "gen-ok", false);
            Assert.Equal("gen-ok", success.Id);
            Assert.Equal("Image ready", success.Title);
            Assert.Equal("A cat in space", success.Message);
            Assert.Equal(BackgroundWorkOutcome.Completed, success.Outcome);
            Assert.False(success.IsUserCancelled);
            Assert.Equal(BackgroundWorkOpenTarget.Chat, success.OpenTarget);

            var failed = BackgroundWorkNotificationPolicy.FormatGeneration(
                "Video", true, "Out of memory", "gen-err", false);
            Assert.Equal("gen-err", failed.Id);
            Assert.Equal("Video generation failed", failed.Title);
            Assert.Equal("Out of memory", failed.Message);
            Assert.Equal(BackgroundWorkOutcome.Failed, failed.Outcome);

            var stopped = BackgroundWorkNotificationPolicy.FormatGeneration(
                "Image edit", false, "_Stopped._", "gen-stop", true);
            Assert.True(stopped.IsUserCancelled);
        }

        [Fact]
        public void FormatDownload_States()
        {
            var completedStatus = new ModelDownloadStatus
            {
                Id = "dl-100",
                FileName = "qwen-7b.gguf",
                Status = "completed"
            };
            var completed = BackgroundWorkNotificationPolicy.FormatDownload(completedStatus);
            Assert.NotNull(completed);
            Assert.Equal("Download completed", completed.Title);
            Assert.Equal("qwen-7b.gguf is ready to use.", completed.Message);
            Assert.Equal(BackgroundWorkOutcome.Completed, completed.Outcome);
            Assert.Equal(BackgroundWorkOpenTarget.ModelHub, completed.OpenTarget);

            var failedStatus = new ModelDownloadStatus
            {
                Id = "dl-101",
                FileName = "mistral-7b.gguf",
                Status = "failed",
                Error = "Disk quota exceeded"
            };
            var failed = BackgroundWorkNotificationPolicy.FormatDownload(failedStatus);
            Assert.NotNull(failed);
            Assert.Equal("Download failed", failed.Title);
            Assert.Equal("mistral-7b.gguf: Disk quota exceeded", failed.Message);
            Assert.Equal(BackgroundWorkOutcome.Failed, failed.Outcome);

            var cancelledStatus = new ModelDownloadStatus
            {
                Id = "dl-102",
                FileName = "phi-3.gguf",
                Status = "cancelled"
            };
            var cancelled = BackgroundWorkNotificationPolicy.FormatDownload(cancelledStatus);
            Assert.NotNull(cancelled);
            Assert.True(cancelled.IsUserCancelled);

            var inFlightStatus = new ModelDownloadStatus
            {
                Id = "dl-103",
                FileName = "llama-3.gguf",
                Status = "downloading"
            };
            Assert.Null(BackgroundWorkNotificationPolicy.FormatDownload(inFlightStatus));
        }

        [Fact]
        public void FormatInstall_States()
        {
            var completedStatus = new RuntimeInstallStatus
            {
                Id = "inst-1",
                Backend = "llama-server",
                Tag = "b3500",
                Stage = "completed"
            };
            var completed = BackgroundWorkNotificationPolicy.FormatInstall(completedStatus);
            Assert.NotNull(completed);
            Assert.Equal("Runtime install completed", completed.Title);
            Assert.Equal("llama-server (b3500) is ready.", completed.Message);
            Assert.Equal(BackgroundWorkOutcome.Completed, completed.Outcome);
            Assert.Equal(BackgroundWorkOpenTarget.RuntimeManager, completed.OpenTarget);

            var failedStatus = new RuntimeInstallStatus
            {
                Id = "inst-2",
                Backend = "comfyui",
                Tag = "v0.3.1",
                Stage = "failed",
                Error = "Extraction hash mismatch"
            };
            var failed = BackgroundWorkNotificationPolicy.FormatInstall(failedStatus);
            Assert.NotNull(failed);
            Assert.Equal("Runtime install failed", failed.Title);
            Assert.Equal("comfyui (v0.3.1): Extraction hash mismatch", failed.Message);
            Assert.Equal(BackgroundWorkOutcome.Failed, failed.Outcome);

            var inProgressStatus = new RuntimeInstallStatus
            {
                Id = "inst-3",
                Backend = "llama-server",
                Tag = "b3600",
                Stage = "extracting"
            };
            Assert.Null(BackgroundWorkNotificationPolicy.FormatInstall(inProgressStatus));
        }

        [Fact]
        public void Tracker_InitialPoll_DoesNotSpamCompletedDownloadsOrInstalls()
        {
            var tracker = new BackgroundWorkTracker();
            var prefs = new BackgroundNotificationPreferences();

            var initialDownloads = new List<ModelDownloadStatus>
            {
                new() { Id = "old-dl-1", FileName = "old1.gguf", Status = "completed" },
                new() { Id = "old-dl-2", FileName = "old2.gguf", Status = "failed" },
                new() { Id = "active-dl-1", FileName = "active1.gguf", Status = "downloading" }
            };

            // First poll: must NOT notify for old-dl-1 or old-dl-2
            var decisions = tracker.EvaluateDownloads(initialDownloads, isWindowHidden: true, prefs);
            Assert.Empty(decisions);

            var initialInstalls = new List<RuntimeInstallStatus>
            {
                new() { Id = "old-inst-1", Backend = "llama", Tag = "v1", Stage = "completed" },
                new() { Id = "active-inst-1", Backend = "comfy", Tag = "v2", Stage = "downloading" }
            };
            var installDecisions = tracker.EvaluateInstalls(initialInstalls, isWindowHidden: true, prefs);
            Assert.Empty(installDecisions);
        }

        [Fact]
        public void Tracker_ActiveDownload_NotifiesOnce_WhenFinishedWhileWindowHidden()
        {
            var tracker = new BackgroundWorkTracker();
            var prefs = new BackgroundNotificationPreferences();

            // Startup poll records active-1
            tracker.EvaluateDownloads(new List<ModelDownloadStatus>
            {
                new() { Id = "active-1", FileName = "model.gguf", Status = "downloading" }
            }, isWindowHidden: false, prefs);

            // Active-1 finishes while window is hidden
            var poll2 = tracker.EvaluateDownloads(new List<ModelDownloadStatus>
            {
                new() { Id = "active-1", FileName = "model.gguf", Status = "completed" }
            }, isWindowHidden: true, prefs);

            Assert.Single(poll2);
            var dec = poll2[0];
            Assert.True(dec.ShouldNotify);
            Assert.Equal("Download completed", dec.Item?.Title);
            Assert.Equal("model.gguf is ready to use.", dec.Item?.Message);
            Assert.Equal(BackgroundWorkOpenTarget.ModelHub, dec.Item?.OpenTarget);

            // Repeated poll: must NOT notify again
            var poll3 = tracker.EvaluateDownloads(new List<ModelDownloadStatus>
            {
                new() { Id = "active-1", FileName = "model.gguf", Status = "completed" }
            }, isWindowHidden: true, prefs);
            Assert.Empty(poll3);
        }

        [Fact]
        public void Tracker_ActiveDownload_FinishesWhenWindowVisible_DoesNotNotifyAndMarksSeen()
        {
            var tracker = new BackgroundWorkTracker();
            var prefs = new BackgroundNotificationPreferences();

            tracker.EvaluateDownloads(new List<ModelDownloadStatus>
            {
                new() { Id = "vis-dl", FileName = "model.gguf", Status = "downloading" }
            }, isWindowHidden: false, prefs);

            // Finishes while window is NOT hidden (user is looking at it)
            var poll2 = tracker.EvaluateDownloads(new List<ModelDownloadStatus>
            {
                new() { Id = "vis-dl", FileName = "model.gguf", Status = "completed" }
            }, isWindowHidden: false, prefs);

            Assert.Empty(poll2);

            // User now hides the window later: still must NOT notify
            var poll3 = tracker.EvaluateDownloads(new List<ModelDownloadStatus>
            {
                new() { Id = "vis-dl", FileName = "model.gguf", Status = "completed" }
            }, isWindowHidden: true, prefs);

            Assert.Empty(poll3);
        }

        [Fact]
        public void Tracker_ActiveInstall_NotifiesFailure_WhenWindowHidden()
        {
            var tracker = new BackgroundWorkTracker();
            var prefs = new BackgroundNotificationPreferences();

            tracker.EvaluateInstalls(new List<RuntimeInstallStatus>
            {
                new() { Id = "inst-f", Backend = "vulkan-server", Tag = "b100", Stage = "extracting" }
            }, isWindowHidden: false, prefs);

            var poll = tracker.EvaluateInstalls(new List<RuntimeInstallStatus>
            {
                new() { Id = "inst-f", Backend = "vulkan-server", Tag = "b100", Stage = "failed", Error = "No disk space" }
            }, isWindowHidden: true, prefs);

            Assert.Single(poll);
            Assert.True(poll[0].ShouldNotify);
            Assert.Equal(BackgroundWorkOutcome.Failed, poll[0].Item?.Outcome);
            Assert.Equal("Runtime install failed", poll[0].Item?.Title);
            Assert.Contains("No disk space", poll[0].Item?.Message);
            Assert.Equal(BackgroundWorkOpenTarget.RuntimeManager, poll[0].Item?.OpenTarget);
        }

        [Fact]
        public void Tracker_Generation_TurnEvaluation()
        {
            var tracker = new BackgroundWorkTracker();
            var prefs = new BackgroundNotificationPreferences();

            var textMsg = new ChatMessage { Role = "assistant", Content = "Hello!" };
            var textDec = tracker.EvaluateGeneration(RouteKind.ChatNormal, textMsg, true, prefs);
            Assert.False(textDec.ShouldNotify);
            Assert.Equal("NotMediaRoute", textDec.Reason);

            var imgMsg = new ChatMessage
            {
                Role = "assistant",
                Content = "Rendered 1024x1024",
                TimestampUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            };
            var imgDec = tracker.EvaluateGeneration(RouteKind.Image, imgMsg, true, prefs);
            Assert.True(imgDec.ShouldNotify);
            Assert.Equal("Image ready", imgDec.Item?.Title);
            Assert.Equal(BackgroundWorkOpenTarget.Chat, imgDec.Item?.OpenTarget);

            // Second evaluate with same message: already notified
            var imgDec2 = tracker.EvaluateGeneration(RouteKind.Image, imgMsg, true, prefs);
            Assert.False(imgDec2.ShouldNotify);
            Assert.Equal("AlreadyNotified", imgDec2.Reason);

            // Stopped message suppresses notification
            var stoppedMsg = new ChatMessage
            {
                Role = "assistant",
                Content = "Cancelled. _Stopped._",
                TimestampUtc = new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc)
            };
            var stopDec = tracker.EvaluateGeneration(RouteKind.Video, stoppedMsg, true, prefs);
            Assert.False(stopDec.ShouldNotify);
            Assert.Equal("UserCancelled", stopDec.Reason);
        }
    }
}
