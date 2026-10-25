using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Selects a prompt-shaping skill for a chat message: FORCED (explicit id) beats SLASH
/// (leading "/command") beats AUTO (keyword match against each enabled skill's TriggerExamples).
/// Execution routes such as the native Windows Coding Agent are deliberately not skills: granting
/// a manifest name must never create filesystem/shell authority inside an ordinary chat completion.
/// </summary>
public sealed class SkillRouter
{
    private const string RetiredCodingSkillId = "coding";
    private readonly SkillRegistryService _registry;

    public SkillRouter(SkillRegistryService registry) => _registry = registry;

    public SkillRouteResult Route(string message, string? forcedSkillId)
    {
        List<SkillManifest> skills = _registry.List()
            .Where(s => s.Enabled && !string.Equals(s.Id, RetiredCodingSkillId, StringComparison.Ordinal))
            .ToList();

        if (string.Equals(forcedSkillId, RetiredCodingSkillId, StringComparison.Ordinal))
        {
            return new SkillRouteResult
            {
                Mode = "none",
                Reason = "Coding is a native execution route, not a prompt-only skill. Start it from the PaRuX Windows /code route so repository authority, sandboxing and verification are enforced."
            };
        }

        if (!string.IsNullOrWhiteSpace(forcedSkillId))
        {
            SkillManifest? forced = skills.FirstOrDefault(s => s.Id == forcedSkillId);
            if (forced == null)
                return new SkillRouteResult { Mode = "none", Reason = "Forced skill '" + forcedSkillId + "' is unknown or disabled." };
            return new SkillRouteResult { Skill = forced, Mode = "forced", Reason = "User forced this skill." };
        }

        string trimmed = (message ?? string.Empty).TrimStart();
        if (trimmed.StartsWith('/'))
        {
            int end = trimmed.IndexOfAny(new[] { ' ', '\n', '\t' });
            string command = end < 0 ? trimmed : trimmed[..end];
            SkillManifest? bySlash = skills.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.SlashCommand) && string.Equals(s.SlashCommand, command, StringComparison.OrdinalIgnoreCase));
            if (bySlash != null)
                return new SkillRouteResult { Skill = bySlash, Mode = "slash", Reason = "Matched slash command " + command + "." };
        }

        string lowered = (message ?? string.Empty).ToLowerInvariant();
        SkillManifest? best = null;
        string? matchedPhrase = null;
        foreach (SkillManifest skill in skills)
        {
            if (skill.Id == "general") continue;
            foreach (string trigger in skill.TriggerExamples)
            {
                if (string.IsNullOrWhiteSpace(trigger)) continue;
                if (lowered.Contains(trigger.ToLowerInvariant()))
                {
                    best = skill;
                    matchedPhrase = trigger;
                    break;
                }
            }
            if (best != null) break;
        }

        if (best != null)
            return new SkillRouteResult { Skill = best, Mode = "auto", Reason = "Matched trigger phrase \"" + matchedPhrase + "\"." };

        SkillManifest? general = skills.FirstOrDefault(s => s.Id == "general");
        return new SkillRouteResult { Skill = general, Mode = general != null ? "auto" : "none", Reason = "No specific intent matched; defaulting to General." };
    }
}
