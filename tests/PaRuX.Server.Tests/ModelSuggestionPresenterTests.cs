using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class ModelSuggestionPresenterTests
    {
        private static SuggestionFeedProposalDto Proposal(string key, string fit, string kind = "new_candidate",
            params string[] cohorts) => new()
        {
            Kind = kind, Key = key, Repo = "org/" + key, FitClass = fit, SizeBytes = 4L * 1073741824,
            Cohorts = cohorts.ToList(), Capability = "general_chat", License = "apache-2.0", Reason = "Criterion: hardware fit."
        };

        private static SuggestionFeedStatusDto Status(string outcome, params SuggestionFeedProposalDto[] proposals) => new()
        {
            LastReport = new SuggestionFeedReportDto { Outcome = outcome, Trigger = "startup", Proposals = proposals.ToList(), FinishedUtc = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc) },
            Fingerprint = new SuggestionFeedFingerprintDto
            {
                SystemRamBytes = 32L * 1073741824,
                Adapters = { new SuggestionFeedAdapterDto { Name = "GPU X", DedicatedVramBytes = 8L * 1073741824 } }
            }
        };

        [Fact]
        public void Null_status_is_an_honest_unavailable_state()
        {
            ModelSuggestionSection s = ModelSuggestionPresenter.Present(null, null);
            Assert.Empty(s.Cards);
            Assert.Contains("unavailable", s.EmptyText);
        }

        [Fact]
        public void Empty_report_says_no_suggestions_yet_without_inventing_any()
        {
            ModelSuggestionSection s = ModelSuggestionPresenter.Present(Status("no_change"), null);
            Assert.Empty(s.Cards);
            Assert.StartsWith("No suggestions yet", s.EmptyText);
            Assert.Contains("32 GB RAM", s.HardwareText);
            Assert.Contains("GPU X (8 GB VRAM)", s.HardwareText);
        }

        [Theory]
        [InlineData("skipped_gaming", "Gaming")]
        [InlineData("skipped_disabled", "turned off")]
        [InlineData("error", "failed")]
        public void Empty_state_reflects_the_outcome(string outcome, string expected) =>
            Assert.Contains(expected, ModelSuggestionPresenter.Present(Status(outcome), null).EmptyText);

        [Fact]
        public void Cards_sort_by_fit_then_fresh_cohort_then_key_and_map_chips()
        {
            ModelSuggestionSection s = ModelSuggestionPresenter.Present(Status("ok",
                Proposal("c", "CpuOnly"), Proposal("b", "FitsOnGpu", "new_candidate", "popular"),
                Proposal("a", "FitsOnGpu", "new_candidate", "new", "recently_updated", "bogus"),
                Proposal("d", "SplitWithRam")), null);
            Assert.Equal(new[] { "a", "b", "d", "c" }, s.Cards.Select(c => c.Key));
            Assert.Equal(new[] { "New", "Recently updated" }, s.Cards[0].CohortChips);
            Assert.Equal("On this PC: Fits GPU", s.Cards[0].FitText);
            Assert.Equal("On this PC: GPU/RAM split", s.Cards[2].FitText);
            Assert.Equal("On this PC: CPU-only", s.Cards[3].FitText);
            Assert.Contains("~4.0 GB", s.Cards[0].SizeText);
            Assert.Equal("General chat", s.Cards[0].CapabilityText);
            Assert.Contains("unverified", s.Cards[0].LicenceText);
        }

        [Fact]
        public void Runtime_update_candidate_has_no_download_action()
        {
            ModelSuggestionCard card = ModelSuggestionPresenter.Present(
                Status("ok", Proposal("k", "FitsOnGpu", "candidate_needs_runtime_update")), null).Cards.Single();
            Assert.False(card.CanReviewAndDownload);
            Assert.Contains("runtime", card.RuntimeNoticeText);
            Assert.True(card.CanIgnore);
        }

        [Fact]
        public void New_candidate_offers_review_and_missing_licence_is_stated()
        {
            SuggestionFeedProposalDto p = Proposal("k", "FitsOnGpu");
            p.License = null;
            ModelSuggestionCard card = ModelSuggestionPresenter.Present(Status("ok", p), null).Cards.Single();
            Assert.True(card.CanReviewAndDownload);
            Assert.Null(card.RuntimeNoticeText);
            Assert.Equal("Licence not declared", card.LicenceText);
        }

        [Fact]
        public void Runtime_only_kinds_and_invalid_proposals_are_not_cards()
        {
            SuggestionFeedProposalDto noRepo = Proposal("x", "FitsOnGpu");
            noRepo.Repo = null;
            ModelSuggestionSection s = ModelSuggestionPresenter.Present(Status("ok",
                Proposal("r", "FitsOnGpu", "runtime_update"), Proposal("n", "FitsOnGpu", "runtime_note"), noRepo), null);
            Assert.Empty(s.Cards);
        }

        [Fact]
        public void Deferred_and_hidden_keys_are_removed_and_cards_are_bounded()
        {
            SuggestionFeedProposalDto[] many = Enumerable.Range(0, 20).Select(i => Proposal("k" + i.ToString("00"), "FitsOnGpu")).ToArray();
            Assert.Equal(ModelSuggestionPresenter.MaximumCards, ModelSuggestionPresenter.Present(Status("ok", many), null).Cards.Count);
            var deferred = new HashSet<string> { "k00" };
            var hidden = new HashSet<string> { "k01" };
            IReadOnlyList<ModelSuggestionCard> cards = ModelSuggestionPresenter.Present(Status("ok", many), deferred, hidden).Cards;
            Assert.DoesNotContain(cards, c => c.Key is "k00" or "k01");
            ModelSuggestionSection none = ModelSuggestionPresenter.Present(Status("ok", Proposal("k", "CpuOnly")), new HashSet<string> { "k" });
            Assert.Contains("Deferred", none.EmptyText);
        }

        [Theory]
        [InlineData(ModelCohortFilter.All, true)]
        [InlineData(ModelCohortFilter.Popular, true)]
        [InlineData(ModelCohortFilter.New, false)]
        [InlineData(ModelCohortFilter.Updated, false)]
        public void Cohort_filter_matches_popular_only(ModelCohortFilter filter, bool expected) =>
            Assert.Equal(expected, ModelCohortFilterPresenter.Matches(filter, new[] { "popular" }));

        [Fact]
        public void Cohort_filter_updated_matches_recently_updated_and_empty_matches_only_all()
        {
            Assert.True(ModelCohortFilterPresenter.Matches(ModelCohortFilter.Updated, new[] { "recently_updated" }));
            Assert.False(ModelCohortFilterPresenter.Matches(ModelCohortFilter.Popular, new List<string>()));
            Assert.True(ModelCohortFilterPresenter.Matches(ModelCohortFilter.All, null));
            Assert.Equal("Discovery feed: not recorded", ModelCohortFilterPresenter.CohortLine(null));
            Assert.Equal("Discovery feed: Popular, New", ModelCohortFilterPresenter.CohortLine(new[] { "popular", "new" }));
            Assert.Equal("ModelHub_CohortFilter_Updated", ModelCohortFilterPresenter.AutomationId(ModelCohortFilter.Updated));
        }

        [Theory]
        [InlineData("abliterated", "abliterated", true)]
        [InlineData("audio", "Audio", true)]
        [InlineData("uncensored-roleplay", "uncensored_roleplay", true)]
        [InlineData("audio", "general", false)]
        [InlineData("All categories", "anything", true)]
        public void Category_filter_handles_every_server_category(string selected, string category, bool expected) =>
            Assert.Equal(expected, ModelCategoryFilter.Matches(selected, category));
    }
}
