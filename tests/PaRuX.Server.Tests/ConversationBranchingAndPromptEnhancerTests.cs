using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ConversationBranchingAndPromptEnhancerTests
{
    private static Conversation Sample()
    {
        var c = new Conversation { Title = "Dragons" };
        c.Messages.Add(new ChatMessage { Role = "user", Content = "draw a dragon" });
        var a = new ChatMessage { Role = "assistant", Content = "done" };
        a.Attachments.Add(new ChatAttachment { FileName = "d.png", LocalPath = @"C:\x\d.png" });
        c.Messages.Add(a);
        c.Messages.Add(new ChatMessage { Role = "user", Content = "now blue" });
        return c;
    }

    [Fact]
    public void BranchAt_KeepsMessagesThroughIndexAndLinksParent()
    {
        Conversation src = Sample();
        Conversation? b = ConversationBranching.BranchAt(src, 1);
        Assert.NotNull(b);
        Assert.Equal(2, b!.Messages.Count);
        Assert.Equal("Branch of Dragons", b.Title);
        Assert.Equal(src.Id, b.ParentConversationId);
        Assert.Equal(src.Messages[1].MessageId, b.ParentMessageId);
        Assert.NotEqual(src.Id, b.Id);
        Assert.Equal(3, src.Messages.Count);
    }

    [Fact]
    public void BranchAt_ReferencesAttachmentsWithoutSharingObjects()
    {
        Conversation src = Sample();
        Conversation b = ConversationBranching.BranchAt(src, src.Messages[1])!;
        Assert.Equal(@"C:\x\d.png", b.Messages[1].Attachments[0].LocalPath);
        Assert.NotSame(src.Messages[1].Attachments[0], b.Messages[1].Attachments[0]);
        b.Messages[1].Content = "changed";
        Assert.Equal("done", src.Messages[1].Content);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void BranchAt_OutOfRangeReturnsNull(int index) =>
        Assert.Null(ConversationBranching.BranchAt(Sample(), index));

    [Fact]
    public void Duplicate_CopiesAllMessagesWithoutParentMessage()
    {
        Conversation src = Sample();
        src.Messages[2].IsStreaming = true;
        Conversation d = ConversationBranching.Duplicate(src);
        Assert.Equal(3, d.Messages.Count);
        Assert.Null(d.ParentMessageId);
        Assert.Equal(src.Id, d.ParentConversationId);
        Assert.False(d.Messages[2].IsStreaming);
        Assert.StartsWith("Copy of", d.Title);
    }

    [Fact]
    public void Title_IsBoundedAndHandlesBlank()
    {
        Assert.Equal("Branch of New chat", ConversationBranching.MakeTitle("Branch of ", " "));
        Assert.True(ConversationBranching.MakeTitle("Branch of ", new string('x', 500)).Length <= 80);
    }

    [Fact]
    public void OldJsonWithoutParentFields_Deserializes()
    {
        Conversation? c = JsonSerializer.Deserialize<Conversation>("{\"Title\":\"old\",\"Messages\":[{\"Role\":\"user\",\"Content\":\"hi\"}]}");
        Assert.NotNull(c);
        Assert.Null(c!.ParentConversationId);
        Assert.Null(c.Messages[0].MessageId);
        Conversation round = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(ConversationBranching.BranchAt(Sample(), 0)!))!;
        Assert.NotNull(round.ParentMessageId);
    }

    // ---- prompt enhancer ----

    [Theory]
    [InlineData("Here is the improved prompt: a red fox, soft light", "a red fox, soft light")]
    [InlineData("\"a red fox\"", "a red fox")]
    [InlineData("```\na red fox\n```", "a red fox")]
    [InlineData("Sure! Prompt: a red fox", "a red fox")]
    public void Sanitize_StripsMetaText(string raw, string expected) =>
        Assert.Equal(expected, MediaPromptEnhancer.Sanitize(raw));

    [Fact]
    public void Sanitize_EmptyIsNull_AndCapsLength()
    {
        Assert.Null(MediaPromptEnhancer.Sanitize("  "));
        string? capped = MediaPromptEnhancer.Sanitize(new string('a', 5000) + " b");
        Assert.True(capped!.Length <= MediaPromptEnhancer.MaxOutputChars);
    }

    [Fact]
    public void BuildInstruction_MentionsKindLanguageAndBoundsInput()
    {
        string v = MediaPromptEnhancer.BuildInstruction("video", new string('z', 10_000));
        Assert.Contains("video-generation", v);
        Assert.Contains("SAME LANGUAGE", v);
        Assert.True(v.Length < 3_000);
        Assert.Contains("music-generation", MediaPromptEnhancer.BuildInstruction("music", "x"));
    }

    [Fact]
    public async Task Enhance_Success_ReturnsCleanedPrompt()
    {
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "un gatto", (_, _) => Task.FromResult("Prompt: un gatto soffice, luce calda"),
            localOnly: true, modelIsLocal: true, cloudConsent: false);
        Assert.False(r.UsedFallback);
        Assert.Equal("un gatto soffice, luce calda", r.Enhanced);
        Assert.True(r.Changed);
        Assert.Equal("un gatto", r.ToProvenanceDetails(true)["Original prompt"]);
        Assert.Equal(r.Enhanced, r.ToProvenanceDetails(true)["Enhanced prompt"]);
    }

    [Fact]
    public async Task Enhance_TimeoutFallsBackToOriginal()
    {
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "a cat",
            async (_, t) => { await Task.Delay(Timeout.Infinite, t); return "x"; },
            true, true, false, TimeSpan.FromMilliseconds(50));
        Assert.True(r.UsedFallback);
        Assert.Equal("a cat", r.Enhanced);
        Assert.Equal("timed out", r.FallbackReason);
    }

    [Fact]
    public async Task Enhance_ModelErrorFallsBack()
    {
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "a cat", (_, _) => throw new InvalidOperationException("boom"), true, true, false);
        Assert.True(r.UsedFallback);
        Assert.Equal("a cat", r.Enhanced);
    }

    [Fact]
    public async Task Enhance_CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaPromptEnhancer.EnhanceAsync("image", "a cat",
            async (_, t) => { await Task.Delay(1000, t); return "x"; }, true, true, false, ct: cts.Token));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public async Task Enhance_NeverUsesCloudWithoutConsentOrInLocalOnly(bool localOnly, bool modelIsLocal, bool consent)
    {
        bool called = false;
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "a cat", (_, _) => { called = true; return Task.FromResult("better cat"); },
            localOnly, modelIsLocal, consent);
        Assert.False(called);
        Assert.True(r.UsedFallback);
    }

    [Fact]
    public async Task Enhance_NonLocalRoute_SendsNothing_AndSaysWhy()
    {
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "secret prompt",
            (_, _) => throw new Xunit.Sdk.XunitException("client must not be called"),
            localOnly: false, modelIsLocal: false, cloudConsent: false);
        Assert.True(r.UsedFallback);
        Assert.Equal("secret prompt", r.Enhanced);
        Assert.Contains("cloud not permitted", r.FallbackReason);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task ServerRefinement_IsSkippedOnlyWhenPromptWasReviewed(bool reviewed, int expectedCalls)
    {
        int calls = 0;
        string used = await PaRuX.Server.ComfyWorkflowRunner.RefineUnlessReviewedAsync(reviewed, "my reviewed text",
            (p, _) => { calls++; return Task.FromResult("silently rewritten"); }, CancellationToken.None);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(reviewed ? "my reviewed text" : "silently rewritten", used);
    }

    [Fact]
    public void MediaJobRequest_PromptAlreadyReviewed_DefaultsFalseAndIsBackwardCompatible()
    {
        Assert.False(new PaRuX.Contracts.MediaJobRequest().PromptAlreadyReviewed);
        var old = JsonSerializer.Deserialize<PaRuX.Contracts.MediaJobRequest>("{\"Type\":\"image\",\"Prompt\":\"x\"}");
        Assert.False(old!.PromptAlreadyReviewed);
    }

    [Fact]
    public async Task Enhance_CloudAllowedOnlyWithConsentAndNotLocalOnly()
    {
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "a cat", (_, _) => Task.FromResult("a better cat"), false, false, true);
        Assert.False(r.UsedFallback);
    }

    [Fact]
    public async Task Enhance_TranslationToDifferentScriptIsRejected()
    {
        var r = await MediaPromptEnhancer.EnhanceAsync("image", "гора на закате", (_, _) => Task.FromResult("a mountain at sunset"), true, true, false);
        Assert.True(r.UsedFallback);
        Assert.Equal("language not preserved", r.FallbackReason);
        Assert.Equal("гора на закате", r.Enhanced);
    }
}
