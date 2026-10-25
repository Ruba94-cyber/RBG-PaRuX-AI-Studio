using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class ChatMediaIntentTests
{
    [Theory]
    [InlineData("Generate an image of a red fox, then animate it")]
    [InlineData("Genera un'immagine di una volpe e poi animala")]
    [InlineData("Create an image of a castle and then make a video of it")]
    public void ImageThenVideoRequestIsFlaggedAsTwoStep(string text)
    {
        Assert.Equal(RouteKind.Image, RouterService.Classify(text));
        Assert.True(RouterService.HasFollowUpVideoStep(text));
    }

    [Theory]
    [InlineData("Generate an image of a red fox")]
    [InlineData("Genera un'immagine di una volpe")]
    [InlineData("Genera un video di una volpe")]
    public void SingleStepMediaRequestHasNoFollowUp(string text) =>
        Assert.False(RouterService.HasFollowUpVideoStep(text));

    [Theory]
    [InlineData("Animate this image")]
    [InlineData("Anima questa immagine")]
    public void AnimateRequestRoutesToVideo(string text)
    {
        Assert.Equal(RouteKind.Video, RouterService.Classify(text));
        Assert.Equal(RouteKind.Video, RouterService.ClassifyWithAttachment(text));
    }

    [Fact]
    public void PlainQuestionWithAttachmentStaysVision() =>
        Assert.Equal(RouteKind.VisionDelegated, RouterService.ClassifyWithAttachment("What is in this picture?"));

    [Theory]
    [InlineData("Rigenera la risposta")]
    [InlineData("Regenerate your answer")]
    [InlineData("regenerate the summary")]
    public void RegeneratingTextIsNotAnImageEdit(string text) =>
        Assert.NotEqual(RouteKind.ImageEdit, RouterService.Classify(text));

    [Theory]
    [InlineData("Animate this image", true)]
    [InlineData("Anima questa immagine", true)]
    [InlineData("Genera un video di una volpe", false)]
    public void OnlyVideoRequestsPointingAtAnImageReuseIt(string text, bool expected) =>
        Assert.Equal(expected, RouterService.ReferencesExistingImage(text));

    [Theory]
    [InlineData(RouteKind.Video, true)]
    [InlineData(RouteKind.ImageEdit, true)]
    [InlineData(RouteKind.ChatNormal, true)]
    [InlineData(RouteKind.Image, false)]
    [InlineData(RouteKind.CodeHandoff, false)]
    public void PriorImageReuseCoversVideo(RouteKind route, bool expected) =>
        Assert.Equal(expected, RouterService.UsesPriorGeneratedImage(route));
}
