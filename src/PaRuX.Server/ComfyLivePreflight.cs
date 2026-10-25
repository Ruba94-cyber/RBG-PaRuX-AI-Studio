using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>A media job that cannot be submitted, or that ComfyUI refused, carrying one typed reason.</summary>
internal sealed class MediaDependencyException : InvalidOperationException
{
    public MediaDependencyException(MediaDependencyError error)
        : base(error.Message)
    {
        Error = error;
    }

    public MediaDependencyError Error { get; }
}

/// <summary>What the running ComfyUI offers: node classes, and the options of every combo input (file names,
/// CLIP types, ...). Read live from /object_info, so a file added or removed since ComfyUI started is seen.</summary>
internal sealed record ComfyNodeCatalog(
    IReadOnlySet<string> NodeClasses,
    IReadOnlyDictionary<string, IReadOnlySet<string>> ComboOptions,
    string? ComfyUiVersion);

/// <summary>
/// Checks a built workflow against the running ComfyUI before it is queued: every node class exists, and every
/// combo value the graph sets (checkpoint, text encoder, CLIP type, VAE) is one the server offers. A failure
/// becomes a typed <see cref="MediaDependencyException"/> with one next action, instead of a raw node error
/// after the job has already been queued. Fails closed: if ComfyUI cannot answer in time, nothing is submitted.
/// </summary>
internal static class ComfyLivePreflight
{
    public const int MaxCatalogBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly record struct CatalogCacheKey(string BaseAddress, string ProcessIdentity);
    private readonly record struct CachedCatalogRead(
        ComfyNodeCatalog Catalog,
        bool WasCached,
        Lazy<Task<ComfyNodeCatalog>>? CacheEntry);

    private static readonly ConcurrentDictionary<CatalogCacheKey, Lazy<Task<ComfyNodeCatalog>>> CatalogCache = new();

    private static readonly Regex NodeErrorDetails = new(
        @"^\s*(?<input>[A-Za-z0-9_]+)\s*:\s*'(?<value>[^']*)'",
        RegexOptions.CultureInvariant);

    private static readonly Regex ComfyVersionPrefix = new(@"^v?(?<v>\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant);

    /// <summary>Reads the live node catalogue and, when available, the ComfyUI version marker.</summary>
    public static async Task<ComfyNodeCatalog> ReadCatalogAsync(HttpClient http, Uri baseAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseAddress);
        string objectInfo = await GetBoundedAsync(http, baseAddress, "object_info", ct).ConfigureAwait(false);
        string? version = null;
        try
        {
            version = ReadVersion(await GetBoundedAsync(http, baseAddress, "system_stats", ct).ConfigureAwait(false));
        }
        catch (MediaDependencyException)
        {
            // The version marker is advisory. The node catalogue above is what decides whether a job can run.
        }
        return ParseCatalog(objectInfo, version);
    }

    /// <summary>Parses an /object_info body. Combo inputs are the ones whose first spec element is an array of options.</summary>
    public static ComfyNodeCatalog ParseCatalog(string objectInfoJson, string? comfyUiVersion)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(objectInfoJson);
        }
        catch (JsonException exception)
        {
            throw Unreachable("object_info was not valid JSON (" + exception.Message + ")");
        }
        if (root is not JsonObject classes) throw Unreachable("object_info did not return a JSON object");

        var nodeClasses = new HashSet<string>(StringComparer.Ordinal);
        var combos = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> classEntry in classes)
        {
            nodeClasses.Add(classEntry.Key);
            if (classEntry.Value is not JsonObject node || node["input"] is not JsonObject inputs) continue;
            foreach (string section in new[] { "required", "optional" })
            {
                if (inputs[section] is not JsonObject fields) continue;
                foreach (KeyValuePair<string, JsonNode?> field in fields)
                {
                    if (field.Value is not JsonArray spec || spec.Count == 0 || spec[0] is not JsonArray options) continue;
                    var values = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonNode? option in options)
                    {
                        if (option is JsonValue value && value.TryGetValue(out string? text) && text != null) values.Add(text);
                    }
                    combos[classEntry.Key + "." + field.Key] = values;
                }
            }
        }
        return new ComfyNodeCatalog(nodeClasses, combos, comfyUiVersion);
    }

    /// <summary>Returns every problem with the workflow against this catalogue. An empty list means it is safe to queue.</summary>
    public static IReadOnlyList<MediaDependencyError> Validate(JsonObject prompt, ComfyNodeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(catalog);
        var errors = new List<MediaDependencyError>();
        foreach (KeyValuePair<string, JsonNode?> nodeEntry in prompt)
        {
            if (nodeEntry.Value is not JsonObject node) continue;
            if (node["class_type"] is not JsonValue classValue || !classValue.TryGetValue(out string? classType) || classType == null) continue;
            if (!catalog.NodeClasses.Contains(classType))
            {
                errors.Add(NodeMissing(classType));
                continue;
            }
            if (node["inputs"] is not JsonObject inputs) continue;
            foreach (KeyValuePair<string, JsonNode?> input in inputs)
            {
                // Links such as ["3", 0] are arrays and are skipped; only literal combo values are checked.
                if (input.Value is not JsonValue literal || !literal.TryGetValue(out string? text) || text == null) continue;
                if (!catalog.ComboOptions.TryGetValue(classType + "." + input.Key, out IReadOnlySet<string>? offered)) continue;
                if (!offered.Contains(text)) errors.Add(ValueNotOffered(classType, input.Key, text));
            }
        }
        return errors;
    }

    /// <summary>Reads the live catalogue, validates the graph, and throws the first problem as a typed error.
    /// Accepts a full ComfyUI API body (graph under "prompt") or the bare graph.</summary>
    public static async Task EnsureSubmittableAsync(HttpClient http, Uri baseAddress, JsonObject body, CancellationToken ct, string? requiredComfyUiVersion = null, string? processIdentity = null)
    {
        JsonObject graph = body["prompt"] as JsonObject ?? body;
        CatalogCacheKey? cacheKey = string.IsNullOrWhiteSpace(processIdentity)
            ? null
            : new CatalogCacheKey(baseAddress.AbsoluteUri, processIdentity);
        CachedCatalogRead catalogRead = cacheKey is { } key
            ? await ReadCachedCatalogAsync(http, baseAddress, key, ct).ConfigureAwait(false)
            : new CachedCatalogRead(await ReadCatalogAsync(http, baseAddress, ct).ConfigureAwait(false), false, null);
        ComfyNodeCatalog catalog = catalogRead.Catalog;

        IReadOnlyList<MediaDependencyError> errors = ValidateForSubmission(graph, catalog, requiredComfyUiVersion);
        if (errors.Count > 0 && catalogRead.WasCached && cacheKey is { } staleKey && SuggestsCatalogStaleness(errors[0].Reason))
        {
            // Files and custom nodes can change while ComfyUI stays up. A cached catalogue that
            // rejects a workflow may be stale, so refresh once and validate again before refusing it.
            InvalidateCachedCatalog(staleKey, catalogRead.CacheEntry);
            catalog = (await ReadCachedCatalogAsync(http, baseAddress, staleKey, ct).ConfigureAwait(false)).Catalog;
            errors = ValidateForSubmission(graph, catalog, requiredComfyUiVersion);
        }
        ThrowFirstValidationError(errors);
    }

    private static async Task<CachedCatalogRead> ReadCachedCatalogAsync(
        HttpClient http, Uri baseAddress, CatalogCacheKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // A new managed process at the same endpoint is a new ComfyUI session. Remove its
        // predecessors so successful catalogues do not accumulate across runtime restarts.
        foreach (CatalogCacheKey previous in CatalogCache.Keys)
        {
            if (string.Equals(previous.BaseAddress, key.BaseAddress, StringComparison.Ordinal)
                && !string.Equals(previous.ProcessIdentity, key.ProcessIdentity, StringComparison.Ordinal))
                CatalogCache.TryRemove(previous, out _);
        }

        var candidate = new Lazy<Task<ComfyNodeCatalog>>(
            () => ReadCatalogAsync(http, baseAddress, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<ComfyNodeCatalog>> entry = CatalogCache.GetOrAdd(key, candidate);
        bool wasCached = !ReferenceEquals(entry, candidate);
        try
        {
            // The shared bounded read is independent of one caller's cancellation. Each caller
            // can still stop waiting immediately without cancelling the catalogue for its peers.
            Task<ComfyNodeCatalog> sharedRead = entry.Value;
            _ = sharedRead.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted || completed.IsCanceled)
                    {
                        _ = completed.Exception;
                        RemoveCachedCatalogEntry(key, entry);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            ComfyNodeCatalog catalog = await sharedRead.WaitAsync(ct).ConfigureAwait(false);
            return new CachedCatalogRead(catalog, wasCached, entry);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The shared catalogue load is still useful to other callers and must stay cached.
            throw;
        }
        catch
        {
            RemoveCachedCatalogEntry(key, entry);
            throw;
        }
    }

    private static void InvalidateCachedCatalog(CatalogCacheKey key, Lazy<Task<ComfyNodeCatalog>>? expectedEntry)
    {
        if (expectedEntry is null) return;
        RemoveCachedCatalogEntry(key, expectedEntry);
    }

    private static void RemoveCachedCatalogEntry(CatalogCacheKey key, Lazy<Task<ComfyNodeCatalog>> entry)
    {
        ((ICollection<KeyValuePair<CatalogCacheKey, Lazy<Task<ComfyNodeCatalog>>>>)CatalogCache)
            .Remove(new KeyValuePair<CatalogCacheKey, Lazy<Task<ComfyNodeCatalog>>>(key, entry));
    }

    private static bool SuggestsCatalogStaleness(string reason) =>
        reason is "node_missing" or "component_missing" or "value_not_offered";

    private static IReadOnlyList<MediaDependencyError> ValidateForSubmission(
        JsonObject graph, ComfyNodeCatalog catalog, string? requiredComfyUiVersion)
    {
        if (requiredComfyUiVersion != null) RequireComfyUiVersion(catalog.ComfyUiVersion, requiredComfyUiVersion);
        return Validate(graph, catalog);
    }

    private static void ThrowFirstValidationError(IReadOnlyList<MediaDependencyError> errors)
    {
        if (errors.Count == 0) return;
        MediaDependencyError first = errors[0];
        if (errors.Count > 1)
            first.Message += " (" + (errors.Count - 1).ToString(CultureInfo.InvariantCulture) + " more problem(s) in this workflow.)";
        throw new MediaDependencyException(first);
    }

    /// <summary>Refuses a running ComfyUI older than the minimum a family declares. An unreadable version is refused too:
    /// a minimum that cannot be checked is not known to be met, so the job fails closed.</summary>
    public static void RequireComfyUiVersion(string? runningVersion, string requiredVersion)
    {
        Version needed = ParseComfyVersion(requiredVersion)
            ?? throw new InvalidDataException("The required ComfyUI version \"" + requiredVersion + "\" is not a dotted version.");
        Version? running = ParseComfyVersion(runningVersion);
        if (running != null && running >= needed) return;

        string message = running == null
            ? "PaRuX could not read the running ComfyUI version, and this workflow needs ComfyUI " + requiredVersion + " or newer. No job was submitted."
            : "The running ComfyUI is " + runningVersion!.Trim() + ", and this workflow needs ComfyUI " + requiredVersion + " or newer. No job was submitted.";
        throw new MediaDependencyException(new MediaDependencyError
        {
            Reason = "comfyui_too_old",
            UpdateRequired = true,
            NextAction = "Update ComfyUI from Runtime Manager to " + requiredVersion + " or newer.",
            Message = message
        });
    }

    /// <summary>Reads the numeric part of a ComfyUI version marker such as "0.37.0" or "v0.38.0-dev"; null when there is none.</summary>
    private static Version? ParseComfyVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Match match = ComfyVersionPrefix.Match(text.Trim());
        return match.Success && Version.TryParse(match.Groups["v"].Value, out Version? version) ? version : null;
    }

    /// <summary>Maps ComfyUI's own node_errors (returned by POST /prompt or /history) onto the same typed reasons.
    /// Unrecognised error kinds are skipped, so the caller can fall back to the generic message.</summary>
    public static IReadOnlyList<MediaDependencyError> MapNodeErrors(JsonElement nodeErrors)
    {
        var result = new List<MediaDependencyError>();
        if (nodeErrors.ValueKind != JsonValueKind.Object) return result;
        foreach (JsonProperty nodeEntry in nodeErrors.EnumerateObject())
        {
            if (nodeEntry.Value.ValueKind != JsonValueKind.Object) continue;
            string classType = nodeEntry.Value.TryGetProperty("class_type", out JsonElement classElement)
                && classElement.ValueKind == JsonValueKind.String ? classElement.GetString() ?? string.Empty : string.Empty;
            if (!nodeEntry.Value.TryGetProperty("errors", out JsonElement errors) || errors.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement error in errors.EnumerateArray())
            {
                string kind = ReadString(error, "type");
                string details = ReadString(error, "details");
                if (kind == "value_not_in_list")
                {
                    Match match = NodeErrorDetails.Match(details);
                    if (!match.Success) continue;
                    result.Add(ValueNotOffered(classType, match.Groups["input"].Value, match.Groups["value"].Value));
                }
                else if (kind == "required_input_missing")
                {
                    result.Add(new MediaDependencyError
                    {
                        Reason = "input_missing",
                        Message = "The ComfyUI node " + classType + " is missing a required input" + (details.Length == 0 ? string.Empty : " (" + details + ")") + ".",
                        NextAction = "Check the custom workflow for " + classType + " or use a built-in workflow.",
                        UpdateRequired = false
                    });
                }
            }
        }
        return result;
    }

    /// <summary>Throws a typed <see cref="MediaDependencyException"/> when a ComfyUI refusal body (HTTP 400 from
    /// POST /prompt, or a queued response with node_errors) names a problem PaRuX can explain. Otherwise returns,
    /// and the caller keeps its generic message.</summary>
    public static void ThrowTypedRefusal(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.Contains("node_errors", StringComparison.Ordinal)) return;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("node_errors", out JsonElement nodeErrors)) return;
            IReadOnlyList<MediaDependencyError> typed = MapNodeErrors(nodeErrors);
            if (typed.Count > 0) throw new MediaDependencyException(typed[0]);
        }
    }

    /// <summary>Typed error for a model file that a ComfyUI loader needs but the folder does not contain.
    /// The registry supplies the folder, repository and size when PaRuX knows them.</summary>
    public static MediaDependencyError MissingFile(string folder, string fileName)
    {
        MediaCompanionRegistry registry = MediaCompanionRegistry.Shipped;
        var found = registry.FindCompanionByFileName(folder, fileName);
        string? repository = found?.Companion.Source.Repository;
        long? size = found?.Companion.Source.SizeBytes;
        string role = found?.Companion.Role ?? (folder == "diffusion_models" ? "main model" : "component");
        string nextAction = repository != null
            ? "Download " + fileName + " from " + repository + " in the Model Hub, or copy it into the ComfyUI " + folder + " folder."
            : "Copy " + fileName + " into the ComfyUI " + folder + " folder. PaRuX has no public source registered for it.";
        return new MediaDependencyError
        {
            Reason = "component_missing",
            Role = role,
            ExpectedFileName = fileName,
            ComfyFolder = folder,
            Repository = repository,
            SizeBytes = size,
            NextAction = nextAction,
            Message = "The workflow needs " + fileName + " (" + role + "), but it is not in the ComfyUI " + folder + " folder. " + nextAction
        };
    }

    /// <summary>Typed error for a file named only in ComfyUI's raw text. The folder comes from the registry when the
    /// file is a known companion; otherwise the file is treated as a main checkpoint.</summary>
    public static MediaDependencyError MissingFileByName(string fileName)
    {
        string folder = MediaCompanionRegistry.Shipped.Families
            .SelectMany(family => family.Companions)
            .FirstOrDefault(companion => string.Equals(companion.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.Folder ?? "diffusion_models";
        return MissingFile(folder, fileName);
    }

    private static MediaDependencyError ValueNotOffered(string classType, string input, string value)
    {
        string key = classType + "." + input;
        if (MediaCompanionRegistry.LoaderFolders.TryGetValue(key, out string? folder))
            return MissingFile(folder, value);

        return new MediaDependencyError
        {
            Reason = "value_not_offered",
            UpdateRequired = true,
            NextAction = "Update ComfyUI from Runtime Manager: this ComfyUI does not offer '" + value + "' for " + key + ".",
            Message = "The running ComfyUI does not offer '" + value + "' for " + key + ". No job was submitted."
        };
    }

    private static MediaDependencyError NodeMissing(string classType) => new()
    {
        Reason = "node_missing",
        UpdateRequired = true,
        NextAction = "Update ComfyUI from Runtime Manager: the node " + classType + " is not available in the running ComfyUI.",
        Message = "The workflow uses the ComfyUI node " + classType + ", which the running ComfyUI does not provide. No job was submitted."
    };

    private static MediaDependencyException Unreachable(string detail) => new(new MediaDependencyError
    {
        Reason = "comfyui_unreachable",
        NextAction = "Start the media runtime from Runtime Manager, then try again.",
        Message = "ComfyUI did not answer the preflight check (" + detail + "). No job was submitted."
    });

    private static async Task<string> GetBoundedAsync(HttpClient http, Uri baseAddress, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(baseAddress, path), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Unreachable(path + " answered HTTP " + (int)response.StatusCode);
            if (response.Content.Headers.ContentLength is long declared && declared > MaxCatalogBytes)
                throw Unreachable(path + " is larger than the preflight limit");

            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxCatalogBytes) throw Unreachable(path + " is larger than the preflight limit");
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unreachable(path + " did not answer within " + ProbeTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds");
        }
        catch (HttpRequestException exception)
        {
            throw Unreachable(path + " refused the connection (" + exception.Message + ")");
        }
    }

    private static string? ReadVersion(string systemStatsJson)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(systemStatsJson);
            if (root?["system"] is JsonObject system && system["comfyui_version"] is JsonValue version
                && version.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                return text;
        }
        catch (JsonException) { }
        return null;
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
