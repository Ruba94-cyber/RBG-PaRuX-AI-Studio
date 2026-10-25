using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class DeckQualityCriticTests
{
    [Fact]
    public void Analyze_flags_missing_hierarchy_duplicate_titles_and_overloaded_body()
    {
        var slides = new List<PresentationSlideDto>
        {
            new() { Title = "Overview", Bullets = new List<string>() },
            new()
            {
                Title = "Overview",
                Bullets = Enumerable.Range(1, 8)
                    .Select(i => new string((char)('a' + i), 125))
                    .ToList()
            },
            new() { Title = "", Bullets = new List<string>() }
        };

        DeckQualityCritic.Review review = DeckQualityCritic.Analyze(slides);

        Assert.True(review.RequiresRepair);
        Assert.True(review.ErrorCount > 0);
        Assert.True(review.WarningCount > 0);
        Assert.Contains(review.Findings, item => item.Code == "title.duplicate");
        Assert.Contains(review.Findings, item => item.Code == "body.too-many-bullets");
        Assert.Contains(review.Findings, item => item.Code == "body.overloaded");
        Assert.Contains(review.Findings, item => item.Code == "title.missing");
    }

    [Fact]
    public void Repair_enforces_layout_budgets_and_unique_titles_without_mutating_input()
    {
        string longTitle = "A very long title " + new string('x', 100);
        string longBullet = "A paragraph-sized bullet " + new string('y', 160);
        var source = new List<PresentationSlideDto>
        {
            new() { Title = longTitle, Bullets = new List<string> { longBullet, longBullet } },
            new() { Title = longTitle, Bullets = Enumerable.Repeat(longBullet, 10).ToList() },
            new() { Title = " ", Bullets = new List<string>() }
        };

        List<PresentationSlideDto> repaired = DeckQualityCritic.Repair(source, "Fallback topic");
        DeckQualityCritic.Review after = DeckQualityCritic.Analyze(repaired);

        Assert.Equal(3, repaired.Count);
        Assert.All(repaired, slide => Assert.True(slide.Title.Length <= DeckQualityCritic.MaximumTitleCharacters + 1));
        Assert.All(repaired, slide => Assert.True(slide.Bullets.Count <= DeckQualityCritic.MaximumBulletsPerSlide));
        Assert.All(repaired.SelectMany(slide => slide.Bullets), bullet => Assert.True(bullet.Length <= DeckQualityCritic.MaximumBulletCharacters + 1));
        Assert.Equal(repaired.Count, repaired.Select(slide => slide.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(after.Findings, item => item.Code == "body.overloaded");
        Assert.Equal(longTitle, source[0].Title);
        Assert.Equal(10, source[1].Bullets.Count);
    }

    [Fact]
    public void Repair_creates_a_real_cover_when_outline_parse_returns_nothing()
    {
        List<PresentationSlideDto> repaired = DeckQualityCritic.Repair(Array.Empty<PresentationSlideDto>(), "PaRuX architecture");
        PresentationSlideDto cover = Assert.Single(repaired);
        Assert.Equal("PaRuX architecture", cover.Title);
        Assert.Empty(cover.Bullets);
    }
}
