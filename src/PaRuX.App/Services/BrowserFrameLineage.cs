using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// Frame ancestry for Fetch.requestPaused attribution. Fetch events carry only
    /// a frameId, so the document chain is rebuilt from Page.getFrameTree and the
    /// Page.frame* / navigatedWithinDocument events. A frame or ancestor whose URL
    /// is unknown yields null, which the initiator policy treats as a denial.
    /// </summary>
    public sealed class BrowserFrameLineage
    {
        private const int MaximumDepth = 64;
        private readonly object _guard = new();
        private readonly Dictionary<string, Frame> _frames = new(StringComparer.Ordinal);
        private string? _topFrameId;

        private sealed record Frame(string? ParentId, string? Url);

        /// <summary>Id of the top-level frame once seeded or first navigated; null until then.</summary>
        public string? TopFrameId
        {
            get { lock (_guard) return _topFrameId; }
        }

        public void Clear()
        {
            lock (_guard)
            {
                _frames.Clear();
                _topFrameId = null;
            }
        }

        /// <summary>Applies one CDP Page.* event. Returns false for events it does not track.</summary>
        public bool Apply(string? method, JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object) return false;
            switch (method)
            {
                case "Page.frameAttached":
                {
                    string? id = ReadString(parameters, "frameId");
                    if (id is null) return true;
                    lock (_guard) Upsert(id, ReadString(parameters, "parentFrameId"), null, replaceUrl: false);
                    return true;
                }
                case "Page.frameNavigated":
                {
                    if (!parameters.TryGetProperty("frame", out JsonElement frame) || frame.ValueKind != JsonValueKind.Object) return true;
                    string? id = ReadString(frame, "id");
                    if (id is null) return true;
                    string? parentId = ReadString(frame, "parentId");
                    lock (_guard)
                    {
                        Upsert(id, parentId, ReadString(frame, "url"), replaceUrl: true);
                        if (parentId is null) _topFrameId = id;
                    }
                    return true;
                }
                case "Page.navigatedWithinDocument":
                {
                    string? id = ReadString(parameters, "frameId");
                    if (id is null) return true;
                    lock (_guard) Upsert(id, null, ReadString(parameters, "url"), replaceUrl: true);
                    return true;
                }
                case "Page.frameDetached":
                {
                    string? id = ReadString(parameters, "frameId");
                    if (id is null) return true;
                    lock (_guard) RemoveSubtree(id);
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>Seeds the lineage from a Page.getFrameTree result so the first navigation is attributable.</summary>
        public void SeedFromFrameTree(JsonElement getFrameTreeResult)
        {
            if (getFrameTreeResult.ValueKind != JsonValueKind.Object
                || !getFrameTreeResult.TryGetProperty("frameTree", out JsonElement tree)
                || tree.ValueKind != JsonValueKind.Object)
                return;
            lock (_guard) SeedNode(tree, parentId: null, depth: 0);
        }

        /// <summary>
        /// Returns the document URLs from the frame up to the top frame, nearest first.
        /// A missing frame, a missing URL or a missing ancestor contributes null.
        /// </summary>
        public IReadOnlyList<string?> ChainFrom(string? frameId)
        {
            var chain = new List<string?>();
            lock (_guard)
            {
                string? current = frameId;
                for (int depth = 0; depth < MaximumDepth; depth++)
                {
                    if (string.IsNullOrEmpty(current) || !_frames.TryGetValue(current, out Frame? frame))
                    {
                        chain.Add(null);
                        return chain;
                    }
                    chain.Add(frame.Url);
                    if (string.IsNullOrEmpty(frame.ParentId)) return chain;
                    current = frame.ParentId;
                }
            }
            chain.Add(null); // cycle or excessive depth: unknown ancestry
            return chain;
        }

        // A navigation event replaces the URL outright: a missing URL becomes unknown
        // (fail closed), never the stale previous origin.
        private void Upsert(string id, string? parentId, string? url, bool replaceUrl)
        {
            _frames.TryGetValue(id, out Frame? existing);
            string? effectiveParent = parentId ?? existing?.ParentId;
            string? effectiveUrl = replaceUrl ? url : existing?.Url;
            _frames[id] = new Frame(effectiveParent, effectiveUrl);
        }

        private void SeedNode(JsonElement node, string? parentId, int depth)
        {
            if (depth > MaximumDepth || !node.TryGetProperty("frame", out JsonElement frame)
                || frame.ValueKind != JsonValueKind.Object) return;
            string? id = ReadString(frame, "id");
            if (id is null) return;
            Upsert(id, ReadString(frame, "parentId") ?? parentId, ReadString(frame, "url"), replaceUrl: true);
            if (depth == 0) _topFrameId = id;
            if (!node.TryGetProperty("childFrames", out JsonElement children) || children.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement child in children.EnumerateArray())
                SeedNode(child, id, depth + 1);
        }

        private void RemoveSubtree(string id)
        {
            var pending = new Stack<string>();
            pending.Push(id);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                _frames.Remove(current);
                foreach (KeyValuePair<string, Frame> entry in _frames)
                {
                    if (string.Equals(entry.Value.ParentId, current, StringComparison.Ordinal))
                        pending.Push(entry.Key);
                }
            }
        }

        private static string? ReadString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            string? text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
