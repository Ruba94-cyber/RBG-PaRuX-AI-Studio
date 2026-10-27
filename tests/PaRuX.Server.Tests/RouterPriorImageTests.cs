using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real UI run: after Chat generated an image, "Reply with exactly the words: TEXT-AFTER-MEDIA-OK" attached that image, took the Vision
/// route and failed with HTTP 503 "No managed local vision helper is active". Plain text must stay plain text.
/// </summary>
public sealed class RouterPriorImageTests
{
    [Theory]
    [InlineData("Reply with exactly the words: TEXT-AFTER-MEDIA-OK")]
    [InlineData("Thanks! Now tell me a joke about foxes")]
    [InlineData("Grazie, ora spiegami come funziona il Wi-Fi")]
    [InlineData("What is the capital of France?")]
    public void An_ordinary_text_message_after_a_generated_image_does_not_attach_it(string text)
    {
        RouteKind route = RouterService.Classify(text);

        Assert.False(RouterService.ShouldAttachPriorGeneratedImage(route, text));
    }

    [Theory]
    [InlineData("Describe this image")]
    [InlineData("What is in the picture you just made?")]
    [InlineData("Cosa c'è in questa immagine?")]
    [InlineData("Descrivi la foto")]
    public void Asking_about_the_generated_image_attaches_it(string text)
    {
        RouteKind route = RouterService.Classify(text);

        Assert.True(RouterService.ShouldAttachPriorGeneratedImage(route, text));
    }

    [Theory]
    [InlineData("Edit this image and make the sky purple", RouteKind.ImageEdit)]
    [InlineData("Animate this image", RouteKind.Video)]
    [InlineData("Anima questa immagine", RouteKind.Video)]
    public void Edit_and_animate_requests_still_use_the_generated_image(string text, RouteKind expected)
    {
        RouteKind route = RouterService.Classify(text);

        Assert.Equal(expected, route);
        Assert.True(RouterService.ShouldAttachPriorGeneratedImage(route, text));
    }
}
