using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>Inline chat media jobs: tracked start, restart re-attach, cancellation and retry.
    /// Decisions live in <see cref="ChatMediaJobReconciler"/>; this file only does I/O.</summary>
    public sealed partial class ChatSession
    {
        private readonly Dictionary<ChatMessage, CancellationTokenSource> _mediaAttached = new();
        private readonly object _mediaGate = new();
        private ChatMessage? _retryingMediaReply;

        private async Task<MediaAsset> RunTrackedMediaJobAsync(ChatMessage reply, string kind, bool chainVideoPending,
            MediaJobRequest request, IProgress<MediaJobStatus> progress, CancellationToken ct)
        {
            ChatMediaJobReconciler.BeginStep(reply, kind, chainVideoPending);
            Persist();
            var tracked = new Progress<MediaJobStatus>(job =>
            {
                ChatMediaJobReconciler.ReportProgress(reply, job);
                progress.Report(job);
            });
            return await MediaJobClient.Current.RunAsync(request, tracked, ct, id =>
            {
                ChatMediaJobReconciler.SetJobId(reply, id);
                Persist();
            }).ConfigureAwait(false);
        }

        private static ChatAttachment SaveMediaAsset(MediaAsset asset, bool video)
        {
            AppSettings settings = SettingsStore.Current.Data;
            byte[] bytes = !string.IsNullOrEmpty(asset.Base64Data)
                ? Convert.FromBase64String(asset.Base64Data)
                : !string.IsNullOrWhiteSpace(asset.FilePath) && File.Exists(asset.FilePath)
                    ? File.ReadAllBytes(asset.FilePath)
                    : throw new LocalAiException("The media job returned no readable file.");
            string folder = video
                ? (string.IsNullOrWhiteSpace(settings.GeneratedVideosFolder) ? AppPaths.GeneratedVideosDir : settings.GeneratedVideosFolder)
                : (string.IsNullOrWhiteSpace(settings.GeneratedImagesFolder) ? AppPaths.GeneratedImagesDir : settings.GeneratedImagesFolder);
            string extension = video ? (asset.MediaType == "video/webm" ? ".webm" : ".mp4")
                : (asset.MediaType == "image/jpeg" ? ".jpg" : ".png");
            if (StorageQuotaService.IsManagedPath(folder)) StorageQuotaService.EnsureCapacity(bytes.LongLength);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(path, bytes);
            return new ChatAttachment { FileName = asset.FileName, LocalPath = path, MediaType = asset.MediaType, SizeBytes = bytes.LongLength };
        }

        private static string DescribeAsset(MediaAsset a, bool video, bool edited) =>
            video ? "Generated locally · " + a.Width + "×" + a.Height + " · " + a.DurationSeconds.ToString("0.0") + "s · "
                + a.Fps + " fps · " + a.Steps + " steps · seed " + a.Seed
            : (edited ? "Edited locally · " : "Generated locally · ") + a.Width + "×" + a.Height + " · seed " + a.Seed;

        private void NotifyMediaChanged(ChatMessage message)
        {
            Action<ChatMessage>? upd = MessageUpdated;
            if (upd != null) upd(message);
            RaiseState();
        }

        /// <summary>Reconciles every persisted, still-"running" media reply of the open conversation with the server's
        /// job state. Safe to call repeatedly: a reply already being followed is skipped.</summary>
        public void ReconcileMediaJobs(ChatMessage? activeReply = null)
        {
            foreach (ChatMessage m in Conversation.Messages.Where(ChatMediaJobReconciler.NeedsReconcile).ToList())
            {
                if (ReferenceEquals(m, activeReply) || ReferenceEquals(m, _retryingMediaReply) || m.IsStreaming) continue;
                var cts = new CancellationTokenSource();
                lock (_mediaGate)
                {
                    if (_mediaAttached.ContainsKey(m)) { cts.Dispose(); continue; }
                    _mediaAttached[m] = cts;
                }
                _ = Task.Run(() => ReattachAsync(m, cts));
            }
        }

        public bool IsMediaReattached(ChatMessage m)
        {
            lock (_mediaGate) return _mediaAttached.ContainsKey(m);
        }

        /// <summary>Cancels the server job a re-attached reply is following.</summary>
        public void CancelMediaJob(ChatMessage m)
        {
            CancellationTokenSource? cts;
            lock (_mediaGate) _mediaAttached.TryGetValue(m, out cts);
            if (cts != null) { try { cts.Cancel(); } catch { } }
        }

        private async Task ReattachAsync(ChatMessage m, CancellationTokenSource cts)
        {
            try
            {
                // Bounded wait for the server (it may still be starting after an app restart).
                MediaJobStatus? job = null;
                bool reachable = false;
                for (int attempt = 0; attempt < 6 && !cts.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await PaRuXServerController.Current.EnsureRunningAsync(null, cts.Token).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(m.MediaJobId))
                            (job, reachable) = await MediaJobClient.Current.TryGetStatusAsync(m.MediaJobId!, cts.Token).ConfigureAwait(false);
                        else reachable = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log.Write("Media re-attach: server not ready: " + ex.Message); reachable = false; }
                    if (reachable) break;
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cts.Token).ConfigureAwait(false);
                }

                ChatMediaReconcileAction action = ChatMediaJobReconciler.Reconcile(m, job, reachable);
                if (action == ChatMediaReconcileAction.Defer)
                {
                    // Honest: the job state is unknown, so say so and offer Retry rather than spin forever.
                    ChatMediaJobReconciler.ApplyFailure(m, "The local server could not be reached to resume this media job.");
                }
                else if (action == ChatMediaReconcileAction.Reattach)
                {
                    NotifyMediaChanged(m);
                    var progress = new Progress<MediaJobStatus>(j =>
                    {
                        ChatMediaJobReconciler.ReportProgress(m, j);
                        NotifyMediaChanged(m);
                    });
                    MediaAsset asset = await MediaJobClient.Current.AttachAsync(m.MediaJobId!, progress, cts.Token).ConfigureAwait(false);
                    FinishAttachedAsset(m, asset);
                }
                else if (action == ChatMediaReconcileAction.AttachAsset)
                {
                    FinishAttachedAsset(m, job!.Asset!);
                }
            }
            catch (OperationCanceledException) { ChatMediaJobReconciler.ApplyCancelled(m); }
            catch (LocalAiException ex) { ChatMediaJobReconciler.ApplyFailure(m, ex.Message); }
            catch (Exception ex)
            {
                Log.Write("Media re-attach failed: " + ex);
                ChatMediaJobReconciler.ApplyFailure(m, ex.Message);
            }
            finally
            {
                lock (_mediaGate) _mediaAttached.Remove(m);
                try { cts.Dispose(); } catch { }
                NotifyMediaChanged(m);
                Persist();
            }
        }

        private void FinishAttachedAsset(ChatMessage m, MediaAsset asset)
        {
            bool video = asset.MediaType != null && asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            string jobId = m.MediaJobId ?? Guid.NewGuid().ToString("N");
            if (m.Attachments.Any(a => a.MediaJobId == jobId)) { ChatMediaJobReconciler.ApplyCompleted(m, null!, jobId, m.Content); return; }
            ChatAttachment saved = SaveMediaAsset(asset, video);
            ChatMediaJobReconciler.ApplyCompleted(m, saved, jobId,
                DescribeAsset(asset, video, m.MediaJobKind is "edit" or "img2img"));
        }

        /// <summary>Retries a failed/cancelled (or half-finished chained) media reply as a new job on the same message.</summary>
        public async Task RetryMediaAsync(ChatMessage reply)
        {
            if (IsBusy) return;
            ChatMediaRetryPlan? plan = ChatMediaRetryPlanner.Plan(Conversation.Messages, reply);
            if (plan == null) return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsBusy = true;
            reply.IsStreaming = false;
            _retryingMediaReply = reply;
            RaiseState();
            try
            {
                AppSettings settings = SettingsStore.Current.Data;
                await PaRuXServerController.Current.EnsureRunningAsync(new Progress<string?>(s => { Status = s; RaiseState(); }), cts.Token).ConfigureAwait(false);
                Status = null;
                string? sourceBase64 = null;

                if (plan.RunImage)
                {
                    MediaAsset image = await RunTrackedMediaJobAsync(reply, "image", plan.RunVideo,
                        new MediaJobRequest
                        {
                            Type = "image", Prompt = plan.Prompt, Seed = plan.Seed,
                            Width = settings.GeneratedImageWidth, Height = settings.GeneratedImageHeight
                        }, StatusProgress(reply), cts.Token).ConfigureAwait(false);
                    ChatMediaJobReconciler.ApplyCompleted(reply, SaveMediaAsset(image, false),
                        reply.MediaJobId ?? Guid.NewGuid().ToString("N"), DescribeAsset(image, false, false));
                    sourceBase64 = image.Base64Data;
                    Persist();
                }

                if (plan.RunVideo)
                {
                    if (string.IsNullOrEmpty(sourceBase64) && !string.IsNullOrWhiteSpace(plan.SourceImagePath) && File.Exists(plan.SourceImagePath))
                        sourceBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(plan.SourceImagePath, cts.Token).ConfigureAwait(false));
                    MediaAsset video = await RunTrackedMediaJobAsync(reply, "video", plan.RunImage || ChatMediaJobReconciler.HasFinishedImage(reply),
                        new MediaJobRequest
                        {
                            Type = "video", Prompt = plan.Prompt, SourceImageBase64 = sourceBase64 ?? string.Empty,
                            Width = settings.GeneratedVideoWidth, Height = settings.GeneratedVideoHeight,
                            DurationSeconds = settings.GeneratedVideoDurationSeconds,
                            Fps = settings.GeneratedVideoFps, Steps = settings.GeneratedVideoSteps, Seed = plan.Seed
                        }, StatusProgress(reply), cts.Token).ConfigureAwait(false);
                    ChatMediaJobReconciler.ApplyCompleted(reply, SaveMediaAsset(video, true),
                        reply.MediaJobId ?? Guid.NewGuid().ToString("N"), DescribeAsset(video, true, false));
                }

                if (plan.RunEdit)
                {
                    if (string.IsNullOrWhiteSpace(plan.SourceImagePath) || !File.Exists(plan.SourceImagePath))
                        throw new LocalAiException("The source image for this edit is no longer on disk.");
                    string src = Convert.ToBase64String(await File.ReadAllBytesAsync(plan.SourceImagePath, cts.Token).ConfigureAwait(false));
                    double? strength = plan.EditType == "img2img" ? TryParseStrength(plan.Prompt) : null;
                    MediaAsset edited = await RunTrackedMediaJobAsync(reply, plan.EditType, false,
                        new MediaJobRequest
                        {
                            Type = plan.EditType, Prompt = plan.Prompt, SourceImageBase64 = src, Seed = plan.Seed, Strength = strength
                        }, StatusProgress(reply), cts.Token).ConfigureAwait(false);
                    ChatMediaJobReconciler.ApplyCompleted(reply, SaveMediaAsset(edited, false),
                        reply.MediaJobId ?? Guid.NewGuid().ToString("N"), DescribeAsset(edited, false, true));
                }
            }
            catch (OperationCanceledException) { ChatMediaJobReconciler.ApplyCancelled(reply); }
            catch (LocalAiException ex) { ChatMediaJobReconciler.ApplyFailure(reply, ex.Message); }
            catch (Exception ex)
            {
                Log.Write("Chat media retry failed: " + ex);
                ChatMediaJobReconciler.ApplyFailure(reply, ex.Message);
            }
            finally
            {
                _retryingMediaReply = null;
                IsBusy = false;
                Status = null;
                _cts = null;
                try { cts.Dispose(); } catch { }
                NotifyMediaChanged(reply);
                Persist();
            }
        }

        private IProgress<MediaJobStatus> StatusProgress(ChatMessage reply) => new Progress<MediaJobStatus>(job =>
        {
            string eta = job.EtaSeconds.HasValue ? " · ETA ~" + job.EtaSeconds.Value + "s" : string.Empty;
            Status = job.Status == "completed" ? "Saving locally…" : job.Stage + " · " + job.ProgressPercent + "%" + eta;
            NotifyMediaChanged(reply);
        });
    }
}
