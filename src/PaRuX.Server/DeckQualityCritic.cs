using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Deterministic preflight/repair pass for generated presentation outlines. It protects the OpenXML
/// renderer from common LLM layout failures (overlong titles, paragraph-sized bullets, duplicated
/// hierarchy and overloaded slides) without requiring PowerPoint, a vision model, or cloud AI.
/// Rendering-based visual acceptance remains an external Windows quality gate.
/// </summary>
public static class DeckQualityCritic
{
    public const int MaximumTitleCharacters = 72;
    public const int MaximumBulletCharacters = 120;
    public const int MaximumBulletsPerSlide = 6;
    public const int MaximumBodyCharactersPerSlide = 520;

    public enum Severity { Info, Warning, Error }

    public sealed record Finding(int SlideNumber, string Code, Severity Severity, string Message);

    public sealed record Review(
        IReadOnlyList<Finding> Findings,
        int ErrorCount,
        int WarningCount,
        double QualityScore,
        bool RequiresRepair);

    public static Review Analyze(IReadOnlyList<PresentationSlideDto> slides)
    {
        ArgumentNullException.ThrowIfNull(slides);
        var findings = new List<Finding>();
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (slides.Count == 0)
            findings.Add(new Finding(0, "deck.empty", Severity.Error, "The deck contains no slides."));

        for (int index = 0; index < slides.Count; index++)
        {
            PresentationSlideDto slide = slides[index];
            int number = index + 1;
            string title = NormalizeWhitespace(slide.Title);
            if (title.Length == 0)
                findings.Add(new Finding(number, "title.missing", Severity.Error, "Slide title is missing."));
            else
            {
                if (title.Length > MaximumTitleCharacters)
                    findings.Add(new Finding(number, "title.long", Severity.Warning, "Title exceeds the layout-safe length."));
                if (!titles.Add(title))
                    findings.Add(new Finding(number, "title.duplicate", Severity.Warning, "Title duplicates another slide."));
            }

            List<string> bullets = slide.Bullets ?? new List<string>();
            if (index > 0 && bullets.Count == 0)
                findings.Add(new Finding(number, "body.empty", Severity.Warning, "Content slide has no body bullets."));
            if (bullets.Count > MaximumBulletsPerSlide)
                findings.Add(new Finding(number, "body.too-many-bullets", Severity.Warning, "Slide contains too many bullets for the default layout."));

            int bodyCharacters = 0;
            var normalizedBullets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in bullets)
            {
                string bullet = NormalizeWhitespace(raw);
                bodyCharacters += bullet.Length;
                if (bullet.Length == 0)
                    findings.Add(new Finding(number, "bullet.empty", Severity.Info, "Empty bullet can be removed."));
                else if (bullet.Length > MaximumBulletCharacters)
                    findings.Add(new Finding(number, "bullet.long", Severity.Warning, "Bullet is too long for scannable slide hierarchy."));
                if (bullet.Length > 0 && !normalizedBullets.Add(bullet))
                    findings.Add(new Finding(number, "bullet.duplicate", Severity.Info, "Duplicate bullet can be removed."));
            }

            if (bodyCharacters > MaximumBodyCharactersPerSlide)
                findings.Add(new Finding(number, "body.overloaded", Severity.Error, "Body text exceeds the safe density for the default layout."));
        }

        int errors = findings.Count(finding => finding.Severity == Severity.Error);
        int warnings = findings.Count(finding => finding.Severity == Severity.Warning);
        double penalty = errors * 18d + warnings * 7d + findings.Count(finding => finding.Severity == Severity.Info) * 2d;
        double score = Math.Clamp(100d - penalty, 0d, 100d);
        return new Review(findings, errors, warnings, score, errors > 0 || warnings > 0);
    }

    public static List<PresentationSlideDto> Repair(IReadOnlyList<PresentationSlideDto> slides, string fallbackTopic)
    {
        ArgumentNullException.ThrowIfNull(slides);
        string topic = NormalizeWhitespace(fallbackTopic);
        if (topic.Length == 0) topic = "Presentation";

        var repaired = new List<PresentationSlideDto>(Math.Max(1, slides.Count));
        var usedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < slides.Count; index++)
        {
            PresentationSlideDto source = slides[index];
            string title = NormalizeWhitespace(source.Title);
            if (title.Length == 0) title = index == 0 ? topic : "Section " + (index + 1);
            title = TruncateAtBoundary(title, MaximumTitleCharacters);
            if (!usedTitles.Add(title))
            {
                string suffix = " · " + (index + 1);
                title = TruncateAtBoundary(title, Math.Max(8, MaximumTitleCharacters - suffix.Length)) + suffix;
                usedTitles.Add(title);
            }

            var bullets = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? raw in source.Bullets ?? new List<string>())
            {
                string bullet = NormalizeWhitespace(raw);
                if (bullet.Length == 0) continue;
                bullet = TruncateAtBoundary(bullet, MaximumBulletCharacters);
                if (!seen.Add(bullet)) continue;
                if (bullets.Count >= MaximumBulletsPerSlide) break;
                int nextBodyCharacters = bullets.Sum(item => item.Length) + bullet.Length;
                if (nextBodyCharacters > MaximumBodyCharactersPerSlide) break;
                bullets.Add(bullet);
            }

            repaired.Add(new PresentationSlideDto
            {
                Title = title,
                Bullets = bullets,
                SpeakerNotes = NormalizeWhitespace(source.SpeakerNotes)
            });
        }

        if (repaired.Count == 0)
            repaired.Add(new PresentationSlideDto { Title = TruncateAtBoundary(topic, MaximumTitleCharacters), Bullets = new List<string>() });
        return repaired;
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string TruncateAtBoundary(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        int boundary = value.LastIndexOf(' ', maximum - 1, maximum);
        if (boundary < Math.Max(8, maximum / 2)) boundary = maximum;
        return value[..boundary].TrimEnd(' ', ',', ';', ':', '-', '–', '—', '.') + "…";
    }
}
