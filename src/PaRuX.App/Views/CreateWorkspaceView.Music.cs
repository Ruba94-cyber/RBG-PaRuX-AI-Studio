using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Create &gt; Music: ACE-Step 1.5 songs with optional lyrics, generated locally through the managed ComfyUI.</summary>
    public sealed partial class CreateWorkspaceView
    {
        private readonly TextBox _musicPrompt = new TextBox { PlaceholderText = "Describe the music: style, instruments, mood", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        private readonly TextBox _musicLyrics = new TextBox { PlaceholderText = "Lyrics (optional, with [Verse] / [Chorus] tags). Leave empty for an instrumental.", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
        private readonly NumberBox _musicSeconds = new NumberBox { Minimum = 10, Maximum = 240, Value = 30, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private readonly ComboBox _musicLanguage = new ComboBox { MinWidth = 160 };

        private UIElement BuildMusicPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("music", "music"));
            AutomationProperties.SetAutomationId(_musicPrompt, "Create_Music_Prompt");
            AutomationProperties.SetAutomationId(_musicLyrics, "Create_Music_Lyrics");
            AutomationProperties.SetAutomationId(_musicLanguage, "Create_Music_Language");
            panel.Children.Add(_musicPrompt);
            panel.Children.Add(ExampleChips(_musicPrompt, "Create_Music_Example",
                "Warm acoustic folk, fingerpicked guitar, soft brushed drums, gentle vocals",
                "Lo-fi hip hop beat, dusty vinyl, mellow Rhodes, rainy night",
                "Epic cinematic orchestra, rising strings and taiko drums",
                "Upbeat 80s synth-pop, driving bass, bright female vocals"));
            panel.Children.Add(_musicLyrics);

            foreach ((string code, string label) in new[] { ("en", "English"), ("it", "Italiano"), ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"), ("pt", "Português"), ("ja", "日本語"), ("zh", "中文") })
                _musicLanguage.Items.Add(new ComboBoxItem { Content = label, Tag = code });
            _musicLanguage.SelectedIndex = 0;
            var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            options.Children.Add(Labeled("Length (seconds)", _musicSeconds));
            options.Children.Add(Labeled("Lyrics language", _musicLanguage));
            panel.Children.Add(options);

            var run = new Button { Content = "Compose music" };
            AutomationProperties.SetAutomationId(run, "Create_Music_Run_Button");
            run.Click += async (_, _) => await GenerateMusicAsync();
            _runButtons.Add(run);
            panel.Children.Add(run);
            return panel;
        }

        private async Task GenerateMusicAsync()
        {
            string prompt;
            try { prompt = RequireText(_musicPrompt.Text, "Describe the music first."); }
            catch (Exception ex) { _status.Text = ex.Message; return; }

            CreateOperationTicket? ticket = BeginOperation("Composing music locally… (a 30 s song takes about half a minute once the model is loaded)");
            if (ticket == null) return;
            try
            {
                var request = new MusicGenerateRequest
                {
                    Prompt = prompt,
                    Lyrics = _musicLyrics.Text ?? string.Empty,
                    DurationSeconds = double.IsNaN(_musicSeconds.Value) ? 30 : _musicSeconds.Value,
                    Language = (_musicLanguage.SelectedItem as ComboBoxItem)?.Tag as string ?? "en",
                    Seed = -1
                };
                MediaAsset asset = await MusicClient.Current.GenerateAsync(request, ticket.Token);
                string path = await MusicClient.SaveAsync(asset, ticket.Token);
                await ArtifactCaptureService.Current.CaptureFileAsync(
                    path, "audio", string.IsNullOrWhiteSpace(asset.FileName) ? Path.GetFileName(path) : asset.FileName, "create-workspace",
                    new { operation = "music", mediaType = asset.MediaType, durationSeconds = asset.DurationSeconds, steps = asset.Steps, seed = asset.Seed },
                    markFinal: true, CancellationToken.None);
                _lastOutput.Text = "Output: " + path;
                ShowResult(path);
                SetProvenance(new MediaProvenanceRecord { EngineFamily = "Local Media Engine", Model = "ACE-Step 1.5 turbo", IsLocal = true });
                FinishOperation(ticket, "Music composed locally and registered in Artifact Workspace.");
            }
            catch (OperationCanceledException) { FinishOperation(ticket, "Music generation cancelled."); }
            catch (Exception ex) { FinishOperation(ticket, "Music generation failed: " + ex.Message); }
        }
    }
}
