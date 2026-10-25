using System.Collections.Generic;

namespace PaRuX.Services
{
    /// <summary>
    /// Fills in the boilerplate for well-known MCP reference servers (exact npm package name,
    /// launch flags) so adding one doesn't require the user to already know or type that syntax
    /// by hand. A path/token placeholder (wrapped in angle brackets) is left in Args when a server
    /// needs one; the user still edits that one value, but nothing else. Manual entry of all three
    /// fields remains fully supported for any server not listed here.
    /// </summary>
    public sealed record McpServerPreset(string DisplayName, string Name, string Command, string Args);

    public static class McpServerPresetCatalog
    {
        public static IReadOnlyList<McpServerPreset> All { get; } = new List<McpServerPreset>
        {
            new("Filesystem (read/write a folder)", "filesystem", "npx",
                "-y @modelcontextprotocol/server-filesystem <path-to-folder>"),
            new("Git (a local repository)", "git", "npx",
                "-y @modelcontextprotocol/server-git --repository <path-to-repo>"),
            new("SQLite (a local database file)", "sqlite", "npx",
                "-y @modelcontextprotocol/server-sqlite <path-to-database.db>"),
            new("Fetch (retrieve and read web pages)", "fetch", "npx",
                "-y @modelcontextprotocol/server-fetch"),
            new("Memory (a persistent knowledge graph)", "memory", "npx",
                "-y @modelcontextprotocol/server-memory"),
            new("Sequential thinking (structured reasoning steps)", "sequential-thinking", "npx",
                "-y @modelcontextprotocol/server-sequential-thinking"),
        };
    }
}
