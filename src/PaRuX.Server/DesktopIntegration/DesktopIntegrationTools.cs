using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaRuX.Server.DesktopIntegration;

/// <summary>
/// Puts connected desktop apps in front of the model as tools it can call, in the same shape as
/// PaRuX's knowledge tools.
///
/// Two properties matter more than the search quality:
///
/// <list type="bullet">
/// <item><b>The tools do not exist until something is connected.</b> An empty schema list means the
/// model is never told it can read the user's mail when it cannot, which is what stops it inventing
/// a mail it "found".</item>
/// <item><b>Results carry their source file.</b> The model is handed provenance with every hit, so
/// an answer can cite the note or the message rather than assert it.</item>
/// </list>
///
/// Matching is lexical and deliberately so: it runs on every turn that calls the tool, over files
/// read fresh from disk, and an embedding pass over a mailbox would cost more than the answer.
/// </summary>
public sealed class DesktopIntegrationTools
{
    public const string SearchToolName = "parux_desktop_search";
    public const string ReadToolName = "parux_desktop_read";

    private const int MaxResults = 12;
    private const int ExcerptCharacters = 600;

    private readonly DesktopIntegrationService _integrations;

    public DesktopIntegrationTools(DesktopIntegrationService integrations) => _integrations = integrations;

    public static bool IsBuiltInTool(string? name) =>
        string.Equals(name, SearchToolName, StringComparison.Ordinal)
        || string.Equals(name, ReadToolName, StringComparison.Ordinal);

    private bool HasConnectedSources() => _integrations.GetStatuses().Any(s => s.Connected);

    public JsonArray GetToolSchemas(IReadOnlyCollection<string>? allowedTools = null)
    {
        var schemas = new JsonArray();
        if (!HasConnectedSources()) return schemas;

        List<string> kinds = _integrations.GetStatuses()
            .Where(s => s.Connected)
            .Select(s => s.Kind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (ContextEngineService.ToolNameAllowed(SearchToolName, allowedTools))
        {
            schemas.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = SearchToolName,
                    ["description"] =
                        "Search the user's own apps on this PC that they have connected to PaRuX ("
                        + string.Join(", ", kinds)
                        + "). Everything is read locally and read-only. Results include the file each "
                        + "item came from - cite it rather than paraphrasing without attribution.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "What to look for. Words are matched against titles and content."
                            },
                            ["kind"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Optional filter: " + string.Join(" | ", kinds)
                            }
                        },
                        ["required"] = new JsonArray { "query" }
                    }
                }
            });
        }

        if (ContextEngineService.ToolNameAllowed(ReadToolName, allowedTools))
        {
            schemas.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ReadToolName,
                    ["description"] =
                        "Read one item in full from a connected app on this PC, using the exact "
                        + "location returned by " + SearchToolName + ".",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["location"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The location value from a search result."
                            }
                        },
                        ["required"] = new JsonArray { "location" }
                    }
                }
            });
        }

        return schemas;
    }

    public Task<string> CallAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        if (!IsBuiltInTool(toolName)) throw new InvalidOperationException("Unknown PaRuX desktop tool.");

        JsonElement args;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Task.FromResult("Desktop tool failed: the arguments were not valid JSON.");
        }

        ct.ThrowIfCancellationRequested();

        return Task.FromResult(toolName == SearchToolName
            ? Search(String(args, "query"), String(args, "kind"))
            : ReadOne(String(args, "location")));
    }

    private string Search(string query, string? kind)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Desktop tool failed: a query is required.";
        if (!HasConnectedSources()) return "No desktop apps are connected to PaRuX.";

        string[] terms = query
            .Split(new[] { ' ', '\t', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 1)
            .ToArray();
        if (terms.Length == 0) terms = new[] { query.Trim() };

        List<DesktopItem> items = _integrations.ReadAllConnected(kind);
        var scored = items
            .Select(item => (Item: item, Score: Score(item, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.Timestamp ?? DateTimeOffset.MinValue)
            .Take(MaxResults)
            .ToList();

        if (scored.Count == 0)
            return "Nothing in the connected apps matched \"" + query + "\".";

        var output = new StringBuilder();
        output.AppendLine(scored.Count + " match" + (scored.Count == 1 ? string.Empty : "es") + ":");
        foreach ((DesktopItem item, int _) in scored)
        {
            output.AppendLine();
            output.AppendLine("### " + item.Title);
            output.AppendLine("- source: " + item.SourceId + " (" + item.Kind + ")");
            if (!string.IsNullOrWhiteSpace(item.Subtitle)) output.AppendLine("- " + item.Subtitle);
            if (item.Timestamp.HasValue)
                output.AppendLine("- date: " + item.Timestamp.Value.ToString("u"));
            output.AppendLine("- location: " + item.Location);
            output.AppendLine(Excerpt(item.Body, terms));
        }
        return output.ToString();
    }

    private string ReadOne(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "Desktop tool failed: a location is required.";

        // Matched against what a connected source actually returned rather than opened directly:
        // that is what keeps this tool from becoming a way to read an arbitrary file by asking.
        DesktopItem? item = _integrations.ReadAllConnected()
            .FirstOrDefault(i => string.Equals(i.Location, location, StringComparison.Ordinal)
                              || string.Equals(i.Id, location, StringComparison.Ordinal));

        if (item == null)
            return "That location is not in any connected app. Search first and use the location it returns.";

        var output = new StringBuilder();
        output.AppendLine("# " + item.Title);
        if (!string.IsNullOrWhiteSpace(item.Subtitle)) output.AppendLine(item.Subtitle);
        if (item.Timestamp.HasValue) output.AppendLine(item.Timestamp.Value.ToString("u"));
        output.AppendLine("source: " + item.Location);
        output.AppendLine();
        output.Append(item.Body);
        return output.ToString();
    }

    /// <summary>Title matches weigh more than body matches: someone searching "invoice" wants the
    /// note called Invoices before the one that mentions the word once.</summary>
    internal static int Score(DesktopItem item, IReadOnlyList<string> terms)
    {
        int score = 0;
        foreach (string term in terms)
        {
            if (item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 5;
            if (!string.IsNullOrEmpty(item.Subtitle)
                && item.Subtitle.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (item.Body.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 1;
        }
        return score;
    }

    /// <summary>An excerpt around the first hit, not the first N characters: the top of a mail is
    /// usually a greeting, and the reason it matched is further down.</summary>
    internal static string Excerpt(string body, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        int at = -1;
        foreach (string term in terms)
        {
            int found = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (at < 0 || found < at)) at = found;
        }
        if (at < 0) at = 0;

        int start = Math.Max(0, at - ExcerptCharacters / 3);
        int length = Math.Min(ExcerptCharacters, body.Length - start);
        string excerpt = body.Substring(start, length).Trim();

        if (start > 0) excerpt = "…" + excerpt;
        if (start + length < body.Length) excerpt += "…";
        return excerpt;
    }

    private static string String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
