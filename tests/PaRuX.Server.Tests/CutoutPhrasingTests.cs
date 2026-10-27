using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>"Remove the background" must run the cutout engine; replacing or restyling it, and how-to questions, must not.</summary>
public sealed class CutoutPhrasingTests
{
    [Theory]
    [InlineData("remove the background")]
    [InlineData("Remove the background from this image")]
    [InlineData("please remove the background of this photo")]
    [InlineData("Can you remove the background from my picture?")]
    [InlineData("remove the white background")]
    [InlineData("erase the background")]
    [InlineData("get rid of the background")]
    [InlineData("background removal")]
    [InlineData("cut out the subject")]
    [InlineData("make a cutout of the dog")]
    [InlineData("make the background transparent")]
    [InlineData("turn the background transparent please")]
    [InlineData("rimuovi lo sfondo")]
    [InlineData("Rimuovi lo sfondo da questa immagine")]
    [InlineData("rimuovi il background")]
    [InlineData("togli lo sfondo")]
    [InlineData("Puoi togliere lo sfondo da questa foto?")]
    [InlineData("elimina lo sfondo")]
    [InlineData("cancella lo sfondo")]
    [InlineData("rimuovi lo sfondo bianco")]
    [InlineData("ritaglia il soggetto")]
    [InlineData("isola il soggetto")]
    [InlineData("rendi lo sfondo trasparente")]
    [InlineData("fammi la foto senza sfondo")]
    public void Plain_background_removal_is_a_cutout(string text)
    {
        Assert.True(CutoutPhrasing.IsBackgroundRemoval(text), text);
    }

    [Theory]
    [InlineData("replace the background with a beach")]
    [InlineData("remove the background and put a sunset behind him")]
    [InlineData("remove the background and make it white")]
    [InlineData("remove the background and add a city skyline")]
    [InlineData("change the background to blue")]
    [InlineData("remove the background then animate it")]
    [InlineData("rimuovi lo sfondo e metti il mare")]
    [InlineData("rimuovi lo sfondo e aggiungi una spiaggia")]
    [InlineData("togli lo sfondo e rendilo blu")]
    [InlineData("sostituisci lo sfondo con un tramonto")]
    [InlineData("cambia lo sfondo")]
    [InlineData("remove the object")]
    [InlineData("rimuovi l'oggetto")]
    [InlineData("How do I remove the background in Photoshop?")]
    [InlineData("Come posso rimuovere lo sfondo in Python?")]
    [InlineData("Explain how background removal models work")]
    [InlineData("Write a function that removes the background of an image")]
    [InlineData("describe the background of this photo")]
    [InlineData("a song without background music")]
    [InlineData("")]
    [InlineData("   ")]
    public void Edits_that_do_more_than_cut_out_and_non_requests_are_not_a_cutout(string text)
    {
        Assert.False(CutoutPhrasing.IsBackgroundRemoval(text), text);
    }

    [Fact]
    public void Null_text_is_not_a_cutout()
    {
        Assert.False(CutoutPhrasing.IsBackgroundRemoval(null));
    }

    [Theory]
    [InlineData("remove the background")]
    [InlineData("rimuovi lo sfondo")]
    [InlineData("togli lo sfondo da questa foto")]
    [InlineData("ritaglia il soggetto")]
    [InlineData("make the background transparent")]
    [InlineData("rendi lo sfondo trasparente")]
    public void Cutout_phrases_take_the_image_edit_route_in_chat_with_or_without_an_attachment(string text)
    {
        Assert.Equal(RouteKind.ImageEdit, RouterService.Classify(text));
        Assert.Equal(RouteKind.ImageEdit, RouterService.ClassifyWithAttachment(text));
        // The last generated image rides along, so "rimuovi lo sfondo" after a generation needs no re-attach.
        Assert.True(RouterService.ShouldAttachPriorGeneratedImage(RouteKind.ImageEdit, text));
    }

    [Theory]
    [InlineData("Draw a cat on a transparent background", RouteKind.Image)]
    [InlineData("Generate an image of a red fox in the snow", RouteKind.Image)]
    [InlineData("Come posso rimuovere lo sfondo in Python?", RouteKind.ChatNormal)]
    public void Phrases_that_are_not_cutouts_keep_their_existing_route(string text, RouteKind expected)
    {
        Assert.Equal(expected, RouterService.Classify(text));
    }
}
