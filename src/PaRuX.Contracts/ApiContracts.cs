using System.Text.Json.Serialization;

namespace PaRuX.Contracts;

public sealed class ChatCompletionRequest
{
    public string Model { get; set; } = "local-default";
    /// <summary>Optional exact model selected in Model Intelligence for this single desktop chat turn.</summary>
    public RouteRequest? ForcedModelRoute { get; set; }
    public List<ChatCompletionMessage> Messages { get; set; } = new();
    public bool Stream { get; set; } = true;
    /// <summary>Hints that this is a simple interactive turn where first-token latency matters
    /// more than optional retrieval and tool discovery. The server still applies routing,
    /// persona, safety and the conversation messages supplied by the client.</summary>
    public bool PreferLowLatency { get; set; }
    /// <summary>Optional id of the server-synced persisted conversation this turn belongs to.
    /// Older clients omit it and keep the exact pre-existing behavior; when present, the Context
    /// Engine may include bounded recent history from that one conversation only.</summary>
    public string? ConversationId { get; set; }
    /// <summary>Optional per-turn override for persisted conversation history. Null preserves the
    /// legacy behavior (include history when ConversationId is present); false is an explicit
    /// privacy/context opt-out for this request; true includes it when an id is available.</summary>
    public bool? IncludeConversationHistory { get; set; }
    /// <summary>Non-empty forces this skill id instead of AUTO routing. See SkillRouter.</summary>
    public string? SkillId { get; set; }
    /// <summary>Values for the forced skill's declared Parameters (e.g. Grill Me's difficulty/style/questions).</summary>
    public Dictionary<string, string> SkillParameters { get; set; } = new();
    /// <summary>
    /// LM Studio-style "edit and continue": when true, the final message in <see cref="Messages"/>
    /// must be role "assistant" (the client's user-edited text), and the provider is asked to
    /// resume generating from exactly that text rather than starting a fresh turn. Requires the
    /// PaRuX-managed local runtime - llama.cpp's continuation extension has no guaranteed
    /// equivalent on an arbitrary USER_MANAGED OpenAI-compatible endpoint.
    /// </summary>
    public bool ContinueFinalMessage { get; set; }
    /// <summary>OpenAI-compatible cap on generated tokens. Forwarded to the runtime; non-positive values are rejected with HTTP 400.</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
    /// <summary>OpenAI's newer name for <see cref="MaxTokens"/>; wins when both are supplied.</summary>
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }
    /// <summary>Sampling temperature (0..2). Forwarded when set, otherwise PaRuX's launch defaults apply.</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }
    /// <summary>Nucleus sampling (0..1). Forwarded when set.</summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }
    /// <summary>Sampling seed. Forwarded when set.</summary>
    [JsonPropertyName("seed")]
    public long? Seed { get; set; }
    /// <summary>A stop string or an array of up to four stop strings (OpenAI shape). Forwarded when set.</summary>
    [JsonPropertyName("stop")]
    public System.Text.Json.JsonElement? Stop { get; set; }
    /// <summary>The client's explicit, per-request approval for Computer Use. Absent/false means a computer-use request is refused.</summary>
    public bool ComputerUseConsent { get; set; }
}

public sealed class ChatCompletionMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public List<ChatImageContent> Images { get; set; } = new();
    /// <summary>Audio attachments (additive). A user turn with audio is routed to the local speech engine, never to a text model.</summary>
    public List<ChatAudioContent> Audio { get; set; } = new();
}

public sealed class ChatAudioContent
{
    public string FileName { get; set; } = "audio.wav";
    public string MediaType { get; set; } = "audio/wav";
    public string Base64Data { get; set; } = string.Empty;
}

public sealed class ChatImageContent
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/png";
    public string Base64Data { get; set; } = string.Empty;
}

public sealed class ServerHealth
{
    public string Status { get; set; } = "ok";
    public string Version { get; set; } = "1.1.0";
    public bool ProviderReady { get; set; }
    public bool GamingMode { get; set; }
}

/// <summary>One thing to do next, and the measurement that says so.</summary>
public sealed class TrainingAdvice
{
    /// <summary>Lower runs first. Ordering is by what the evidence most supports, not by category.</summary>
    public int Priority { get; set; }

    /// <summary>Machine-readable kind: measure, speculative-decoding, latency, prompt-cache,
    /// evidence, prompt-first, adapter-candidate, no-training.</summary>
    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    /// <summary>The measurement this rests on, so the user can check it rather than trust it.</summary>
    public string Evidence { get; set; } = string.Empty;
}

public sealed class TrainingAdviceReport
{
    public string Workload { get; set; } = string.Empty;

    /// <summary>False when PaRuX has never benchmarked this model on this hardware, which is the
    /// one case where the honest advice is to measure before doing anything else.</summary>
    public bool HasMeasurement { get; set; }

    public List<TrainingAdvice> Advice { get; set; } = new();
}

/// <summary>A durable key another application uses to call PaRuX as an OpenAI or Ollama provider.</summary>
public sealed class ApiKeyInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    /// <summary>Null until the key is first used, so a user revoking keys can see which are dead.</summary>
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>The one and only time the plaintext key exists outside the caller's program.</summary>
public sealed class ApiKeyCreated
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    /// <summary>Shown once. PaRuX keeps only a hash and cannot display this again.</summary>
    public string Key { get; set; } = string.Empty;
}

public sealed class ApiKeyCreateRequest
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>The passphrase a browser presents to open a PaRuX session through a reverse proxy.</summary>
public sealed class WebSessionRequest
{
    public string Passphrase { get; set; } = string.Empty;
}

public sealed class WebSessionResponse
{
    public bool Authenticated { get; set; }

    /// <summary>Echoed back in the X-PaRuX-Csrf header on every state-changing request. Kept in the
    /// page's memory rather than a cookie, because the point of it is to be the half a cross-site
    /// attacker cannot reproduce.</summary>
    public string CsrfToken { get; set; } = string.Empty;
}

/// <summary>What Settings shows about the browser door, and what it can change.</summary>
public sealed class WebAccessStatus
{
    public bool Enabled { get; set; }
    public bool PassphraseSet { get; set; }
    public int ActiveSessions { get; set; }

    /// <summary>Where to point the reverse proxy, and what to open locally to check it works.</summary>
    public string LocalUrl { get; set; } = string.Empty;
}

public sealed class WebAccessConfigRequest
{
    public bool? Enabled { get; set; }

    /// <summary>Null leaves the existing passphrase alone; a value replaces it and signs everyone out.</summary>
    public string? Passphrase { get; set; }
}

public sealed class RemoteAccessStatus
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 51124;
    public string Endpoint { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
    public bool RestartRequired { get; set; }

    /// <summary>
    /// How Windows classifies the network this PC is on: "Public", "Private", "Domain" or
    /// "unknown". A private IPv4 address is not the same thing - Windows Firewall applies its rules
    /// by this category, not by address range - and on a Public network an inbound allowance for
    /// PaRuX Core has to be made against the Public profile or the network reclassified, so a phone
    /// on the same Wi-Fi cannot pair until one of those happens.
    /// </summary>
    public string NetworkCategory { get; set; } = string.Empty;

    public List<PairedDeviceInfo> Devices { get; set; } = new();
}

public sealed class RemoteAccessConfigRequest
{
    public bool Enabled { get; set; }
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 51124;
}

public sealed class PairingStartResponse
{
    public string Code { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
}

public sealed class PairingCompleteRequest
{
    public string Code { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}

public sealed class PairingCompleteResponse
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceToken { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
}

public sealed class PairedDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

public sealed class ServerBootstrap
{
    public string Endpoint { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTime StartedUtc { get; set; }
}

public sealed class RuntimeActionRequest
{
    public string Action { get; set; } = string.Empty;
}

public sealed class RuntimeActionResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class LocalAiRootStatus
{
    public string Root { get; set; } = string.Empty;
    public bool Configured { get; set; }
    public bool LooksConfigured { get; set; }
}

public sealed class LocalAiRootRequest
{
    public string Root { get; set; } = string.Empty;
}

public static class ModelCapabilityIds
{
    public const string GeneralChat = "chat.general";
    public const string Reasoning = "chat.reasoning";
    public const string Coding = "coding";
    public const string Vision = "vision";
    public const string ImageGenerate = "image.generate";
    public const string ImageEdit = "image.edit";
    public const string VideoGenerate = "video.generate";
    /// <summary>Speech-to-text and audio understanding (transcribe first, then reason over the transcript).</summary>
    public const string AudioTranscribe = "audio.transcribe";
    public const string AudioSpeak = "audio.speak";
    /// <summary>A text model with verified tool-calling support.</summary>
    public const string ToolUse = "tool.use";
    /// <summary>The Computer Use agent path; never a plain model, always consent-gated.</summary>
    public const string ComputerUse = "computer.use";
}

/// <summary>Content preference is an independent axis, not a task capability or quality score.</summary>
public static class ModelContentProfileIds
{
    public const string Standard = "standard";
    public const string Uncensored = "uncensored";
}

public static class ModelAdviceEvidenceStates
{
    /// <summary>Hardware/workflow fit may be estimated, but no scoped quality/performance winner exists.</summary>
    public const string Provisional = "provisional_no_quality_winner";
}

public sealed class RecommendedModel
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string HuggingFaceRepoId { get; set; } = string.Empty;
    public string ApproxParams { get; set; } = string.Empty;
    public string QuantHint { get; set; } = string.Empty;
    /// <summary>Explicit task tags; display-category text is never parsed as capability evidence.</summary>
    public List<string> CapabilityIds { get; set; } = new();
    /// <summary>Separate content preference, such as standard or uncensored. Defaults to standard.</summary>
    public string ContentProfileId { get; set; } = ModelContentProfileIds.Standard;
    /// <summary>Rough on-disk size of the recommended quant, for client-side hardware-fit
    /// classification (ModelFitClassifier) before any file is actually chosen or downloaded.</summary>
    public long EstimatedSizeBytes { get; set; }
    /// <summary>Optional per-quant runtime-memory multiplier. Null uses the conservative shared
    /// estimate; a value is still provisional and must not be treated as a measured fit unless its
    /// exact file/runtime/configuration evidence is available.</summary>
    public double? EstimatedRuntimeMemoryFactor { get; set; }
}

/// <summary>
/// Hardware-fit candidates for a first-time user. Fit is not task-quality evidence and does not
/// establish a universal winner. The capability groups keep task and content-profile advice separate.
/// </summary>
public sealed class FirstModelAdvice
{
    public string HardwareSummary { get; set; } = string.Empty;
    public bool GpuDetected { get; set; }
    public long VramBytes { get; set; }
    public long RamBytes { get; set; }

    /// <summary>Optional general-chat starter candidate selected by the current fit heuristic.</summary>
    public string? RecommendedId { get; set; }
    public string Headline { get; set; } = string.Empty;

    /// <summary>Hardware-fit list, including candidates that do not fit this machine.</summary>
    public List<FirstModelCandidate> Candidates { get; set; } = new();

    /// <summary>Task-specific provisional candidate groups. Empty groups deliberately mean the
    /// local starter catalog has no candidate for that capability, not that another model can serve it.</summary>
    public List<FirstModelCapabilityGroup> CapabilityGroups { get; set; } = new();
}

public sealed class FirstModelCapabilityGroup
{
    public string CapabilityId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>Optional content preference axis, for example standard or uncensored chat.</summary>
    public string ContentProfileId { get; set; } = string.Empty;
    public string EvidenceState { get; set; } = ModelAdviceEvidenceStates.Provisional;
    public string EvidenceNote { get; set; } = string.Empty;
    public List<FirstModelCandidate> TextCandidates { get; set; } = new();
    public List<RecommendedCheckpoint> MediaCandidates { get; set; } = new();
}

public sealed class FirstModelCandidate
{
    public RecommendedModel Model { get; set; } = new();
    /// <summary>"FitsOnGpu" | "SplitWithRam" | "CpuOnly" | "TooLarge" | "Unknown".</summary>
    public string Suitability { get; set; } = string.Empty;
    /// <summary>One sentence naming the actual hardware, for display next to the model.</summary>
    public string Reason { get; set; } = string.Empty;
    public int Rank { get; set; }
    /// <summary>Null when the model has no known runtime requirement; otherwise "Satisfied" |
    /// "NeedsRuntimeUpdate" | "NeedsUserAction". A needing state means the installed llama.cpp cannot load it.</summary>
    public string? RuntimeRequirementState { get; set; }
    public string? RuntimeRequirementMessage { get; set; }
}

public sealed class ImageGenerationRequest
{
    public string Prompt { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public long Seed { get; set; } = -1;
}

public sealed class ImageGenerationResponse
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/png";
    public string Base64Data { get; set; } = string.Empty;
    public long Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class ConversationSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class ConversationDocument
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
}

public sealed class ConversationUpsertRequest
{
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string DocumentJson { get; set; } = string.Empty;
}

public sealed class AiProfileStatus
{
    public string Mode { get; set; } = "AUTO";
    public string TextProfile { get; set; } = "STANDARD";
    public string ActiveProfile { get; set; } = string.Empty;
    public bool GamingMode { get; set; }

    /// <summary>
    /// Whether PaRuX may automatically escalate a refused/limited STANDARD reply
    /// to the UNCENSORED profile and retry. Server-owned so every client (desktop,
    /// paired devices) reads and toggles the same switch.
    /// </summary>
    public bool AutoUncensoredFallback { get; set; } = true;
}

public sealed class AiProfileRequest
{
    public string TextProfile { get; set; } = "STANDARD";
}

public sealed class AiAutoFallbackRequest
{
    public bool Enabled { get; set; } = true;
}

/// <summary>A local AI backend (Ollama, LM Studio, koboldcpp, text-generation-webui, ...) found
/// actually answering on its own well-known default port right now.</summary>
public sealed class DetectedTextBackend
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>Whether chat is currently routed to PaRuX's own bundled model (Configured=false) or
/// to a user-configured external OpenAI-compatible backend instead.</summary>
public sealed class ExternalTextProviderStatus
{
    public bool Configured { get; set; }
    public string? ProviderName { get; set; }
    public string? Endpoint { get; set; }
    public bool Reachable { get; set; }

    /// <summary>True when the configured endpoint is off this machine and its private LAN, i.e.
    /// prompts sent through it leave the device. Surfaced so the UI can say so plainly rather than
    /// leaving a remote provider visually indistinguishable from a local one.</summary>
    public bool IsRemote { get; set; }
}

/// <summary>
/// Toggles the remote-inference opt-in. <see cref="ApiKey"/> is null when the caller is not
/// changing the credential, so flipping the policy does not silently wipe a key set earlier;
/// an empty string clears it. Disabling the policy always drops the key regardless.
/// </summary>
public sealed class SetRemoteProviderPolicyRequest
{
    public bool AllowRemoteProviders { get; set; }
    public string? ApiKey { get; set; }
}

public sealed class SetExternalTextProviderRequest
{
    public string ProviderName { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>
/// State of the cloud <em>fallback</em> provider - the one PaRuX may call only when its own local
/// runtime could not serve a turn. <see cref="Armed"/> is the single question the UI actually needs
/// answered ("could a fallback happen right now?"); <see cref="NotArmedReason"/> says why not, so a
/// half-configured fallback is never silently inert.
/// </summary>
public sealed class CloudFallbackStatus
{
    public bool Enabled { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool HasApiKey { get; set; }
    public bool Armed { get; set; }
    public string? NotArmedReason { get; set; }

    /// <summary>When a turn last actually left the device through the fallback, and why. Null until
    /// it has been used - the user is entitled to see this after the fact, not only in the moment.</summary>
    public DateTime? LastUsedUtc { get; set; }
    public string? LastUsedReason { get; set; }
    public int UseCount { get; set; }

    public List<CloudFallbackProviderOption> AvailableProviders { get; set; } = new();
}

/// <summary>One selectable provider preset. <see cref="BaseUrl"/> is empty for the custom entry,
/// where the user supplies an OpenAI-compatible https endpoint themselves.</summary>
public sealed class CloudFallbackProviderOption
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string SuggestedModel { get; set; } = string.Empty;
}

/// <summary>
/// Configures the cloud fallback. <see cref="ApiKey"/> is null when the caller is not changing the
/// credential, so re-saving a model id does not wipe a working key; an empty string clears it.
/// Setting <see cref="Enabled"/> to false always drops the stored key.
/// </summary>
public sealed class SetCloudFallbackRequest
{
    public bool Enabled { get; set; }
    public string ProviderId { get; set; } = "openrouter";
    public string? BaseUrl { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
}

/// <summary>Result of checking the fallback credential against the provider's model list. The check
/// never sends any of the user's text off the device.</summary>
public sealed class CloudFallbackTestResult
{
    public bool Success { get; set; }
    public bool ModelAvailable { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class MediaJobRequest
{
    /// <summary>"image" | "video" | "edit" | "img2img" | "upscale" | "custom" | "imported" (a stored user template; its id travels in CustomWorkflowId)</summary>
    public string Type { get; set; } = "image";
    /// <summary>For "edit"/"img2img", this describes the desired result. For "custom", used only
    /// if the imported workflow has a detected, overridable prompt node - otherwise ignored and
    /// the workflow's own baked-in prompt runs unchanged.</summary>
    public string Prompt { get; set; } = string.Empty;
    /// <summary>True when the user already reviewed/accepted this exact prompt (Create workspace review dialog):
    /// the server must run it verbatim and skip its own silent prompt refinement. Default false = existing behaviour.</summary>
    public bool PromptAlreadyReviewed { get; set; }
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public double DurationSeconds { get; set; } = 5;
    public int Fps { get; set; } = 24;
    public int Steps { get; set; } = 30;
    public long Seed { get; set; } = -1;
    /// <summary>"edit"/"img2img"/"upscale": base64-encoded source image. "custom": only if the
    /// imported workflow has a detected LoadImage node.</summary>
    public string SourceImageBase64 { get; set; } = string.Empty;
    /// <summary>"img2img" only: how much the result may deviate from the source image (0-1;
    /// ComfyUI's own KSampler "denoise" value). Low = stays close to the source, high = mostly
    /// ignores it and follows the prompt. Defaults to 0.6 when omitted.</summary>
    public double? Strength { get; set; }
    /// <summary>"custom" only: id of a previously imported workflow (see CustomWorkflowInfo).</summary>
    public string? CustomWorkflowId { get; set; }
    /// <summary>"imported" only: parameter key (for example "6.text") to value overrides for a stored user template.
    /// The server validates every key, type and range against the template's schema before anything is queued.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? WorkflowOverrides { get; set; }
}

/// <summary>A user-imported ComfyUI "API format" workflow. The booleans reflect what
/// CustomWorkflowService could actually auto-detect by node class_type when the workflow was
/// imported - never the underlying node ids, which stay a server-internal implementation
/// detail. A workflow with HasPromptControl=false / HasSeedControl=false still runs; it just
/// always uses its own baked-in prompt/seed rather than the caller's.</summary>
public sealed class CustomWorkflowInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool HasPromptControl { get; set; }
    public bool HasSeedControl { get; set; }
    public bool RequiresSourceImage { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class ImportCustomWorkflowRequest
{
    public string Name { get; set; } = string.Empty;
    /// <summary>The raw ComfyUI "API format" workflow JSON, exactly as exported from ComfyUI's
    /// own web UI (Workflow -> Export (API)) - either the bare node-id-&gt;node dict, or that
    /// dict nested under a top-level "prompt" key (both real ComfyUI export shapes).</summary>
    public string WorkflowJson { get; set; } = string.Empty;
}

public sealed class MediaAsset
{
    public string FileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public string Base64Data { get; set; } = string.Empty;
    /// <summary>Optional local file reference when recovery keeps the output on disk instead of embedding it.</summary>
    public string? FilePath { get; set; }
    public long Seed { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSeconds { get; set; }
    public int Fps { get; set; }
    public int Steps { get; set; }
}

public sealed class MediaJobStatus
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "Queued";
    public int ProgressPercent { get; set; }
    public int? EtaSeconds { get; set; }
    public string? Error { get; set; }
    /// <summary>Stable machine-readable failure category when available.</summary>
    public string? ErrorCode { get; set; }
    /// <summary>Typed reason behind <see cref="Error"/> when the failure is a missing or incompatible
    /// ComfyUI component, node or value. Null for untyped failures.</summary>
    public MediaDependencyError? ErrorDetail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public MediaAsset? Asset { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MediaJobProvenance? Provenance { get; set; }
}

/// <summary>Path-free attribution for the managed engine that produced or failed a media job.</summary>
public sealed class MediaJobProvenance
{
    public string EngineFamily { get; set; } = string.Empty;
    public string RuntimeIdentityHash { get; set; } = string.Empty;
    public string? ModelFileName { get; set; }
    public string Locality { get; set; } = "local";
    public string? FallbackReason { get; set; }
    /// <summary>Why the router chose this model for this request (short, no paths, no prompt text). Null when not routed.</summary>
    public string? ModelSelectionReason { get; set; }
    public string? FailureClass { get; set; }
}

// ---- Memory (server-owned, so desktop and paired devices share one store) ----

public sealed class MemoryEntryDto
{
    public string Id { get; set; } = string.Empty;
    public string Scope { get; set; } = "User";
    public string? ScopeKey { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class MemoryAddRequest
{
    public string Scope { get; set; } = "User";
    public string? ScopeKey { get; set; }
    public string Content { get; set; } = string.Empty;
}

public sealed class MemoryUpdateRequest
{
    public string Content { get; set; } = string.Empty;
}

public sealed class MemoryEnabledRequest
{
    public bool Enabled { get; set; } = true;
}

// ---- RAG (server-owned; folder paths are on the PC's own filesystem) ----

public sealed class RagSourceDto
{
    public string Id { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public DateTime AddedUtc { get; set; }
    public bool Enabled { get; set; } = true;
    public int DocumentCount { get; set; }
    public int ChunkCount { get; set; }
}

public sealed class RagAddSourceRequest
{
    public string FolderPath { get; set; } = string.Empty;
}

public sealed class RagEnabledRequest
{
    public bool Enabled { get; set; } = true;
}

// ---- Internet (server-owned; keyless web search, off by default) ----

public sealed class InternetStatus
{
    public bool Enabled { get; set; }
}

public sealed class InternetEnabledRequest
{
    public bool Enabled { get; set; }
}

// ---- Research Engine (Fast/Balanced/Deep Research, built on the Internet toggle above) ----

public sealed class ResearchSource
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public DateTime RetrievedUtc { get; set; }
    public string? FullText { get; set; }
}

public sealed class ResearchResult
{
    public string Answer { get; set; } = string.Empty;
    public List<ResearchSource> Sources { get; set; } = new();
    public List<string> QueriesUsed { get; set; } = new();
    /// <summary>Structured form of the same result - source_card blocks + numbered citations, per the Interactive Response Protocol. Answer/Sources above remain the plain-text source of truth; this is additive.</summary>
    public InteractiveMessage? Interactive { get; set; }
}

public sealed class ResearchRequest
{
    public string Question { get; set; } = string.Empty;
    /// <summary>"fast" | "balanced" | "deep"</summary>
    public string Mode { get; set; } = "balanced";
}

// ---- Presentations (native PPTX generation, no external service) ----

public sealed class PresentationSlideDto
{
    public string Title { get; set; } = string.Empty;
    public List<string> Bullets { get; set; } = new();
    public string SpeakerNotes { get; set; } = string.Empty;
}

public sealed class PresentationRequest
{
    public string Topic { get; set; } = string.Empty;
    public int SlideCount { get; set; } = 10;
}

public sealed class PresentationResponse
{
    public string FileName { get; set; } = string.Empty;
    public List<PresentationSlideDto> Slides { get; set; } = new();
    /// <summary>Base64-encoded .pptx bytes, same pattern as ImageGenerationResponse/MediaAsset.</summary>
    public string Base64Data { get; set; } = string.Empty;
}

// ---- WorkloadOrchestrator (Gaming & AI Resource Sharing Subsystem) ----

public sealed class GamingProfileDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>"local" | "remote"</summary>
    public string SessionType { get; set; } = "local";
    /// <summary>"manual" | "display" | "sunshine"</summary>
    public string TriggerProvider { get; set; } = "manual";
    public Dictionary<string, string> TriggerSettings { get; set; } = new();
    /// <summary>"playnite" | "steam" | "custom" | "none"</summary>
    public string LauncherProvider { get; set; } = "playnite";
    public Dictionary<string, string> LauncherSettings { get; set; } = new();
    /// <summary>"native" | "xoutput" | "sunshine" | "custom"</summary>
    public string InputProvider { get; set; } = "native";
    public Dictionary<string, string> InputSettings { get; set; } = new();
    /// <summary>"nyrna" | "win32native" | "none"</summary>
    public string SuspendProvider { get; set; } = "nyrna";
    public Dictionary<string, string> SuspendSettings { get; set; } = new();
    /// <summary>"release_gpu" | "maintain_light_services"</summary>
    public string AiResourcePolicy { get; set; } = "release_gpu";
    /// <summary>"off" | "always" | "auto"</summary>
    public string QuickResumePolicy { get; set; } = "auto";
    /// <summary>
    /// Explicit user policy: when the session ends WITHOUT the user asking to return to AI (remote
    /// grace period expiry, failed-entry rollback), reload the local AI runtime. Default false:
    /// Gaming Mode lifts the hard-off but never silently reloads a model/GPU resident after a game.
    /// An explicit "Return to AI" action always restores.
    /// </summary>
    public bool RestoreAiOnExit { get; set; }
    public int GracePeriodSeconds { get; set; } = 30;
    public List<string> StartupActions { get; set; } = new();
    public List<string> ShutdownActions { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public string? PreCommand { get; set; }
    public string? PostCommand { get; set; }
}

public sealed class GamingStatusDto
{
    /// <summary>State: "AiReady" | "EnteringGaming" | "GamingLocal" | "GamingRemote" | "ExitingGaming" | "GameSuspended" | "RestoringAi" | "Error"</summary>
    public string State { get; set; } = "AiReady";
    public bool IsGamingActive { get; set; }
    public string? ActiveProfileName { get; set; }
    public string? ActiveLauncher { get; set; }
    public string? ActiveInput { get; set; }
    public string? ActiveTrigger { get; set; }
    public int? SuspendedProcessId { get; set; }
    public string? SuspendedProcessName { get; set; }
    /// <summary>VRAM held by the suspended game, in MiB. 0 when nothing is suspended. Null when a game is
    /// suspended and the driver cannot report its GPU memory: clients must show "unknown", never a number.</summary>
    public long? SuspendedVramMib { get; set; }
    public long SystemTotalVramMib { get; set; }
    public long SystemFreeVramMib { get; set; }
    public List<string> SuspendedAiWorkloads { get; set; } = new();
    public bool CanQuickResume { get; set; }
    public string? Message { get; set; }
    public DateTime LastStateChangeUtc { get; set; } = DateTime.UtcNow;
}

public sealed class GamingProviderStatusDto
{
    public string Category { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Installed { get; set; }
    public bool Available { get; set; }
    public string? ExecutablePath { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class GamingActionRequest
{
    public string Action { get; set; } = string.Empty; // "enter", "exit", "suspend", "resume"
    public string? ProfileName { get; set; }
}

public sealed class RoleAutoAssignItem
{
    public string Role { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class RoleAutoAssignResponse
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<RoleAutoAssignItem> Assignments { get; set; } = new();
    /// <summary>Roles left unchanged, as "role: reason" (e.g. a role you assigned yourself is never replaced).</summary>
    public List<string> Skipped { get; set; } = new();
}
