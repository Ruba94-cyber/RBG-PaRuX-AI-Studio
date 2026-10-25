using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Paired Android may author a small declarative prompt/routing skill, but it may not use that
/// generic manifest write surface to grant itself desktop capabilities. Core discards caller ids,
/// tools, workflows, dependencies and every privileged permission. Explicit network use is the
/// only opt-in capability preserved because Android's builder exposes that choice directly and it
/// still flows through the normal InternetPolicy gate.
/// </summary>
internal static class MobileSkillCreationPolicy
{
    internal static SkillManifest Sanitize(SkillManifest? input)
    {
        if (input == null) throw new InvalidDataException("A skill manifest is required.");
        string display = Bounded(input.DisplayName, 80);
        if (display.Length == 0) throw new InvalidDataException("A skill display name is required.");
        string description = Bounded(input.Description, 2000);
        string command = Bounded(input.SlashCommand, 64);
        string preferredRole = Bounded(input.PreferredModelRole, 80);
        if (preferredRole.Length == 0) preferredRole = "MAIN_LLM";
        bool network = string.Equals(input.InternetPolicy, "required", StringComparison.OrdinalIgnoreCase);

        return new SkillManifest
        {
            Id = string.Empty, // force a fresh server-owned id; remote callers cannot overwrite another skill
            Name = Slug(Bounded(input.Name, 80), display),
            DisplayName = display,
            Description = description,
            Category = "Custom",
            Version = "1.0.0",
            Author = "PaRuX paired device",
            Source = "user",
            Enabled = true,
            Platforms = new List<string> { "Windows", "Android" },
            PreferredModelRole = preferredRole,
            AllowedModelRoles = new List<string>(),
            RequiredCapabilities = new List<string>(),
            AllowedTools = new List<string>(),
            InternetPolicy = network ? "required" : "off",
            MemoryPolicy = "optional",
            RagPolicy = "optional",
            SlashCommand = command,
            TriggerExamples = new List<string>(),
            Workflow = new List<string>(),
            Dependencies = new List<string>(),
            Permissions = new SkillPermissions { Network = network },
            TimeoutSeconds = 120,
            RetryPolicy = "none",
            FallbackSkillId = string.Empty,
            ConfirmationPolicy = "never",
            SubSkillIds = new List<string>(),
            Parameters = new List<SkillParameterDto>()
        };
    }

    private static string Bounded(string? value, int max)
    {
        string s = (value ?? string.Empty).Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static string Slug(string requested, string fallback)
    {
        string source = requested.Length == 0 ? fallback : requested;
        string slug = new(source.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length == 0) slug = "mobile-skill";
        return slug.Length <= 80 ? slug : slug[..80].TrimEnd('-');
    }
}
