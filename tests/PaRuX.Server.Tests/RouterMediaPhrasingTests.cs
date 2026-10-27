using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Natural English and Italian phrasings that must start media from Chat, and ordinary prompts that must not.</summary>
public sealed class RouterMediaPhrasingTests
{
    [Theory]
    [InlineData("Can you create an image of a red fox in the snow?")]
    [InlineData("generate a photo of a mountain lake at sunrise")]
    [InlineData("I want a picture of a dog wearing sunglasses")]
    [InlineData("Paint a sunset over the sea")]
    [InlineData("make a logo for my coffee shop")]
    [InlineData("Render an illustration of a medieval castle")]
    [InlineData("Show me a photo of a futuristic city")]
    [InlineData("Please draw a cat sitting on a windowsill")]
    [InlineData("I'd like an image of a spaceship")]
    [InlineData("Design a poster for a jazz festival")]
    [InlineData("Puoi creare un'immagine di un gatto sul divano?")]
    [InlineData("Mi fai una foto di un tramonto sul mare")]
    [InlineData("Voglio un'immagine di un drago")]
    [InlineData("Genera un logo per la mia pizzeria")]
    [InlineData("Crea una locandina per un concerto")]
    [InlineData("Dipingi un tramonto in montagna")]
    [InlineData("Realizza un'illustrazione di un castello")]
    [InlineData("Disegnami un robot che cucina")]
    [InlineData("Fammi un'immagine di una città futuristica")]
    public void Image_requests_in_natural_english_and_italian_route_to_image(string text)
    {
        Assert.Equal(RouteKind.Image, RouterService.Classify(text));
    }

    [Theory]
    [InlineData("Make a short video of waves on a beach")]
    [InlineData("create a 5 second clip of a rocket launching")]
    [InlineData("Can you generate a video of a cat playing piano?")]
    [InlineData("I want a video of a city at night")]
    [InlineData("Render a short film of a forest in autumn")]
    [InlineData("Fammi un video di onde sulla spiaggia")]
    [InlineData("Crea una clip di un razzo che decolla")]
    [InlineData("Realizza un video di una città di notte")]
    [InlineData("Genera un filmato di un gatto che suona il pianoforte")]
    [InlineData("Voglio un video di un tramonto")]
    public void Video_requests_in_natural_english_and_italian_route_to_video(string text)
    {
        Assert.Equal(RouteKind.Video, RouterService.Classify(text));
    }

    [Theory]
    [InlineData("How do I draw a line chart in matplotlib?")]
    [InlineData("How to draw a rounded rectangle on an HTML canvas")]
    [InlineData("Come posso disegnare un grafico a barre in Python?")]
    [InlineData("Explain how image generation models work")]
    [InlineData("What is a diffusion model and how does it make pictures?")]
    [InlineData("Write a function that resizes an image")]
    [InlineData("Spiegami come funziona la generazione di immagini")]
    [InlineData("Summarize this video transcript in three bullets")]
    [InlineData("Give me a recipe for a good photo editing workflow")]
    [InlineData("Make a video call to John at 5pm")]
    [InlineData("I need a photo editor recommendation for Windows")]
    [InlineData("I want a picture frame for my desk, any ideas?")]
    [InlineData("Create a video game idea for a weekend jam")]
    [InlineData("Voglio una videochiamata con Marco domani")]
    [InlineData("Ho bisogno di un editor di foto gratuito")]
    [InlineData("Show me the image file size limits in HTTP uploads")]
    public void Ordinary_prompts_about_drawing_images_or_videos_do_not_start_media(string text)
    {
        RouteKind route = RouterService.Classify(text);
        Assert.NotEqual(RouteKind.Image, route);
        Assert.NotEqual(RouteKind.Video, route);
        Assert.NotEqual(RouteKind.ImageEdit, route);
    }
}

public sealed class RouterMusicPhrasingTests
{
    [Theory]
    [InlineData("Compose a song about the sea at night")]
    [InlineData("Can you make a lofi beat for studying?")]
    [InlineData("generate some relaxing piano music")]
    [InlineData("Create an upbeat jingle for my coffee shop")]
    [InlineData("I want a song about summer rain")]
    [InlineData("Produce an epic cinematic soundtrack")]
    [InlineData("Componi una canzone sul mare")]
    [InlineData("Puoi creare una musica rilassante con il pianoforte?")]
    [InlineData("Fammi un brano rock con la chitarra")]
    [InlineData("Genera una base strumentale lo-fi")]
    [InlineData("Voglio una canzone sull'estate")]
    public void Music_requests_in_english_and_italian_route_to_music(string text)
    {
        Assert.Equal(RouteKind.Music, RouterService.Classify(text));
    }

    [Theory]
    [InlineData("How do I compose a song in Ableton?")]
    [InlineData("Make a music video of waves on a beach")]
    [InlineData("Write the lyrics of a song about love")]
    [InlineData("Explain the music theory behind a chord progression")]
    [InlineData("What is the best music player for Windows?")]
    [InlineData("Come si compone una canzone?")]
    [InlineData("Create a playlist name for my road trip")]
    [InlineData("Scrivi il testo di una canzone sull'amore")]
    public void Ordinary_prompts_about_music_do_not_start_music_generation(string text)
    {
        Assert.NotEqual(RouteKind.Music, RouterService.Classify(text));
    }
}

public sealed class MusicLanguageGuessTests
{
    [Theory]
    [InlineData("Componi una canzone sul mare", "it")]
    [InlineData("Fammi un brano rock con la chitarra", "it")]
    [InlineData("Voglio una canzone sull'estate", "it")]
    [InlineData("Compose a song about the sea at night", "en")]
    [InlineData("Make a lofi beat for studying", "en")]
    [InlineData("", "en")]
    public void The_lyrics_language_follows_the_language_of_the_request(string text, string expected)
    {
        Assert.Equal(expected, PaRuX.Services.MediaPhrasing.GuessLanguage(text));
    }
}
