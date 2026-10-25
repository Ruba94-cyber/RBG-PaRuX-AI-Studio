using System.Buffers.Binary;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

[Collection(UiTestCollection.Name)]
public sealed class CreateWorkspaceSpeechCancellationTests
{
    [Fact, Trait("Category", "Regression"), Trait("Category", "Create"), Trait("Category", "FlaUI")]
    public async Task CreateWorkspaceSpeechCancel_then_followup_stream_completes()
    {
        const string testName = nameof(CreateWorkspaceSpeechCancel_then_followup_stream_completes);
        using var app = new UiTestApplication(testName, enableSpeechCancellationFixture: true);
        try
        {
            new MainWindowPage(app.MainWindow).Click("Navigation_Create_Item", ControlType.ListItem);
            AutomationElement text = UiWait.ById(app.MainWindow, "Create_TTS_Text_Box", ControlType.Edit);
            text.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            text.Patterns.Value.Pattern.SetValue("synthetic cancellation fixture turn");
            AutomationElement voice = UiWait.ById(app.MainWindow, "Create_TTS_Voice_Box", ControlType.Edit);
            voice.Patterns.Value.Pattern.SetValue("af_heart");
            AutomationElement run = UiWait.ById(app.MainWindow, "Create_TTS_Run_Button", ControlType.Button);
            AutomationElement cancel = UiWait.ById(app.MainWindow, "Create_Cancel_Active_Job_Button", ControlType.Button);
            AutomationElement status = UiWait.ById(app.MainWindow, "Create_Operation_Status_Text", ControlType.Text);

            run.Patterns.Invoke.Pattern.Invoke();
            await app.SpeechFirstAudio.WaitAsync(TimeSpan.FromSeconds(15));
            string audioDirectory = Path.Combine(app.DataRoot, "generated", "audio");
            UiWait.For(() => Directory.Exists(audioDirectory)
                && Directory.EnumerateFiles(audioDirectory, "*.partial", SearchOption.TopDirectoryOnly)
                    .Any(path => new FileInfo(path).Length > 44),
                "product writer consumed PCM before cancellation", TimeSpan.FromSeconds(15));
            cancel.Patterns.Invoke.Pattern.Invoke();
            await app.SpeechFirstCancellation.WaitAsync(TimeSpan.FromSeconds(15));
            UiWait.For(() => status.Name == "Speech was stopped; no file was kept.", "Create reports speech cancellation");


            Assert.Empty(Directory.Exists(audioDirectory)
                ? Directory.EnumerateFiles(audioDirectory, "*", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>());
            Assert.Equal(0, app.SpeechArtifactCreateCount);
            Assert.Equal(0, app.SpeechArtifactStatusCount);

            run.Patterns.Invoke.Pattern.Invoke();
            await app.SpeechFollowupDone.WaitAsync(TimeSpan.FromSeconds(15));
            UiWait.For(() => status.Name.Contains("Speech synthesis completed and registered", StringComparison.Ordinal),
                "Create reports successful follow-up synthesis");

            string[] wavs = Directory.GetFiles(audioDirectory, "*.wav", SearchOption.TopDirectoryOnly);
            string[] partials = Directory.GetFiles(audioDirectory, "*.partial", SearchOption.TopDirectoryOnly);
            string wav = Assert.Single(wavs);
            Assert.Empty(partials);
            byte[] bytes = await File.ReadAllBytesAsync(wav);
            Assert.True(bytes.Length >= 48);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(checked((uint)(bytes.Length - 8)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            Assert.Equal(checked((uint)(bytes.Length - 44)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
            Assert.True(app.SpeechFirstWasCancelled);
            Assert.True(app.SpeechFollowupWasCompleted);
            string[] requestIds = app.SpeechRequestIds;
            Assert.Equal(2, requestIds.Length);
            Assert.NotEqual(requestIds[0], requestIds[1]);
            Assert.Equal(new[] { 0, 1 }, app.SpeechSequences(requestIds[0]));
            Assert.Equal(new[] { 0, 1, 2 }, app.SpeechSequences(requestIds[1]));
            Assert.Equal(1, app.SpeechArtifactCreateCount);
            Assert.Equal(1, app.SpeechArtifactStatusCount);
        }
        catch (Exception ex)
        {
            app.CaptureFailure(testName, ex);
            throw;
        }
    }
}
