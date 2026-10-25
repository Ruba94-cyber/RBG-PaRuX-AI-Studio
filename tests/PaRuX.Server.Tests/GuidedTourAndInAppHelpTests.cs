using System;
using System.Collections.Generic;
using System.Linq;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class GuidedTourAndInAppHelpTests
    {
        [Fact]
        public void EvaluateFirstAnswer_StartsTourOnFirstSuccessfulAssistantMessage()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: false,
                role: "assistant",
                content: "Hello! I am your local AI assistant running on this PC.",
                isError: false,
                isStreaming: false);

            Assert.True(decision.ShouldStart);
            Assert.Equal("FirstSuccessfulAnswer", decision.Reason);
        }

        [Fact]
        public void EvaluateFirstAnswer_DoesNotStartIfTourAlreadyCompleted()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: true,
                hasHadSuccessfulAnswer: false,
                role: "assistant",
                content: "Here is your answer.",
                isError: false,
                isStreaming: false);

            Assert.False(decision.ShouldStart);
            Assert.Equal("TourAlreadyCompleted", decision.Reason);
        }

        [Fact]
        public void EvaluateFirstAnswer_DoesNotStartOnSubsequentAnswers()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: true,
                role: "assistant",
                content: "This is the second answer in the conversation.",
                isError: false,
                isStreaming: false);

            Assert.False(decision.ShouldStart);
            Assert.Equal("NotFirstAnswer", decision.Reason);
        }

        [Theory]
        [InlineData("user")]
        [InlineData("system")]
        [InlineData("")]
        [InlineData(null)]
        public void EvaluateFirstAnswer_DoesNotStartForNonAssistantRoles(string? role)
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: false,
                role: role,
                content: "Some text",
                isError: false,
                isStreaming: false);

            Assert.False(decision.ShouldStart);
            Assert.Equal("NotAssistantTurn", decision.Reason);
        }

        [Fact]
        public void EvaluateFirstAnswer_DoesNotStartIfMessageIsError()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: false,
                role: "assistant",
                content: "Failed to load model: llama-server exited with code 1.",
                isError: true,
                isStreaming: false);

            Assert.False(decision.ShouldStart);
            Assert.Equal("AssistantMessageIsError", decision.Reason);
        }

        [Fact]
        public void EvaluateFirstAnswer_DoesNotStartWhileStillStreaming()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: false,
                role: "assistant",
                content: "Partially streamed text...",
                isError: false,
                isStreaming: true);

            Assert.False(decision.ShouldStart);
            Assert.Equal("MessageStillStreaming", decision.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void EvaluateFirstAnswer_DoesNotStartIfContentEmpty(string? content)
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: false,
                role: "assistant",
                content: content,
                isError: false,
                isStreaming: false);

            Assert.False(decision.ShouldStart);
            Assert.Equal("AssistantMessageEmpty", decision.Reason);
        }

        [Fact]
        public void EvaluateFirstAnswer_DoesNotStartIfTurnWasStoppedByUser()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateFirstAnswer(
                isTourCompleted: false,
                hasHadSuccessfulAnswer: false,
                role: "assistant",
                content: "I started thinking about this...\n\n_Stopped._",
                isError: false,
                isStreaming: false);

            Assert.False(decision.ShouldStart);
            Assert.Equal("TurnStopped", decision.Reason);
        }

        [Fact]
        public void EvaluateHandoff_StartsTourOnModelHubHandoff()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateHandoff(
                isTourCompleted: false,
                handoffKind: "ModelHub");

            Assert.True(decision.ShouldStart);
            Assert.Equal("Handoff:ModelHub", decision.Reason);
        }

        [Fact]
        public void EvaluateHandoff_StartsTourOnCodeHandoff()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateHandoff(
                isTourCompleted: false,
                handoffKind: "CodeHandoff");

            Assert.True(decision.ShouldStart);
            Assert.Equal("Handoff:CodeHandoff", decision.Reason);
        }

        [Fact]
        public void EvaluateHandoff_DoesNotStartIfTourAlreadyCompleted()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateHandoff(
                isTourCompleted: true,
                handoffKind: "ModelHub");

            Assert.False(decision.ShouldStart);
            Assert.Equal("TourAlreadyCompleted", decision.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void EvaluateHandoff_RejectsInvalidHandoffKind(string? kind)
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateHandoff(
                isTourCompleted: false,
                handoffKind: kind);

            Assert.False(decision.ShouldStart);
            Assert.Equal("UnknownHandoff", decision.Reason);
        }

        [Fact]
        public void EvaluateReplayRequest_AlwaysStartsEvenIfCompleted()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateReplayRequest();

            Assert.True(decision.ShouldStart);
            Assert.Equal("ReplayRequested", decision.Reason);
        }

        [Fact]
        public void EvaluateGetStarted_StartsIfTourNotCompleted()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateGetStarted(isTourCompleted: false);

            Assert.True(decision.ShouldStart);
            Assert.Equal("GetStarted", decision.Reason);
        }

        [Fact]
        public void EvaluateGetStarted_DoesNotStartIfTourAlreadyCompleted()
        {
            GuidedTourDecision decision = GuidedTourTriggerPolicy.EvaluateGetStarted(isTourCompleted: true);

            Assert.False(decision.ShouldStart);
            Assert.Equal("TourAlreadyCompleted", decision.Reason);
        }

        [Fact]
        public void ShouldRecordCompletionOnDismiss_ReturnsTrue()
        {
            Assert.True(GuidedTourTriggerPolicy.ShouldRecordCompletionOnDismiss(false));
            Assert.True(GuidedTourTriggerPolicy.ShouldRecordCompletionOnDismiss(true));
        }

        [Fact]
        public void InAppHelpCatalog_DefaultContainsRequiredTopics()
        {
            InAppHelpCatalog catalog = InAppHelpCatalog.Default;
            IReadOnlyList<InAppHelpTopic> topics = catalog.GetAllTopics();

            Assert.NotEmpty(topics);
            Assert.Contains(topics, t => t.Id == "tour");
            Assert.Contains(topics, t => t.Id == "first-start");
            Assert.Contains(topics, t => t.Id == "model-hub");
            Assert.Contains(topics, t => t.Id == "workspaces");
            Assert.Contains(topics, t => t.Id == "shortcuts");
            Assert.Contains(topics, t => t.Id == "troubleshooting");
        }

        [Fact]
        public void InAppHelpCatalog_TourTopicSupportsReplay()
        {
            InAppHelpTopic? tourTopic = InAppHelpCatalog.Default.GetTopic("tour");

            Assert.NotNull(tourTopic);
            Assert.True(tourTopic!.SupportsReplayTour);
            Assert.Contains("replay", tourTopic.Keywords);
        }

        [Fact]
        public void InAppHelpCatalog_GetTopic_ReturnsNullForUnknownId()
        {
            InAppHelpTopic? topic = InAppHelpCatalog.Default.GetTopic("unknown-id-12345");
            Assert.Null(topic);
        }

        [Fact]
        public void InAppHelpCatalog_Search_FindsRelevantTopicsByKeyword()
        {
            IReadOnlyList<InAppHelpTopic> results = InAppHelpCatalog.Default.Search("shortcuts");

            Assert.NotEmpty(results);
            Assert.Contains(results, t => t.Id == "shortcuts");
        }

        [Fact]
        public void InAppHelpCatalog_Search_EmptyQueryReturnsAllTopics()
        {
            IReadOnlyList<InAppHelpTopic> all = InAppHelpCatalog.Default.GetAllTopics();
            IReadOnlyList<InAppHelpTopic> searchEmpty = InAppHelpCatalog.Default.Search("  ");

            Assert.Equal(all.Count, searchEmpty.Count);
        }

        [Fact]
        public void InAppHelpCatalog_GenerateHelpSummaryMarkdown_ProducesStructuredMarkdown()
        {
            string markdown = InAppHelpCatalog.Default.GenerateHelpSummaryMarkdown();

            Assert.NotNull(markdown);
            Assert.Contains("# PaRuX AI Studio — Help & Guided Tour", markdown);
            Assert.Contains("Replay Guided Tour", markdown);
            Assert.Contains("## Getting Started", markdown);
            Assert.Contains("### Interactive Guided Tour", markdown);
        }

        [Fact]
        public void InAppHelpCatalog_GenerateTopicMarkdown_ReturnsTopicContentOrNotFound()
        {
            string found = InAppHelpCatalog.Default.GenerateTopicMarkdown("first-start");
            Assert.Contains("First Start & Local Chat", found);
            Assert.Contains("llama.cpp", found);

            string notFound = InAppHelpCatalog.Default.GenerateTopicMarkdown("missing-topic");
            Assert.Contains("Topic Not Found", notFound);
        }
    }
}
