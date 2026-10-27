using PaRuX.Models;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real UI run: "Edit this image: make the sky purple" answered "I need an attached image to edit" because the last assistant message
/// was an error from an unrelated text turn, although two generated images were right above it.
/// </summary>
public sealed class LastGeneratedImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-lastimg-" + Guid.NewGuid().ToString("N"));

    public LastGeneratedImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ChatAttachment Image(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return new ChatAttachment { MediaType = "image/png", LocalPath = path, FileName = name };
    }

    private static ChatMessage Assistant(string text, bool error = false, params ChatAttachment[] attachments)
    {
        var message = new ChatMessage { Role = "assistant", Content = text, IsError = error };
        message.Attachments.AddRange(attachments);
        return message;
    }

    [Fact]
    public void The_latest_generated_image_is_found_even_when_a_later_assistant_message_is_an_error_or_plain_text()
    {
        ChatAttachment first = Image("first.png");
        ChatAttachment second = Image("second.png");
        var messages = new List<ChatMessage>
        {
            Assistant("Generated locally", false, first),
            Assistant("Generated locally", false, second),
            new ChatMessage { Role = "user", Content = "Reply with exactly TEXT" },
            Assistant("PaRuX Core rejected the request (HTTP 503).", error: true),
            Assistant("Sure, here is a joke.")
        };

        Assert.Same(second, RouterService.FindLastGeneratedImage(messages));
    }

    [Fact]
    public void A_deleted_image_file_is_skipped_for_the_one_before_it()
    {
        ChatAttachment older = Image("older.png");
        ChatAttachment gone = Image("gone.png");
        File.Delete(gone.LocalPath!);
        var messages = new List<ChatMessage> { Assistant("a", false, older), Assistant("b", false, gone) };

        Assert.Same(older, RouterService.FindLastGeneratedImage(messages));
    }

    [Fact]
    public void Conversations_without_generated_images_or_with_only_user_attachments_return_nothing()
    {
        var user = new ChatMessage { Role = "user", Content = "look at this" };
        user.Attachments.Add(Image("mine.png"));

        Assert.Null(RouterService.FindLastGeneratedImage(new List<ChatMessage> { user, Assistant("no image") }));
        Assert.Null(RouterService.FindLastGeneratedImage(new List<ChatMessage>()));
    }
}
