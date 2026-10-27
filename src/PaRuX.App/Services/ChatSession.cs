using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;
using PaRuX.Models;

namespace PaRuX.Services
{
    /// <summary>
    /// The one live conversation, shared by the main window and the bubble so both
    /// show the same thread and the same in-flight stream.
    /// </summary>
    public sealed partial class ChatSession
    {
        public static ChatSession Current { get; } = new ChatSession();

        private CancellationTokenSource? _cts;

        public Conversation Conversation { get; private set; } = new Conversation();

        public bool IsBusy { get; private set; }

        /// <summary>Transient status such as "Waking up PaRuX…"; null when idle.</summary>
        public string? Status { get; private set; }

        /// <summary>The route chosen for the most recent turn, for the composer label.</summary>
        public RouteKind LastRoute { get; private set; } = RouteKind.ChatNormal;

        /// <summary>
        /// Non-null/non-empty forces every subsequent chat-route turn to that server skill id
        /// instead of AUTO routing. Sticky - set by the composer's skill selector and stays
        /// forced until the user picks Auto again. A slash command overrides this for one
        /// message only via SendAsync's oneShotSkillId parameter, without changing this field.
        /// </summary>
        public string? ForcedSkillId { get; set; }

        /// <summary>A different conversation was loaded; views should rebuild from scratch.</summary>
        public event Action? ConversationChanged;

        /// <summary>The current conversation was committed to local history.</summary>
        public event Action? ConversationPersisted;

        public event Action<ChatMessage>? MessageAdded;

        /// <summary>Fires per streamed fragment. Views are expected to throttle.</summary>
        public event Action<ChatMessage>? MessageUpdated;

        public event Action? StateChanged;

        /// <summary>Raised when the bubble asks to surface the thread in the main window.</summary>
        public event Action? OpenInMainRequested;

        private ChatSession() { }

        public void RequestOpenInMain()
        {
            Action? h = OpenInMainRequested;
            if (h != null) h();
        }

        private void RaiseState()
        {
            Action? h = StateChanged;
            if (h != null) h();
        }

        // ------------------------------------------------------------------
        // Conversation lifecycle
        // ------------------------------------------------------------------

        public void NewConversation()
        {
            Cancel();
            Persist();
            Conversation = new Conversation();
            Action? h = ConversationChanged;
            if (h != null) h();
        }

        public void NewIncognitoConversation()
        {
            Cancel();
            Persist();
            Conversation = new Conversation { Title = "Incognito chat", IsIncognito = true };
            Action? h = ConversationChanged;
            if (h != null) h();
        }

        public bool OpenConversation(string id)
        {
            if (Conversation != null && Conversation.Id == id) return true;
            Conversation? loaded = HistoryStore.Load(id);
            if (loaded == null) return false;

            Cancel();
            Persist();
            Conversation = loaded;
            Action? h = ConversationChanged;
            if (h != null) h();
            return true;
        }

        /// <summary>Creates a NEW conversation containing messages up to and including <paramref name="message"/>,
        /// saves it and opens it. The source conversation is untouched.</summary>
        public bool BranchFromMessage(ChatMessage message)
        {
            Conversation? branch = ConversationBranching.BranchAt(Conversation, message);
            if (branch == null) return false;
            return OpenNewConversation(branch);
        }

        /// <summary>Duplicates a stored conversation (default: the current one) into a new "Copy of …" conversation and opens it.</summary>
        public bool DuplicateConversation(string? id = null)
        {
            Conversation? source = id == null || id == Conversation.Id ? Conversation : HistoryStore.Load(id);
            if (source == null || source.Messages.Count == 0) return false;
            return OpenNewConversation(ConversationBranching.Duplicate(source));
        }

        private bool OpenNewConversation(Conversation created)
        {
            Cancel();
            Persist(); // keep the source's pending changes (also persists lazily-assigned fork message ids)
            if (!created.IsIncognito && !HistoryStore.Save(created)) return false;
            Conversation = created;
            ConversationPersisted?.Invoke();
            Action? h = ConversationChanged;
            if (h != null) h();
            return true;
        }

        public void Persist()
        {
            Conversation c = Conversation;
            if (c == null) return;
            if (c.IsIncognito) return;
            if (c.Messages.Count == 0) return;
            c.Title = c.Title == "New chat" || string.IsNullOrWhiteSpace(c.Title)
                ? HistoryStore.DeriveTitle(c)
                : c.Title;
            if (HistoryStore.Save(c)) ConversationPersisted?.Invoke();
        }

        public void AppendAssistantNotice(string content)
        {
            var notice = new ChatMessage { Role = "assistant", Content = content };
            Conversation.Messages.Add(notice);
            Action<ChatMessage>? added = MessageAdded;
            if (added != null) added(notice);
            Persist();
        }

        public ChatMessage AppendLiveAssistantMessage()
        {
            var message = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
            Conversation.Messages.Add(message);
            Action<ChatMessage>? added = MessageAdded;
            if (added != null) added(message);
            return message;
        }

        public void NotifyMessageUpdated(ChatMessage message)
        {
            Action<ChatMessage>? upd = MessageUpdated;
            if (upd != null) upd(message);
        }

        public void Cancel()
        {
            CancellationTokenSource? cts = _cts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        // ------------------------------------------------------------------
        // Turns
        // ------------------------------------------------------------------

        public Task SendAsync(string text)
        {
            return SendAsync(text, null);
        }

        public Task SendAsync(string text, IReadOnlyList<ChatAttachment>? attachments)
        {
            return SendAsync(text, attachments, null);
        }

        private ChatAttachment? LastGeneratedImage()
        {
            return RouterService.FindLastGeneratedImage(Conversation.Messages);
        }

        private static readonly Regex StrengthRx = new(
            @"\b(?:strength|denoise|forza)\s*[:=]?\s*(\d{1,3}(?:\.\d+)?)\s*(%)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VariationRx = new(
            @"\b(variazione|variante|reinterpreta|re-?style|variation|variant)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const double DefaultImg2ImgStrength = 0.6;

        internal static double? TryParseStrength(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            Match match = StrengthRx.Match(text);
            if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                if (match.Groups[2].Success || value > 1.0) value /= 100.0;
                return Math.Clamp(value, 0.05, 1.0);
            }
            return VariationRx.IsMatch(text) ? DefaultImg2ImgStrength : null;
        }

        private static bool hasAudioOnly(IReadOnlyList<ChatAttachment>? attachments) =>
            attachments != null && attachments.Count > 0
            && attachments.All(a => ChatAudioAttachmentPolicy.IsAudioMediaType(a.MediaType));

        /// <summary>
        /// Set by the chat view: shows the Computer Use consent dialog and returns true only on an explicit confirm.
        /// With no handler (no UI), consent is treated as refused.
        /// </summary>
        public Func<string, Task<bool>>? ComputerUseConsentPrompt { get; set; }

        private async Task<bool> AskComputerUseConsentAsync(string? task)
        {
            Func<string, Task<bool>>? prompt = ComputerUseConsentPrompt;
            if (prompt == null) return false;
            try { return await prompt(task ?? string.Empty).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write("Computer Use consent prompt failed: " + ex.Message); return false; }
        }

        public Task SendAsync(string text, IReadOnlyList<ChatAttachment>? attachments, string? oneShotSkillId)
        {
            if (IsBusy) return Task.CompletedTask;
            bool hasAttachments = attachments != null && attachments.Count > 0;
            if (string.IsNullOrWhiteSpace(text) && !hasAttachments) return Task.CompletedTask;

            RouteKind route = hasAttachments ? RouterService.ClassifyWithAttachment(text) : RouterService.Classify(text);
            if (RouterService.UsesPriorGeneratedImage(route) && !hasAttachments
                && RouterService.ShouldAttachPriorGeneratedImage(route, text))
            {
                ChatAttachment? lastImage = LastGeneratedImage();
                if (lastImage != null)
                {
                    attachments = new List<ChatAttachment> { lastImage };
                    hasAttachments = true;
                    route = RouterService.ClassifyWithAttachment(text);
                }
            }
            bool audioOnly = hasAudioOnly(attachments);
            // Audio is not an image: it goes to the local speech engine via Core, so the Vision route does not apply.
            if (audioOnly && route is RouteKind.VisionDelegated) route = RouteKind.ChatNormal;
            LastRoute = route;
            // A message that asks for an image or video will take the GPU in a moment: let Core start ComfyUI now (a hint; Core may refuse).
            if (route is RouteKind.Image or RouteKind.ImageEdit or RouteKind.Video) MediaPrewarmClient.Request("chat-media-intent");

            var userMessage = new ChatMessage
            {
                Role = "user",
                Content = string.IsNullOrWhiteSpace(text) ? (audioOnly ? "Transcribe this audio." : "Describe this image.") : text.Trim(),
                Route = RouterService.Label(route)
            };
            if (hasAttachments) userMessage.Attachments.AddRange(attachments!);
            Conversation.Messages.Add(userMessage);
            Action<ChatMessage>? added = MessageAdded;
            if (added != null) added(userMessage);

            bool eligibleForForcedModelRoute = route is (RouteKind.ChatFast or RouteKind.ChatNormal or RouteKind.DeepReasoning)
                && !Conversation.Messages.Any(message => message.Attachments != null && message.Attachments.Count > 0);
            RouteRequest? forcedModelRoute = eligibleForForcedModelRoute
                ? ModelRoutingClient.Current.TakeForcedNextChatRoute()
                : null;
            return RunTurnAsync(route, oneShotSkillId, forcedModelRoute);
        }

        public Task RegenerateAsync(ChatMessage? targetAssistant = null)
        {
            if (IsBusy) return Task.CompletedTask;

            List<ChatMessage> msgs = Conversation.Messages;
            if (msgs.Count == 0) return Task.CompletedTask;

            int lastAssistant = targetAssistant == null ? -1 : msgs.IndexOf(targetAssistant);
            if (lastAssistant >= 0 && msgs[lastAssistant].Role != "assistant") lastAssistant = -1;
            for (int i = msgs.Count - 1; lastAssistant < 0 && i >= 0; i--)
            {
                if (msgs[i].Role == "assistant") { lastAssistant = i; break; }
            }
            if (lastAssistant < 0) return Task.CompletedTask;

            msgs.RemoveRange(lastAssistant, msgs.Count - lastAssistant);

            ChatMessage? lastUser = msgs.LastOrDefault(m => m.Role == "user");
            RouteKind route = lastUser != null
                ? (lastUser.Attachments != null && lastUser.Attachments.Count > 0
                    ? RouterService.ClassifyWithAttachment(lastUser.Content)
                    : RouterService.Classify(lastUser.Content))
                : RouteKind.ChatNormal;
            LastRoute = route;

            Action? changed = ConversationChanged;
            if (changed != null) changed();

            return RunTurnAsync(route, oneShotSkillId: null, forcedModelRoute: null);
        }

        /// <summary>
        /// Loads a vision-capable local profile and waits until it is actually serving. Required by
        /// every route that sends an image to the model - the Vision route, and Computer Use, whose
        /// agent loop sends a screenshot on every single step.
        ///
        /// Computer Use previously skipped this. Verified live: with the small Qwen3.5-4B fallback
        /// loaded (hasVisionSupport=false), the agent sent screenshots to a model that could not see
        /// them and failed at step 1 with "the model's response wasn't a recognizable action" - the
        /// model was being asked to pick a screen coordinate from an image it never received.
        /// </summary>
        private async Task EnsureVisionProfileReadyAsync(CancellationToken ct)
        {
            Status = "Loading the local vision profile…";
            RaiseState();
            string profile = SettingsStore.Current.Data.DefaultProfile;
            if (profile == "AUTO" && File.Exists(AppPaths.TextProfileFile))
                profile = File.ReadAllText(AppPaths.TextProfileFile).Trim().ToUpperInvariant();
            string action = profile == "UNCENSORED" ? "Uncensored" : "Standard";
            Task<LocalAiController.ActionResult> switchTask = LocalAiController.Current
                .RunActionAsync(action, ct);
            DateTime visionDeadline = DateTime.UtcNow.AddMinutes(5);
            bool visionReady = false;
            while (DateTime.UtcNow < visionDeadline)
            {
                ct.ThrowIfCancellationRequested();
                string active = File.Exists(AppPaths.ActiveProfileFile)
                    ? File.ReadAllText(AppPaths.ActiveProfileFile) : string.Empty;
                if (!active.Contains("media assistant", StringComparison.OrdinalIgnoreCase)
                    && await LocalAiController.Current.IsHealthyAsync(ct).ConfigureAwait(false))
                {
                    visionReady = true;
                    break;
                }
                if (switchTask.IsCompleted)
                {
                    LocalAiController.ActionResult switched = await switchTask.ConfigureAwait(false);
                    if (!switched.Success)
                        throw new LocalAiException(string.IsNullOrWhiteSpace(switched.Message)
                            ? "The local vision profile could not be loaded." : switched.Message.Trim());
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            if (!visionReady) throw new LocalAiException("The local vision profile timed out while loading.");

            await EnsureLoadedModelCanSeeImagesAsync(ct).ConfigureAwait(false);
            Status = null;
            RaiseState();
        }

        /// <summary>
        /// Fails honestly when the model that is actually serving cannot accept an image.
        ///
        /// The profile being "ready" is not the same as it being vision-capable: on a machine with
        /// no heavy GPU the runtime deliberately loads the small text-only fallback instead of the
        /// configured 27B+projector profile. Before 2026-09-02 nothing could see that - the roles
        /// endpoint reported the CONFIGURED model, so a capability check read "vision available"
        /// while a text-only model was serving, and the request failed further downstream with a
        /// misleading error ("the model's response wasn't a recognizable action" for Computer Use,
        /// which is a parse failure, not the real cause). The runtime now reports the projector it
        /// really attached, so the precondition can be enforced where it belongs.
        ///
        /// A status call that itself fails is deliberately non-fatal: inability to observe the
        /// runtime is not evidence that it lacks vision, and turning it into a hard stop would
        /// block a working machine over a transient local API error.
        /// </summary>
        private static async Task EnsureLoadedModelCanSeeImagesAsync(CancellationToken ct)
        {
            PaRuX.Contracts.RuntimeManagerStatus status;
            try { status = await ModelRolesClient.Current.GetRuntimeStatusAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return; }

            if (status == null || status.ActiveHasVisionSupport) return;
            throw new LocalAiException(
                "The model currently loaded ("
                + (string.IsNullOrWhiteSpace(status.ActiveProfile) ? "unknown profile" : status.ActiveProfile)
                + ") is text-only and cannot read images"
                + (status.GpuDetected
                    ? "."
                    : ", because no GPU with at least 12GB VRAM was detected and PaRuX fell back to the small local model.")
                + " Connect a supported GPU, or assign a vision-capable model to this role, and try again.");
        }

        /// <summary>
        /// LM Studio-style "edit and continue": <paramref name="editedContent"/> replaces
        /// <paramref name="targetAssistant"/>'s text, and the model resumes generating from exactly
        /// that edited prefix instead of PaRuX discarding it and re-rolling the whole reply. Lets a
        /// user nudge one wrong line of an otherwise good answer and steer the rest, rather than
        /// losing everything that was already right.
        ///
        /// Anything the user sent after this reply is dropped, same as RegenerateAsync: continuing
        /// an edited mid-thread reply while later turns still reference the old text would leave the
        /// conversation internally inconsistent.
        /// </summary>
        public Task ContinueAsync(ChatMessage targetAssistant, string editedContent)
        {
            if (IsBusy || targetAssistant == null || targetAssistant.Role != "assistant")
                return Task.CompletedTask;

            List<ChatMessage> msgs = Conversation.Messages;
            int index = msgs.IndexOf(targetAssistant);
            if (index < 0) return Task.CompletedTask;

            targetAssistant.Content = editedContent ?? string.Empty;
            if (targetAssistant.Content.Length == 0) return Task.CompletedTask;
            targetAssistant.IsError = false;

            if (index < msgs.Count - 1) msgs.RemoveRange(index + 1, msgs.Count - index - 1);

            ChatMessage? lastUser = msgs.Take(index + 1).LastOrDefault(m => m.Role == "user");
            RouteKind route = lastUser != null
                ? (lastUser.Attachments != null && lastUser.Attachments.Count > 0
                    ? RouterService.ClassifyWithAttachment(lastUser.Content)
                    : RouterService.Classify(lastUser.Content))
                : RouteKind.ChatNormal;
            LastRoute = route;

            Action? changed = ConversationChanged;
            if (changed != null) changed();

            return RunContinueTurnAsync(targetAssistant, index, route);
        }

        private async Task RunContinueTurnAsync(ChatMessage targetAssistant, int index, RouteKind route)
        {
            targetAssistant.IsStreaming = true;
            Action<ChatMessage>? upd0 = MessageUpdated;
            if (upd0 != null) upd0(targetAssistant);

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsBusy = true;
            RaiseState();

            try
            {
                var progress = new Progress<string?>(s => { Status = s; RaiseState(); });
                await PaRuXServerController.Current.EnsureRunningAsync(progress, cts.Token).ConfigureAwait(false);
                Status = null;
                RaiseState();

                List<ChatMessage> historyThroughTarget = Conversation.Messages.Take(index + 1).ToList();
                var buffer = new System.Text.StringBuilder(targetAssistant.Content);
                await ChatClient.Current.ContinueAsync(
                    historyThroughTarget,
                    RouterService.SystemPrompt(route) ?? string.Empty,
                    fragment =>
                    {
                        buffer.Append(fragment);
                        targetAssistant.Content = buffer.ToString();
                        Action<ChatMessage>? upd = MessageUpdated;
                        if (upd != null) upd(targetAssistant);
                    },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                targetAssistant.Content += "\n\n_Stopped._";
            }
            catch (LocalAiException ex)
            {
                targetAssistant.Content += "\n\n_Couldn't continue: " + ex.Message + "_";
                targetAssistant.IsError = true;
            }
            catch (Exception ex)
            {
                Log.Write("Continue turn failed: " + ex);
                targetAssistant.Content += "\n\n_Couldn't continue: " + ex.Message + "_";
                targetAssistant.IsError = true;
            }
            finally
            {
                targetAssistant.IsStreaming = false;
                IsBusy = false;
                Status = null;
                _cts = null;
                try { cts.Dispose(); } catch { }

                Action<ChatMessage>? upd = MessageUpdated;
                if (upd != null) upd(targetAssistant);
                RaiseState();
                Persist();
            }
        }

        /// <summary>
        /// Ask-mode Computer Use approval. The action card is an authorization request, not a
        /// suggestion: clicking it grants exactly this launch/task once. The persistent Full
        /// Computer Use setting is the explicit "pre-authorize all" mode and bypasses this card.
        /// </summary>
        /// <param name="plan">Null for a multi-step Computer Use goal; non-null only for the
        /// simple "open this app" case, which is the distinction the message body turns on.</param>
        private void AddComputerUseApproval(ComputerActionPlan? plan, string? goal)
        {
            string? launchTarget = plan?.Name;
            bool simpleLaunch = launchTarget != null;
            var approval = new ChatMessage
            {
                Role = "assistant",
                Content = simpleLaunch
                    ? "PaRuX wants permission to open **" + launchTarget + "** on this PC. Nothing will run until you approve this request. "
                      + "Select **Allow once** below, or enable **Full Computer Use** in Settings to pre-authorize future Computer Use requests."
                    : "PaRuX wants permission to control this PC's screen, mouse, and keyboard for this task. "
                      + "Approval applies only to this goal, is capped at " + ComputerUseAgentService.MaxSteps + " steps, and can be stopped at any time. "
                      + "Select **Allow once** below, or enable **Full Computer Use** in Settings to pre-authorize future Computer Use requests.",
                ActionKind = simpleLaunch ? "computer-launch" : "computer-agent",
                ActionLabel = simpleLaunch ? "Allow once · Open " + launchTarget : "Allow once · Start Computer Use",
                ActionPayload = plan != null ? ComputerUseService.SerializePlan(plan) : goal
            };
            Conversation.Messages.Add(approval);
            MessageAdded?.Invoke(approval);
            Persist();
        }

        private async Task RunPreauthorizedComputerLaunchAsync(ComputerActionPlan plan)
        {
            var reply = new ChatMessage
            {
                Role = "assistant",
                Content = "Opening **" + plan.Name + "**…",
                IsStreaming = true
            };
            Conversation.Messages.Add(reply);
            MessageAdded?.Invoke(reply);

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsBusy = true;
            Status = "Computer Use · opening " + plan.Name + "…";
            RaiseState();

            try
            {
                ComputerActionResult result = await ComputerUseService.ExecuteAsync(
                    ComputerUseService.SerializePlan(plan)).ConfigureAwait(false);
                reply.Content = result.Message;
                reply.IsError = !result.Success;
            }
            catch (Exception ex)
            {
                Log.Write("Pre-authorized Computer Use launch failed: " + ex);
                reply.Content = "Couldn't open " + plan.Name + ": " + ex.Message;
                reply.IsError = true;
            }
            finally
            {
                reply.IsStreaming = false;
                IsBusy = false;
                Status = null;
                _cts = null;
                try { cts.Dispose(); } catch { }
                MessageUpdated?.Invoke(reply);
                RaiseState();
                Persist();
            }
        }

        private async Task RunPreauthorizedComputerAgentAsync(string? goal, bool taskConsent = false)
        {
            var reply = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true
            };
            Conversation.Messages.Add(reply);
            MessageAdded?.Invoke(reply);

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsBusy = true;
            Status = "Computer Use · controlling the PC…";
            RaiseState();
            Guid cancellationRegistration = LocalAgentCancellationRegistry.Register(cts);
            var log = new System.Text.StringBuilder();

            try
            {
                await EnsureVisionProfileReadyAsync(cts.Token).ConfigureAwait(false);
                Status = "Computer Use · controlling the PC…";
                RaiseState();

                ComputerUseAgentResult result = await ComputerUseAgentService.RunAsync(
                    string.IsNullOrWhiteSpace(goal) ? "Carry out the requested action on this PC." : goal,
                    line =>
                    {
                        if (log.Length > 0) log.AppendLine();
                        log.Append(line);
                        reply.Content = log.ToString();
                        MessageUpdated?.Invoke(reply);
                    },
                    cts.Token,
                    // Full Computer Use is this run's authorization, so turning it off mid-run stops the run.
                    persistentAuthorizationStillValid: taskConsent ? null : () => SettingsStore.Current.Data.ComputerUseFullAutomationEnabled).ConfigureAwait(false);

                if (result.Cancelled || cts.IsCancellationRequested)
                {
                    if (log.Length > 0) log.AppendLine().AppendLine();
                    log.Append("_Computer Use stopped._");
                    reply.Content = log.ToString();
                }
                else
                {
                    if (log.Length > 0) log.AppendLine().AppendLine();
                    log.Append(result.Summary);
                    reply.Content = log.ToString();
                    reply.IsError = !result.GoalReported;
                }
            }
            catch (OperationCanceledException)
            {
                if (log.Length > 0) log.AppendLine().AppendLine();
                log.Append("_Computer Use stopped._");
                reply.Content = log.ToString();
            }
            catch (Exception ex)
            {
                Log.Write("Pre-authorized Computer Use agent failed: " + ex);
                if (log.Length > 0) log.AppendLine().AppendLine();
                log.Append("Computer Use failed: ").Append(ex.Message);
                reply.Content = log.ToString();
                reply.IsError = true;
            }
            finally
            {
                LocalAgentCancellationRegistry.Unregister(cancellationRegistration);
                reply.IsStreaming = false;
                IsBusy = false;
                Status = null;
                _cts = null;
                try { cts.Dispose(); } catch { }
                MessageUpdated?.Invoke(reply);
                RaiseState();
                Persist();
            }
        }

        /// <summary>
        /// Runs the evidence-driven Binary Analysis agent directly from Chat. An attached local
        /// artifact takes precedence and scopes the read-only workspace to its containing folder.
        /// Without an attachment, direct analysis may use only the repository explicitly selected
        /// for the active Project or the dedicated Binary Analysis workspace. It never borrows the
        /// Coding Agent's global/legacy repository authority.
        /// </summary>
        private async Task RunDirectBinaryAnalysisAsync(string? goal)
        {
            var reply = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true
            };
            Conversation.Messages.Add(reply);
            MessageAdded?.Invoke(reply);

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsBusy = true;
            Status = "Binary Analysis · collecting local evidence…";
            RaiseState();
            Guid cancellationRegistration = LocalAgentCancellationRegistry.Register(cts);
            var log = new System.Text.StringBuilder();

            try
            {
                ChatMessage? request = Conversation.Messages.LastOrDefault(message => message.Role == "user");
                ChatAttachment? attachedArtifact = request?.Attachments?.FirstOrDefault(attachment =>
                    !string.IsNullOrWhiteSpace(attachment.LocalPath) && File.Exists(attachment.LocalPath));

                string? workspace;
                string effectiveGoal = string.IsNullOrWhiteSpace(goal)
                    ? "Analyze the selected binary artifact and report evidence-backed findings."
                    : goal.Trim();

                if (attachedArtifact != null)
                {
                    workspace = Path.GetDirectoryName(Path.GetFullPath(attachedArtifact.LocalPath));
                    string artifactName = Path.GetFileName(attachedArtifact.LocalPath);
                    effectiveGoal += "\n\nPrimary attached artifact: " + artifactName
                        + ". Start by analyzing this file before exploring related files.";
                }
                else
                {
                    string projectRepository = ProjectWorkspaceSession.Current.RepositoryRoot?.Trim() ?? string.Empty;
                    string dedicatedBinary = SettingsStore.Current.Data.BinaryAnalysisWorkspaceRoot?.Trim() ?? string.Empty;
                    workspace = projectRepository.Length > 0 ? projectRepository : dedicatedBinary;
                    if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
                        throw new InvalidOperationException(
                            "No explicit Binary Analysis workspace is available. Attach the target file, choose a repository for the active Project, or configure the dedicated Binary Analysis workspace in Settings.");
                    workspace = Path.GetFullPath(workspace);
                }

                if (string.IsNullOrWhiteSpace(workspace))
                    throw new InvalidOperationException(
                        "Could not determine a workspace folder for the selected artifact.");

                await PaRuXServerController.Current.EnsureRunningAsync(
                    new Progress<string?>(status =>
                    {
                        Status = status;
                        RaiseState();
                    }), cts.Token).ConfigureAwait(false);

                Status = "Binary Analysis · local agent running…";
                RaiseState();

                BinaryAnalysisAgentResult result = await BinaryAnalysisAgentService.RunAsync(
                    workspace,
                    effectiveGoal,
                    line =>
                    {
                        if (log.Length > 0) log.AppendLine();
                        log.Append("- ").Append(line);
                        reply.Content = log.ToString();
                        MessageUpdated?.Invoke(reply);
                    },
                    cts.Token).ConfigureAwait(false);

                if (log.Length > 0) log.AppendLine().AppendLine();
                if (result.Cancelled || cts.IsCancellationRequested)
                {
                    log.Append("_Binary Analysis stopped._");
                }
                else
                {
                    log.Append("**").Append(result.GoalReported ? "Done" : "Stopped").Append(":** ")
                        .Append(result.Summary);
                    reply.IsError = !result.GoalReported;
                }
                reply.Content = log.ToString();
            }
            catch (OperationCanceledException)
            {
                if (log.Length > 0) log.AppendLine().AppendLine();
                log.Append("_Binary Analysis stopped._");
                reply.Content = log.ToString();
            }
            catch (Exception ex)
            {
                Log.Write("Direct Binary Analysis failed: " + ex);
                if (log.Length > 0) log.AppendLine().AppendLine();
                log.Append("Binary Analysis failed: ").Append(ex.Message);
                reply.Content = log.ToString();
                reply.IsError = true;
            }
            finally
            {
                LocalAgentCancellationRegistry.Unregister(cancellationRegistration);
                reply.IsStreaming = false;
                IsBusy = false;
                Status = null;
                _cts = null;
                try { cts.Dispose(); } catch { }
                MessageUpdated?.Invoke(reply);
                RaiseState();
                Persist();
            }
        }

        private async Task RunTurnAsync(RouteKind route, string? oneShotSkillId, RouteRequest? forcedModelRoute)
        {
            if (GamingManager.Current.IsActive)
            {
                var blocked = new ChatMessage
                {
                    Role = "assistant",
                    Content = "**AI paused by Gaming — Return to AI.** Use the Return to AI button in the chat header (or Settings > Gaming). PaRuX will not reload a model automatically; it loads on your next message after you return."
                };
                Conversation.Messages.Add(blocked);
                MessageAdded?.Invoke(blocked);
                Persist();
                return;
            }

            string? lastUserContent = Conversation.Messages.LastOrDefault(m => m.Role == "user")?.Content;
            ComputerActionPlan? computerPlan = route == RouteKind.ComputerUse
                ? ComputerUseService.TryPlan(lastUserContent)
                : null;

            if (route == RouteKind.BinaryAnalysis)
            {
                await RunDirectBinaryAnalysisAsync(lastUserContent).ConfigureAwait(false);
                return;
            }

            if (route == RouteKind.ComputerUse)
            {
                if (!SettingsStore.Current.Data.ComputerUseFullAutomationEnabled)
                {
                    AddComputerUseApproval(computerPlan, lastUserContent);
                    return;
                }

                if (computerPlan != null)
                    await RunPreauthorizedComputerLaunchAsync(computerPlan).ConfigureAwait(false);
                else
                    await RunPreauthorizedComputerAgentAsync(lastUserContent).ConfigureAwait(false);
                return;
            }

            if (!RouterService.IsChatRoute(route) && route != RouteKind.Image && route != RouteKind.Video && route != RouteKind.ImageEdit && route != RouteKind.Music)
            {
                var deferred = new ChatMessage
                {
                    Role = "assistant",
                    Content = RouterService.DeferredReply(route),
                    ActionKind = route == RouteKind.CodeHandoff ? "code-handoff" : null,
                    ActionLabel = route == RouteKind.CodeHandoff ? "Start PaRuX Coding Agent" : null,
                    ActionPayload = null
                };
                Conversation.Messages.Add(deferred);
                Action<ChatMessage>? addedDeferred = MessageAdded;
                if (addedDeferred != null) addedDeferred(deferred);
                Persist();
                return;
            }

            var reply = new ChatMessage { Role = "assistant", Content = string.Empty, IsStreaming = true };
            Conversation.Messages.Add(reply);
            Action<ChatMessage>? addedReply = MessageAdded;
            if (addedReply != null) addedReply(reply);

            var cts = new CancellationTokenSource();
            _cts = cts;
            IsBusy = true;
            RaiseState();

            // Set when the Core reports it had to compact this turn; used to explain an empty reply
            // instead of leaving the user with a bare notice.
            string? contextCompactedDetail = null;
            // Set when Core answered with a computer_use_handoff; the agent starts after this turn's bookkeeping is done.
            string? computerUseHandoffTask = null;
            try
            {
                var progress = new Progress<string?>(s =>
                {
                    Status = s;
                    RaiseState();
                });

                await PaRuXServerController.Current.EnsureRunningAsync(progress, cts.Token).ConfigureAwait(false);

                Status = null;
                RaiseState();

                if (route == RouteKind.Vision)
                {
                    await EnsureVisionProfileReadyAsync(cts.Token).ConfigureAwait(false);
                }

                string? chainedVideoSourceBase64 = null;
                if (route == RouteKind.Image)
                {
                    Status = "Generating your image locally…";
                    RaiseState();
                    ChatMessage request = Conversation.Messages.Last(m => m.Role == "user");
                    AppSettings settings = SettingsStore.Current.Data;
                    var imageProgress = new Progress<PaRuX.Contracts.MediaJobStatus>(job =>
                    {
                        if (job.Status == "completed")
                        {
                            Status = "Saving image locally…";
                            RaiseState();
                            return;
                        }
                        string eta = job.EtaSeconds.HasValue ? " · ETA ~" + job.EtaSeconds.Value + "s" : string.Empty;
                        Status = job.Stage + " · " + job.ProgressPercent + "%" + eta;
                        RaiseState();
                    });
                    PaRuX.Contracts.MediaAsset image = await RunTrackedMediaJobAsync(reply, "image", RouterService.HasFollowUpVideoStep(request.Content),
                        new PaRuX.Contracts.MediaJobRequest
                        {
                            Type = "image", Prompt = request.Content,
                            Width = settings.GeneratedImageWidth, Height = settings.GeneratedImageHeight,
                            Seed = -1
                        }, imageProgress, cts.Token).ConfigureAwait(false);

                    byte[] bytes = Convert.FromBase64String(image.Base64Data);
                    string extension = image.MediaType == "image/jpeg" ? ".jpg" : ".png";
                    string outputFolder = string.IsNullOrWhiteSpace(settings.GeneratedImagesFolder)
                        ? AppPaths.GeneratedImagesDir
                        : settings.GeneratedImagesFolder;
                    if (StorageQuotaService.IsManagedPath(outputFolder)) StorageQuotaService.EnsureCapacity(bytes.LongLength);
                    Directory.CreateDirectory(outputFolder);
                    string path = Path.Combine(outputFolder,
                        Guid.NewGuid().ToString("N") + extension);
                    File.WriteAllBytes(path, bytes);
                    ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment
                    {
                        FileName = image.FileName,
                        LocalPath = path,
                        MediaType = image.MediaType,
                        SizeBytes = bytes.LongLength
                    }, reply.MediaJobId ?? Guid.NewGuid().ToString("N"), "Generated locally · " + image.Width + "×" + image.Height
                        + " · seed " + image.Seed);
                    Persist();
                    // "Generate an image, then animate it": the second step reuses this image.
                    if (RouterService.HasFollowUpVideoStep(request.Content))
                        chainedVideoSourceBase64 = image.Base64Data;
                }

                if (route == RouteKind.Video || chainedVideoSourceBase64 != null)
                {
                    ChatMessage request = Conversation.Messages.Last(m => m.Role == "user");
                    AppSettings settings = SettingsStore.Current.Data;
                    string videoSourceBase64 = chainedVideoSourceBase64 ?? string.Empty;
                    ChatAttachment? videoSource = chainedVideoSourceBase64 != null ? null
                        : request.Attachments?.FirstOrDefault(a =>
                            a.MediaType != null && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(a.LocalPath) && File.Exists(a.LocalPath));
                    if (videoSource != null)
                        videoSourceBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(videoSource.LocalPath, cts.Token).ConfigureAwait(false));
                    Status = "Generating your video locally…";
                    RaiseState();
                    var mediaProgress = new Progress<PaRuX.Contracts.MediaJobStatus>(job =>
                    {
                        if (job.Status == "completed")
                        {
                            Status = "Saving video locally…";
                            RaiseState();
                            return;
                        }
                        string eta = job.EtaSeconds.HasValue ? " · ETA ~" + job.EtaSeconds.Value + "s" : string.Empty;
                        Status = job.Stage + " · " + job.ProgressPercent + "%" + eta;
                        RaiseState();
                    });
                    PaRuX.Contracts.MediaAsset video = await RunTrackedMediaJobAsync(reply, "video", chainedVideoSourceBase64 != null,
                        new PaRuX.Contracts.MediaJobRequest
                        {
                            Type = "video", Prompt = request.Content, SourceImageBase64 = videoSourceBase64,
                            Width = settings.GeneratedVideoWidth, Height = settings.GeneratedVideoHeight,
                            DurationSeconds = settings.GeneratedVideoDurationSeconds,
                            Fps = settings.GeneratedVideoFps, Steps = settings.GeneratedVideoSteps, Seed = -1
                        }, mediaProgress, cts.Token).ConfigureAwait(false);
                    byte[] bytes = Convert.FromBase64String(video.Base64Data);
                    string outputFolder = string.IsNullOrWhiteSpace(settings.GeneratedVideosFolder)
                        ? AppPaths.GeneratedVideosDir : settings.GeneratedVideosFolder;
                    if (StorageQuotaService.IsManagedPath(outputFolder)) StorageQuotaService.EnsureCapacity(bytes.LongLength);
                    Directory.CreateDirectory(outputFolder);
                    string extension = video.MediaType == "video/webm" ? ".webm" : ".mp4";
                    string path = Path.Combine(outputFolder, Guid.NewGuid().ToString("N") + extension);
                    File.WriteAllBytes(path, bytes);
                    ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment
                    {
                        FileName = video.FileName, LocalPath = path,
                        MediaType = video.MediaType, SizeBytes = bytes.LongLength
                    }, reply.MediaJobId ?? Guid.NewGuid().ToString("N"), "Generated locally · " + video.Width + "×" + video.Height
                        + " · " + video.DurationSeconds.ToString("0.0") + "s · " + video.Fps
                        + " fps · " + video.Steps + " steps · seed " + video.Seed);
                }
                else if (route == RouteKind.ImageEdit)
                {
                    ChatMessage request = Conversation.Messages.Last(m => m.Role == "user");
                    ChatAttachment? sourceAttachment = request.Attachments?.FirstOrDefault(a =>
                        a.MediaType != null && a.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(a.LocalPath) && File.Exists(a.LocalPath));
                    if (sourceAttachment == null)
                    {
                        reply.Content = "I need an attached image to edit - attach one and describe the change.";
                        reply.IsError = true;
                    }
                    else if (CutoutPhrasing.IsBackgroundRemoval(request.Content))
                    {
                        // "Remove the background" / "rimuovi lo sfondo" is a cutout (transparent PNG), not a diffusion edit.
                        Status = "Removing the background locally…";
                        RaiseState();
                        PaRuX.Contracts.MediaAsset cutout = await CutoutClient.Current.RemoveAsync(sourceAttachment.LocalPath, cts.Token).ConfigureAwait(false);
                        AppSettings cutoutSettings = SettingsStore.Current.Data;
                        string cutoutPath = await CutoutClient.SaveAsync(cutout,
                            string.IsNullOrWhiteSpace(cutoutSettings.GeneratedImagesFolder) ? null : cutoutSettings.GeneratedImagesFolder,
                            cts.Token).ConfigureAwait(false);
                        ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment
                        {
                            FileName = cutout.FileName, LocalPath = cutoutPath,
                            MediaType = cutout.MediaType, SizeBytes = new FileInfo(cutoutPath).Length
                        }, reply.MediaJobId ?? Guid.NewGuid().ToString("N"),
                        "Background removed locally · BiRefNet-lite · " + cutout.Width + "×" + cutout.Height + " · transparent PNG");
                        Persist();
                    }
                    else
                    {
                        Status = "Editing your image locally…";
                        RaiseState();
                        string sourceBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(sourceAttachment.LocalPath, cts.Token).ConfigureAwait(false));
                        var mediaProgress = new Progress<PaRuX.Contracts.MediaJobStatus>(job =>
                        {
                            if (job.Status == "completed")
                            {
                                Status = "Saving edited image locally…";
                                RaiseState();
                                return;
                            }
                            string eta = job.EtaSeconds.HasValue ? " · ETA ~" + job.EtaSeconds.Value + "s" : string.Empty;
                            Status = job.Stage + " · " + job.ProgressPercent + "%" + eta;
                            RaiseState();
                        });
                        double? strength = TryParseStrength(request.Content);
                        PaRuX.Contracts.MediaAsset edited = await RunTrackedMediaJobAsync(reply, strength.HasValue ? "img2img" : "edit", false,
                            new PaRuX.Contracts.MediaJobRequest
                            {
                                Type = strength.HasValue ? "img2img" : "edit",
                                Prompt = request.Content, SourceImageBase64 = sourceBase64, Seed = -1,
                                Strength = strength
                            }, mediaProgress, cts.Token).ConfigureAwait(false);

                        AppSettings settings = SettingsStore.Current.Data;
                        byte[] bytes = Convert.FromBase64String(edited.Base64Data);
                        string extension = edited.MediaType == "image/jpeg" ? ".jpg" : ".png";
                        string outputFolder = string.IsNullOrWhiteSpace(settings.GeneratedImagesFolder)
                            ? AppPaths.GeneratedImagesDir : settings.GeneratedImagesFolder;
                        if (StorageQuotaService.IsManagedPath(outputFolder)) StorageQuotaService.EnsureCapacity(bytes.LongLength);
                        Directory.CreateDirectory(outputFolder);
                        string path = Path.Combine(outputFolder, Guid.NewGuid().ToString("N") + extension);
                        File.WriteAllBytes(path, bytes);
                        ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment
                        {
                            FileName = edited.FileName, LocalPath = path,
                            MediaType = edited.MediaType, SizeBytes = bytes.LongLength
                        }, reply.MediaJobId ?? Guid.NewGuid().ToString("N"),
                        "Edited locally · " + edited.Width + "×" + edited.Height + " · seed " + edited.Seed
                        + (RouterService.RegionEditHint(request.Content) is { } regionHint ? "\n\n" + regionHint : string.Empty));
                    }
                }
                else if (route == RouteKind.Music)
                {
                    ChatMessage request = Conversation.Messages.Last(m => m.Role == "user");
                    Status = "Composing your music locally…";
                    RaiseState();
                    PaRuX.Contracts.MediaAsset song = await MusicClient.Current.GenerateAsync(new PaRuX.Contracts.MusicGenerateRequest
                    {
                        Prompt = request.Content, WriteLyrics = true, Language = MediaPhrasing.GuessLanguage(request.Content),
                        DurationSeconds = 45, Seed = -1
                    }, cts.Token).ConfigureAwait(false);
                    string songPath = await MusicClient.SaveAsync(song, cts.Token).ConfigureAwait(false);
                    ChatMediaJobReconciler.ApplyCompleted(reply, new ChatAttachment
                    {
                        FileName = song.FileName, LocalPath = songPath, MediaType = song.MediaType, SizeBytes = new FileInfo(songPath).Length
                    }, reply.MediaJobId ?? Guid.NewGuid().ToString("N"),
                    "Composed locally · ACE-Step 1.5 · " + song.DurationSeconds.ToString("0") + "s · seed " + song.Seed);
                    Persist();
                }
                else if (route != RouteKind.Image)
                {
                    if (route == RouteKind.Vision)
                    {
                        Status = "Analyzing visual media locally…";
                        RaiseState();
                    }
                    var history = Conversation.Messages.Where(m => !ReferenceEquals(m, reply)).ToList();
                    string? forcedSkillId = oneShotSkillId ?? ForcedSkillId;
                    var buffer = new System.Text.StringBuilder();
                    var reasoningBuffer = new System.Text.StringBuilder();
                    bool computerUseConsent = false;
                    bool computerUseDeclined = false;
                    string? modalityTask = Conversation.Messages.LastOrDefault(m => m.Role == "user")?.Content;
                    // The intent step already knows this is a Computer Use request: ask before anything is sent.
                    if (ModalityIntent.IsComputerUse(modalityTask))
                    {
                        Status = "Waiting for your Computer Use decision…";
                        RaiseState();
                        computerUseConsent = await AskComputerUseConsentAsync(modalityTask).ConfigureAwait(false);
                        computerUseDeclined = !computerUseConsent;
                        Status = null;
                        RaiseState();
                    }
                    if (computerUseDeclined)
                    {
                        reply.Content = "Computer Use was not started because you did not approve it.";
                    }
                    else
                    for (int attempt = 0; ; attempt++)
                    {
                    try
                    {
                    await ChatClient.Current.StreamAsync(
                        history,
                        RouterService.SystemPrompt(route) ?? string.Empty,
                        fragment =>
                        {
                            buffer.Append(fragment);
                            reply.Content = buffer.ToString();
                            Action<ChatMessage>? upd = MessageUpdated;
                            if (upd != null) upd(reply);
                        },
                        () =>
                        {
                            reply.SwitchedToUncensored = true;
                            Action<ChatMessage>? upd = MessageUpdated;
                            if (upd != null) upd(reply);
                        },
                        sources =>
                        {
                            reply.RagSources = sources;
                            Action<ChatMessage>? upd = MessageUpdated;
                            if (upd != null) upd(reply);
                        },
                        (displayName, mode) =>
                        {
                            if (mode == "auto" && string.Equals(displayName, "General", StringComparison.OrdinalIgnoreCase)) return;
                            reply.SkillUsed = displayName + (mode == "forced" ? " (forced)" : mode == "slash" ? " (/" + displayName.ToLowerInvariant() + ")" : string.Empty);
                            Action<ChatMessage>? upd = MessageUpdated;
                            if (upd != null) upd(reply);
                        },
                        forcedSkillId,
                        reasoningFragment =>
                        {
                            reasoningBuffer.Append(reasoningFragment);
                            reply.Reasoning = reasoningBuffer.ToString();
                            Action<ChatMessage>? upd = MessageUpdated;
                            if (upd != null) upd(reply);
                        },
                        route == RouteKind.ChatFast,
                        cts.Token,
                        detail => contextCompactedDetail = detail,
                        forcedModelRoute: forcedModelRoute,
                        onProvenance: provenance =>
                        {
                            reply.ModelId = provenance.ModelId;
                            reply.Provider = provenance.Provider;
                            reply.Locality = provenance.Locality;
                            reply.FallbackReason = provenance.FallbackReason;
                            reply.ManualLock = provenance.ManualLock;
                            Action<ChatMessage>? upd = MessageUpdated;
                            if (upd != null) upd(reply);
                        },
                        computerUseConsent: computerUseConsent,
                        onComputerUseHandoff: task => computerUseHandoffTask = string.IsNullOrWhiteSpace(task) ? modalityTask : task).ConfigureAwait(false);
                    break;
                    }
                    catch (ModalityRefusalException refused)
                        when (refused.Refusal.Code == ModalityRefusalPresenter.ConsentRequired && !computerUseConsent && attempt == 0)
                    {
                        // Core asked for consent the intent step did not foresee. Ask once; resend only on an explicit yes.
                        computerUseConsent = await AskComputerUseConsentAsync(modalityTask).ConfigureAwait(false);
                        if (!computerUseConsent)
                        {
                            reply.Content = "Computer Use was not started because you did not approve it.";
                            break;
                        }
                        buffer.Clear();
                        reasoningBuffer.Clear();
                    }
                    }
                }

                if (reply.Content.Length == 0)
                {
                    // An empty reply is almost never the model "having nothing to say": when the
                    // Core had to compact the turn, or could not fit it at all, it says exactly why.
                    // Repeating that beats the bare notice, which left a user with no way to tell a
                    // model quirk from a window too small to hold their question.
                    reply.Content = contextCompactedDetail == null
                        ? "_The model returned an empty response._"
                        : "_The model returned an empty response. " + contextCompactedDetail.TrimEnd('.') + "._";
                }
            }
            catch (OperationCanceledException) when (ChatMediaJobReconciler.NeedsReconcile(reply))
            {
                ChatMediaJobReconciler.ApplyCancelled(reply);
            }
            catch (LocalAiException ex) when (ChatMediaJobReconciler.NeedsReconcile(reply))
            {
                ChatMediaJobReconciler.ApplyFailure(reply, ex.Message);
            }
            catch (Exception ex) when (ChatMediaJobReconciler.NeedsReconcile(reply) && ex is not OperationCanceledException)
            {
                Log.Write("Chat media turn failed: " + ex);
                ChatMediaJobReconciler.ApplyFailure(reply, ex.Message);
            }
            catch (OperationCanceledException)
            {
                if (reply.Content.Length == 0)
                {
                    reply.Content = "_Stopped._";
                    reply.IsError = true;
                }
                else
                {
                    reply.Content += "\n\n_Stopped._";
                }
            }
            catch (LocalAiException ex)
            {
                reply.Content = ex.Message;
                reply.IsError = true;
            }
            catch (Exception ex)
            {
                Log.Write("Chat turn failed: " + ex);
                reply.Content = "Something went wrong talking to the local model: " + ex.Message;
                reply.IsError = true;
            }
            finally
            {
                reply.IsStreaming = false;
                IsBusy = false;
                Status = null;
                _cts = null;
                try { cts.Dispose(); } catch { }

                Action<ChatMessage>? upd = MessageUpdated;
                if (upd != null) upd(reply);
                RaiseState();
                Persist();
            }

            if (computerUseHandoffTask != null)
            {
                // Consent was given in this turn (the request carried ComputerUseConsent), so the run holds an explicit
                // per-task approval. The agent itself still refuses when Gaming mode is active or the input gate is taken.
                await RunPreauthorizedComputerAgentAsync(computerUseHandoffTask, taskConsent: true).ConfigureAwait(false);
            }
        }
    }
}
