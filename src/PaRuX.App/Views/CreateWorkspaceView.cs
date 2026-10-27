using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// One first-class surface for PaRuX's existing local media engines. Image/video/edit reuse the
    /// cancellable MediaJobService pipeline; 3D and TTS reuse their clients (which already persist
    /// and catalog successful outputs as Artifacts); STT consumes a user audio file and therefore
    /// returns text without creating a new Artifact.
    /// </summary>
    public sealed partial class CreateWorkspaceView : UserControl
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        private readonly TextBlock _lastOutput = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        private readonly TextBlock _provenanceText = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly Button _provenanceDetailsButton = new Button { Content = "Details" };
        private readonly StackPanel _provenanceDetailsPanel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 4) };
        private readonly TextBlock _provenanceDetailsContent = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 };
        // One job at a time: starting a second is refused, and the run buttons stay disabled while a job runs.
        private readonly CreateOperationGate _operations = new();
        private readonly List<Button> _runButtons = new();
        private readonly List<MediaSetupCard> _setupCards = new();

        private readonly TextBox _imagePrompt = new TextBox { PlaceholderText = "Describe the image to create", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
        private readonly NumberBox _imageWidth = DimensionBox(1024);
        private readonly NumberBox _imageHeight = DimensionBox(1024);

        private readonly TextBox _videoPrompt = new TextBox { PlaceholderText = "Describe the video to create", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
        private readonly NumberBox _videoWidth = DimensionBox(1280);
        private readonly NumberBox _videoHeight = DimensionBox(720);
        private readonly NumberBox _videoDuration = new NumberBox { Minimum = 1, Maximum = 60, Value = 5, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private readonly NumberBox _videoFps = new NumberBox { Minimum = 1, Maximum = 60, Value = 24, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private readonly NumberBox _videoSteps = new NumberBox { Minimum = 1, Maximum = 100, Value = 30, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        // Optional start image (image-to-video): becomes the first frame of clip one. Empty means text-to-video.
        private string? _videoStartImagePath;
        private readonly TextBlock _videoStartLabel = new TextBlock { Text = "No start image: text-to-video", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _videoStartCheck = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };

        private readonly TextBox _editPrompt = new TextBox { PlaceholderText = "Describe the edit", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        private readonly TextBlock _editSourceLabel = new TextBlock { Text = "No source image selected", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        private readonly ComboBox _editMode = new ComboBox { MinWidth = 220 };
        private readonly TextBlock _editModeDescription = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        private readonly StackPanel _variationStrengthPanel = new StackPanel { Spacing = 3, Visibility = Visibility.Collapsed };
        private readonly Button _editRunButton = new Button { Content = "Edit image" };
        private readonly NumberBox _editStrength = new NumberBox { Minimum = 0.05, Maximum = 1, Value = 0.6, SmallChange = 0.05, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private string? _editSourcePath;

        private readonly TextBlock _threedSourceLabel = new TextBlock { Text = "No source image selected", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        private string? _threedSourcePath;

        private readonly TextBox _ttsText = new TextBox { PlaceholderText = "Text to synthesize locally", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
        private readonly TextBox _ttsVoice = new TextBox { PlaceholderText = "Voice id (optional, e.g. if_sara)" };
        private readonly TextBlock _ttsStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };

        private readonly TextBlock _sttSourceLabel = new TextBlock { Text = "No audio file selected", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        private readonly TextBox _transcription = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, PlaceholderText = "Transcription appears here" };
        private string? _sttSourcePath;

        public CreateWorkspaceView()
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            Content = BuildUi();
            _runButtons.Add(_editRunButton);
            Unloaded += (_, _) => { _operations.CancelActive(); foreach (MediaSetupCard card in _setupCards) card.StopPolling(); };
        }

        public void OnShown()
        {
            _status.Text = CreateSetupStatusLine.Checking;
            SetProvenance(null);
            PaRuX.Services.MediaPrewarmClient.Request("create-opened");
            foreach (MediaModelRow row in new[] { _imageModelRow, _videoModelRow, _editModelRow }) _ = row.RefreshAsync();
            foreach (MediaSetupCard card in _setupCards) card.StartPolling();
        }

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 16,
                Margin = new Thickness(24),
                MaxWidth = 1080,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            body.Children.Add(new TextBlock { Text = "Create", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock
            {
                Text = "Image, video, edit, 3D and speech share one local workspace. Successful generated files are stored in PaRuX-managed output folders and registered in Artifact Workspace; user source files remain user-managed.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });

            var statusBlock = new StackPanel { Spacing = 8 };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var cancel = new Button { Content = "Cancel active job" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cancel, "Create_Cancel_Active_Job_Button");
            cancel.Click += (_, _) => CancelActive();
            top.Children.Add(cancel);
            statusBlock.Children.Add(top);
            statusBlock.Children.Add(_progress);
            statusBlock.Children.Add(_status);
            statusBlock.Children.Add(new ModelTuningStatusControl(ModelTuningStatusControl.Mode.Line, "media"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_status, "Create_Operation_Status_Text");
            statusBlock.Children.Add(_lastOutput);
            statusBlock.Children.Add(BuildResultStrip());

            var provenanceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_provenanceText, MediaProvenanceFormatter.ProvenanceTextAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_provenanceText, MediaProvenanceFormatter.MissingProvenanceNote);
            _provenanceText.Text = MediaProvenanceFormatter.MissingProvenanceNote;
            provenanceRow.Children.Add(_provenanceText);

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_provenanceDetailsButton, MediaProvenanceFormatter.ProvenanceDetailsButtonAutomationId);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_provenanceDetailsButton, "Details");
            _provenanceDetailsButton.Click += (_, _) => ToggleProvenanceDetails();
            provenanceRow.Children.Add(_provenanceDetailsButton);
            statusBlock.Children.Add(provenanceRow);

            _provenanceDetailsPanel.Children.Add(_provenanceDetailsContent);
            statusBlock.Children.Add(_provenanceDetailsPanel);

            // One mode at a time: the long single page made every mode compete for attention. Results, status and setup stay above.
            var speech = new StackPanel { Spacing = 14 };
            speech.Children.Add(NewSetupCard("speech", "speech"));
            speech.Children.Add(Section("TEXT TO SPEECH", BuildTtsPanel()));
            speech.Children.Add(Section("SPEECH TO TEXT", BuildSttPanel()));
            var gallery = new MediaGalleryView();
            gallery.ItemReusedAsEditSource += (_, item) =>
            {
                if (item.FilePath is not { Length: > 0 } filePath) return;
                _editSourcePath = filePath;
                _editSourceLabel.Text = filePath;
                SelectMode("Edit");
            };
            body.Children.Add(BuildModeTabs(new (string, UIElement)[]
            {
                ("Image", Section("IMAGE", BuildImagePanel())),
                ("Video", Section("VIDEO", BuildVideoPanel())),
                ("Music", Section("MUSIC", BuildMusicPanel())),
                ("Edit", Section("IMAGE EDIT", BuildEditPanel())),
                ("3D", Section("IMAGE → 3D", ThreedWithSetup())),
                ("Speech", speech),
                ("Workflows", Section("COMFYUI WORKFLOWS AND LORAS", BuildWorkflowLoraPanel())),
                ("Gallery", gallery)
            }));
            body.Children.Add(statusBlock);
            return scroll;
        }

        /// <summary>One-click example prompts, so a first-time user sees what to type. Clicking only fills the box.</summary>
        private static UIElement ExampleChips(TextBox target, string idPrefix, params string[] examples)
        {
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock { Text = "Try an example, or just ask in Chat (\"create an image of…\", \"crea un'immagine di…\")", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            int i = 0;
            foreach (string example in examples)
            {
                var chip = new Button { Content = example.Length > 44 ? example.Substring(0, 41) + "…" : example, Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(chip, idPrefix + "_" + (++i));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, example);
                string text = example;
                chip.Click += (_, _) => { target.Text = text; target.Focus(FocusState.Programmatic); };
                chips.Children.Add(chip);
            }
            row.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto, Content = chips });
            return row;
        }

        /// <summary>One-click aspect-ratio presets that fill the width and height boxes.</summary>
        private static UIElement SizePresets(string idPrefix, NumberBox width, NumberBox height, params (string Label, int W, int H)[] presets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = "Size", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            foreach ((string label, int w, int h) in presets)
            {
                var button = new Button { Content = label + " " + w + "×" + h, Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, idPrefix + "_" + label);
                int pw = w, ph = h;
                button.Click += (_, _) => { width.Value = pw; height.Value = ph; };
                row.Children.Add(button);
            }
            return row;
        }

        private MediaSetupCard NewSetupCard(string type, string label)
        {
            var card = new MediaSetupCard(type, label, _dispatcher);
            if (type == "image")
                card.StatusChanged += status =>
                {
                    if (_operations.IsBusy || _operationMessageSticky || !CreateSetupStatusLine.MayReplace(_status.Text)) return;
                    _status.Text = CreateSetupStatusLine.For(status.Ready);
                };
            _setupCards.Add(card);
            return card;
        }

        private UIElement BuildImagePanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("image", "image generation"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_imagePrompt, "Create_Image_Prompt_Box");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_imageWidth, "Create_Image_Width_Box");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_imageHeight, "Create_Image_Height_Box");
            panel.Children.Add(_imagePrompt);
            panel.Children.Add(ExampleChips(_imagePrompt, "Create_Image_Example",
                "A cozy cabin in a snowy forest at dusk, warm light in the windows",
                "Macro photo of a dew-covered spider web at sunrise",
                "Watercolor portrait of an old sailor with a pipe",
                "Isometric pixel-art city at night, neon signs"));
            panel.Children.Add(_enhanceImage);
            panel.Children.Add(_imageModelRow);
            var dimensions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            dimensions.Children.Add(Labeled("Width", _imageWidth));
            dimensions.Children.Add(Labeled("Height", _imageHeight));
            panel.Children.Add(dimensions);
            panel.Children.Add(BuildLoraStackPanel());
            panel.Children.Add(SizePresets("Create_Image_Size", _imageWidth, _imageHeight,
                ("Square", 1024, 1024), ("Portrait", 896, 1152), ("Landscape", 1344, 768), ("Phone", 768, 1344)));
            var run = new Button { Content = "Generate image" };
            run.Click += async (_, _) => await GenerateMediaAsync("image");
            _runButtons.Add(run);
            panel.Children.Add(run);
            return panel;
        }

        // Model + content lane per generating tab. Edit follows its mode: instruction edits use the edit checkpoint, variations the image one.
        private readonly MediaModelRow _imageModelRow = new MediaModelRow("image", "Create_Image");
        private readonly MediaModelRow _videoModelRow = new MediaModelRow("video", "Create_Video");
        private readonly MediaModelRow _editModelRow = new MediaModelRow("edit", "Create_Edit");
        private readonly CheckBox _enhanceImage = MediaPromptEnhancerUi.CreateToggle();
        private readonly CheckBox _enhanceVideo = MediaPromptEnhancerUi.CreateToggle("_Video");
        private IReadOnlyDictionary<string, string>? _pendingEnhancementDetails;

        /// <summary>Optional, default-off prompt enhancement with user review. Returns false when the user cancels.</summary>
        private async Task<bool> ApplyPromptEnhancementAsync(CheckBox toggle, MediaJobRequest request, string kind)
        {
            _pendingEnhancementDetails = null;
            if (toggle.IsChecked != true) return true;
            _status.Text = "Enhancing prompt with the local text model…";
            MediaPromptEnhancerUi.Outcome? outcome = await MediaPromptEnhancerUi.ReviewAsync(XamlRoot, kind, request.Prompt, CancellationToken.None);
            if (outcome == null) { _status.Text = "Generation cancelled."; return false; }
            request.Prompt = outcome.PromptToUse;
            request.PromptAlreadyReviewed = true; // the reviewed text runs verbatim; server skips its silent rewrite
            _pendingEnhancementDetails = outcome.Result.ToProvenanceDetails(outcome.UsedEnhanced);
            return true;
        }

        private UIElement BuildVideoPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("video", "video generation"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_videoPrompt, "Create_Video_Prompt_Box");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_videoWidth, "Create_Video_Width_Box");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_videoHeight, "Create_Video_Height_Box");
            panel.Children.Add(_videoPrompt);
            panel.Children.Add(ExampleChips(_videoPrompt, "Create_Video_Example",
                "Waves rolling onto a beach at sunset, cinematic, slow motion",
                "A paper boat drifting down a rain-soaked street",
                "Clouds racing over a mountain ridge, time-lapse"));
            panel.Children.Add(_enhanceVideo);
            panel.Children.Add(_videoModelRow);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Labeled("Width", _videoWidth));
            row.Children.Add(Labeled("Height", _videoHeight));
            panel.Children.Add(SizePresets("Create_Video_Size", _videoWidth, _videoHeight,
                ("Fast 480p", 832, 480), ("HD 720p", 1280, 720), ("Vertical", 480, 832)));
            row.Children.Add(Labeled("Seconds", _videoDuration));
            row.Children.Add(Labeled("FPS", _videoFps));
            row.Children.Add(Labeled("Steps", _videoSteps));
            panel.Children.Add(row);
            var startRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var chooseStart = new Button { Content = "Choose start image…" };
            chooseStart.Click += async (_, _) =>
            {
                _videoStartImagePath = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".webp" });
                await RefreshVideoStartImageCheckAsync();
            };
            var clearStart = new Button { Content = "Clear start image" };
            clearStart.Click += async (_, _) =>
            {
                _videoStartImagePath = null;
                await RefreshVideoStartImageCheckAsync();
            };
            startRow.Children.Add(chooseStart);
            startRow.Children.Add(clearStart);
            panel.Children.Add(startRow);
            panel.Children.Add(_videoStartLabel);
            panel.Children.Add(_videoStartCheck);
            var run = new Button { Content = "Generate video" };
            run.Click += async (_, _) => await GenerateMediaAsync("video");
            _runButtons.Add(run);
            panel.Children.Add(run);
            return panel;
        }

        // Reads the picked image's header size (no full decode) and reports whether it matches the video's aspect ratio.
        // The server checks format, size and aspect again before queueing, so this is guidance, not the gate.
        private async Task RefreshVideoStartImageCheckAsync()
        {
            if (string.IsNullOrEmpty(_videoStartImagePath))
            {
                _videoStartLabel.Text = "No start image: text-to-video";
                _videoStartCheck.Text = string.Empty;
                return;
            }
            _videoStartLabel.Text = Path.GetFileName(_videoStartImagePath);
            try
            {
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_videoStartImagePath);
                using Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                Windows.Graphics.Imaging.BitmapDecoder decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                int width = (int)decoder.PixelWidth;
                int height = (int)decoder.PixelHeight;
                int videoWidth = IntValue(_videoWidth, 1280);
                int videoHeight = IntValue(_videoHeight, 720);
                double ratioDifference = Math.Abs(((double)width / height) / ((double)videoWidth / videoHeight) - 1.0);
                _videoStartCheck.Text = ratioDifference <= 0.02
                    ? $"Start image {width}×{height} matches the {videoWidth}×{videoHeight} aspect ratio."
                    : $"Start image {width}×{height} does not match the {videoWidth}×{videoHeight} aspect ratio. Choose an image with the same proportions; PaRuX does not stretch or crop it.";
            }
            catch (Exception ex)
            {
                _videoStartCheck.Text = "This file could not be read as an image: " + ex.Message;
            }
        }

        private UIElement BuildEditPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("edit", "image editing"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_editMode, "Create_EditMode_ComboBox");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editMode, "Edit mode");
            var instructionEdit = new ComboBoxItem { Content = "Instruction edit", Tag = "edit" };
            var imageVariation = new ComboBoxItem { Content = "Image variation", Tag = "img2img" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(instructionEdit, "Create_EditMode_InstructionEdit_Item");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(imageVariation, "Create_EditMode_ImageVariation_Item");
            var maskInpaint = new ComboBoxItem { Content = CreateInpaintPresenter.ModeLabel, Tag = CreateInpaintPresenter.ModeTag };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(maskInpaint, CreateInpaintPresenter.ModeItem);
            _editMode.Items.Add(instructionEdit);
            _editMode.Items.Add(imageVariation);
            _editMode.Items.Add(maskInpaint);
            _editMode.SelectionChanged += (_, _) => UpdateEditModeDescription();
            _editMode.SelectedIndex = 0;
            panel.Children.Add(Labeled("Edit mode", _editMode));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_editModeDescription, "Create_EditMode_Description_Text");
            panel.Children.Add(_editModeDescription);
            var choose = new Button { Content = "Choose source image…" };
            choose.Click += async (_, _) =>
            {
                _editSourcePath = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".webp" });
                _editSourceLabel.Text = _editSourcePath ?? "No source image selected";
                await LoadInpaintSourceAsync();
            };
            panel.Children.Add(choose);
            panel.Children.Add(_editSourceLabel);
            BuildInpaintControls(panel);
            panel.Children.Add(_editPrompt);
            panel.Children.Add(_editModelRow);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_variationStrengthPanel, "Create_EditVariationStrength_Panel");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_editStrength, "Create_EditVariationStrength_NumberBox");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_editStrength, "Variation strength");
            _variationStrengthPanel.Children.Add(new TextBlock { Text = "Variation strength", FontSize = 11, Opacity = 0.68 });
            _variationStrengthPanel.Children.Add(_editStrength);
            panel.Children.Add(_variationStrengthPanel);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_editRunButton, "Create_Edit_Run_Button");
            _editRunButton.Click += async (_, _) => await GenerateMediaAsync(SelectedEditType());
            panel.Children.Add(_editRunButton);
            panel.Children.Add(new TextBlock { Text = "Or cut the subject out and keep a transparent background:", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(BuildCutoutControls());
            UpdateEditModeDescription();
            return panel;
        }

        private string SelectedEditType() => (_editMode.SelectedItem as ComboBoxItem)?.Tag as string switch
        {
            "img2img" => "img2img",
            CreateInpaintPresenter.ModeTag => CreateInpaintPresenter.ModeTag,
            _ => "edit"
        };

        private void UpdateEditModeDescription()
        {
            bool variation = SelectedEditType() == "img2img";
            bool inpaint = SelectedEditType() == CreateInpaintPresenter.ModeTag;
            _ = _editModelRow.SetTypeAsync(variation ? "image" : "edit");
            _variationStrengthPanel.Visibility = variation ? Visibility.Visible : Visibility.Collapsed;
            _editRunButton.Content = variation ? "Create variation" : inpaint ? "Repaint masked area" : "Edit image";
            _editModeDescription.Text = variation
                ? "Create a variation of your source image. Strength controls how far the result can move from the original."
                : inpaint
                    ? CreateInpaintPresenter.ModeDescription
                    : "Describe the changes you want. The source image guides the edit while your instructions shape the result.";
            ApplyInpaintModeVisibility();
        }

        private UIElement BuildThreedPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Uses the managed local 3D engine. If the engine is not installed, Runtime/Settings can install it before generation.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
            var choose = new Button { Content = "Choose source image…" };
            choose.Click += async (_, _) =>
            {
                _threedSourcePath = await PickFileAsync(new[] { ".png", ".jpg", ".jpeg", ".webp" });
                _threedSourceLabel.Text = _threedSourcePath ?? "No source image selected";
            };
            panel.Children.Add(choose);
            panel.Children.Add(_threedSourceLabel);
            var run = new Button { Content = "Generate 3D model" };
            run.Click += async (_, _) => await GenerateThreedAsync();
            _runButtons.Add(run);
            panel.Children.Add(run);
            return panel;
        }

        private UIElement BuildTtsPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_ttsText, "Create_TTS_Text_Box");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_ttsVoice, "Create_TTS_Voice_Box");
            panel.Children.Add(_ttsText);
            panel.Children.Add(_ttsVoice);
            var run = new Button { Content = "Synthesize speech" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(run, "Create_TTS_Run_Button");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_ttsStatus, SpeechTimingPresenter.StatusTextAutomationId);
            run.Click += async (_, _) => await SynthesizeAsync();
            _runButtons.Add(run);
            panel.Children.Add(run);
            panel.Children.Add(_ttsStatus);
            return panel;
        }

        private UIElement BuildSttPanel()
        {
            var panel = new StackPanel { Spacing = 8 };
            var choose = new Button { Content = "Choose audio file…" };
            choose.Click += async (_, _) =>
            {
                _sttSourcePath = await PickFileAsync(new[] { ".wav", ".mp3", ".m4a", ".flac", ".ogg" });
                _sttSourceLabel.Text = _sttSourcePath ?? "No audio file selected";
            };
            panel.Children.Add(choose);
            panel.Children.Add(_sttSourceLabel);
            var run = new Button { Content = "Transcribe" };
            run.Click += async (_, _) => await TranscribeAsync();
            _runButtons.Add(run);
            panel.Children.Add(run);
            panel.Children.Add(_transcription);
            return panel;
        }

        private async Task GenerateMediaAsync(string type)
        {
            CreateOperationTicket? ticket = null;
            try
            {
                var request = new MediaJobRequest { Type = type, Seed = -1 };
                if (type == "image")
                {
                    request.Prompt = RequireText(_imagePrompt.Text, "An image prompt is required.");
                    if (!await ApplyPromptEnhancementAsync(_enhanceImage, request, "image")) return;
                    request.Width = IntValue(_imageWidth, 1024);
                    request.Height = IntValue(_imageHeight, 1024);
                    ApplyLoraStack(request);
                }
                else if (type == "video")
                {
                    request.Prompt = RequireText(_videoPrompt.Text, "A video prompt is required.");
                    if (!await ApplyPromptEnhancementAsync(_enhanceVideo, request, "video")) return;
                    request.Width = IntValue(_videoWidth, 1280);
                    request.Height = IntValue(_videoHeight, 720);
                    request.DurationSeconds = Math.Clamp(_videoDuration.Value, 1, 60);
                    request.Fps = IntValue(_videoFps, 24);
                    request.Steps = IntValue(_videoSteps, 30);
                    if (!string.IsNullOrEmpty(_videoStartImagePath))
                    {
                        byte[] start = await File.ReadAllBytesAsync(_videoStartImagePath);
                        request.SourceImageBase64 = Convert.ToBase64String(start);
                    }
                }
                else if (type is "edit" or "img2img" or "inpaint")
                {
                    string source = _editSourcePath ?? throw new InvalidOperationException("Choose a source image first.");
                    if (type == "inpaint") ApplyInpaintRequest(request);
                    request.Prompt = RequireText(_editPrompt.Text, type == "inpaint" ? "Describe what should appear in the painted area." : "Describe the image edit first.");
                    byte[] input = await File.ReadAllBytesAsync(source);
                    request.SourceImageBase64 = Convert.ToBase64String(input);
                    if (type == "img2img") request.Strength = Math.Clamp(_editStrength.Value, 0.05, 1);
                }
                else throw new InvalidOperationException("Unknown media generation mode.");

                ticket = BeginOperation(type == "video" ? "Generating video…" : type == "image" ? "Generating image…" : type == "inpaint" ? "Repainting masked area…" : "Editing image…");
                if (ticket == null) return;
                MediaJobProvenance? jobProvenance = null;
                var progress = new Progress<MediaJobStatus>(job =>
                {
                    if (job.Provenance != null) jobProvenance = job.Provenance;
                    _progress.Value = Math.Clamp(job.ProgressPercent, 0, 100);
                    string eta = job.EtaSeconds.HasValue ? " · ETA ~" + job.EtaSeconds.Value + "s" : string.Empty;
                    _status.Text = job.Stage + " · " + job.ProgressPercent + "%" + eta;
                });
                MediaAsset asset = await RunWithCompanionConsentAsync(request, type, progress, ticket.Token);
                string path = await SaveMediaAssetAsync(asset, type, ticket.Token);
                _lastOutput.Text = "Output: " + path;
                ShowResult(path);
                _status.Text = "Generation completed locally and registered in Artifact Workspace.";
                if (type == "inpaint") await ShowInpaintResultAsync(path);
                // The model the router/override actually chose comes from the server's job provenance; the output file name is only a fallback.
                string modelName = !string.IsNullOrWhiteSpace(jobProvenance?.ModelFileName) ? jobProvenance!.ModelFileName!
                    : !string.IsNullOrWhiteSpace(asset.FileName) ? asset.FileName : Path.GetFileName(path);
                SetProvenance(new MediaProvenanceRecord
                {
                    EngineFamily = "Local Media Engine",
                    Model = modelName,
                    IsLocal = true,
                    SelectionReason = jobProvenance?.ModelSelectionReason,
                    AdditionalDetails = _pendingEnhancementDetails
                });
                _pendingEnhancementDetails = null;
                FinishOperation(ticket);
            }
            catch (OperationCanceledException) { FinishOperation(ticket, "Generation cancelled."); }
            catch (MediaDependencyJobException ex) { FinishOperation(ticket, "Generation failed: " + ex.Message); }
            catch (Exception ex) { FinishOperation(ticket, "Generation failed: " + ex.Message); }
        }

        private async Task<MediaAsset> RunWithCompanionConsentAsync(
            MediaJobRequest request,
            string jobType,
            IProgress<MediaJobStatus> progress,
            CancellationToken ct)
        {
            try
            {
                return await MediaJobClient.Current.RunAsync(request, progress, ct);
            }
            catch (MediaDependencyJobException failure)
            {
                MediaDependencyError error = failure.ErrorDetail;
                string? companionType = MediaCompanionRemediationText.CompanionTypeForJob(jobType);
                if (companionType == null || error.UpdateRequired
                    || !string.Equals(error.Reason, "component_missing", StringComparison.Ordinal))
                    throw;

                MediaCompanionPlan plan;
                try
                {
                    plan = await MediaJobClient.Current.GetCompanionPlanAsync(companionType, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new MediaDependencyJobException(error, "The companion plan could not be listed: " + ex.Message);
                }

                if (!MediaCompanionRemediationText.CanOfferFetch(error, plan, companionType))
                    throw new MediaDependencyJobException(error, MediaCompanionRemediationText.FormatPlan(plan));

                ct.ThrowIfCancellationRequested();
                var content = new ScrollViewer
                {
                    MaxHeight = 440,
                    Content = new TextBlock
                    {
                        Text = MediaCompanionRemediationText.BuildConsentMessage(error, plan),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                var dialog = new ContentDialog
                {
                    Title = "Missing media components",
                    Content = content,
                    PrimaryButtonText = "Fetch and retry once",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                using CancellationTokenRegistration closeOnCancel = ct.Register(() =>
                {
                    _ = DispatcherQueue.TryEnqueue(() =>
                    {
                        try { dialog.Hide(); }
                        catch { }
                    });
                });
                ContentDialogResult choice = await dialog.ShowAsync();
                ct.ThrowIfCancellationRequested();
                if (choice != ContentDialogResult.Primary)
                    throw new MediaDependencyJobException(error, "No companion files were fetched because consent was not given.");

                MediaCompanionFetchResult fetched;
                try
                {
                    fetched = await MediaJobClient.Current.FetchCompanionsAsync(plan, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new MediaDependencyJobException(error, "The companion fetch could not be completed: " + ex.Message);
                }

                if (!fetched.Ok)
                {
                    string failedStep = string.IsNullOrWhiteSpace(fetched.FailedStep) ? string.Empty : " Failed step: " + fetched.FailedStep + ".";
                    throw new MediaDependencyJobException(error, "Companion fetch failed: " + fetched.Message + failedStep);
                }

                if (!MediaCompanionRemediationText.ShouldRetryAfterFetch(fetched.Ok, retryAlreadyAttempted: false))
                    throw new MediaDependencyJobException(error, "The job was not retried because the companion fetch did not complete.");

                _progress.Value = 0;
                _status.Text = "Companions installed. Retrying the media job once…";
                return await MediaJobClient.Current.RunAsync(request, progress, ct);
            }
        }

        private async Task<string> SaveMediaAssetAsync(MediaAsset asset, string type, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(asset.Base64Data)) throw new InvalidDataException("PaRuX Core returned no media bytes.");
            byte[] bytes = Convert.FromBase64String(asset.Base64Data);
            AppPaths.EnsureCreated();
            StorageQuotaService.EnsureCapacity(bytes.LongLength);

            bool video = type == "video" || asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            string directory = video ? AppPaths.GeneratedVideosDir : AppPaths.GeneratedImagesDir;
            string extension = video
                ? string.Equals(asset.MediaType, "video/webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4"
                : string.Equals(asset.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + extension);
            await File.WriteAllBytesAsync(path, bytes, ct);

            await ArtifactCaptureService.Current.CaptureFileAsync(
                path,
                video ? "video" : "image",
                string.IsNullOrWhiteSpace(asset.FileName) ? Path.GetFileName(path) : asset.FileName,
                "create-workspace",
                new
                {
                    operation = type,
                    mediaType = asset.MediaType,
                    width = asset.Width,
                    height = asset.Height,
                    durationSeconds = asset.DurationSeconds,
                    fps = asset.Fps,
                    steps = asset.Steps,
                    seed = asset.Seed
                },
                markFinal: true,
                CancellationToken.None);
            return path;
        }

        private async Task GenerateThreedAsync()
        {
            string source = _threedSourcePath ?? string.Empty;
            if (source.Length == 0) { _status.Text = "Choose a source image first."; return; }
            CreateOperationTicket? ticket = BeginOperation("Generating local 3D model…");
            if (ticket == null) return;
            try
            {
                ThreedGenerateResponse result = await ThreedClient.Current.GenerateAsync(source, ticket.Token);
                _status.Text = "3D generation completed and registered in Artifact Workspace.";
                _lastOutput.Text = "Generated: " + (string.IsNullOrWhiteSpace(result.FileName) ? "model.glb" : result.FileName)
                    + " · " + result.DurationSeconds.ToString("0.0") + "s";
                FinishOperation(ticket);
            }
            catch (OperationCanceledException) { FinishOperation(ticket, "3D generation cancelled."); }
            catch (Exception ex) { FinishOperation(ticket, "3D generation failed: " + ex.Message); }
        }

        private async Task SynthesizeAsync()
        {
            string text;
            try { text = RequireText(_ttsText.Text, "Enter text to synthesize."); }
            catch (Exception ex) { _status.Text = ex.Message; return; }

            string runningMessage = SpeechTimingPresenter.FormatGenerating();
            _ttsStatus.Text = runningMessage;
            CreateOperationTicket? ticket = BeginOperation(runningMessage);
            if (ticket == null)
            {
                _ttsStatus.Text = _status.Text;
                return;
            }

            try
            {
                SpeechSynthesisArtifactResult result = await SpeechClient.Current.SynthesizeStreamToArtifactAsync(
                    text,
                    (_ttsVoice.Text ?? string.Empty).Trim(),
                    ticket.Token);

                _ttsStatus.Text = SpeechTimingPresenter.FormatTtfa(result.TtfaMilliseconds);
                _status.Text = result.ArtifactRegistered
                    ? "Speech synthesis completed and registered in Artifact Workspace."
                    : "Speech synthesis completed and saved locally, but Artifact Workspace registration failed.";
                _lastOutput.Text = "Generated: " + (string.IsNullOrWhiteSpace(result.FileName) ? "speech.wav" : result.FileName)
                    + " · " + result.DurationSeconds.ToString("0.0") + "s";
                FinishOperation(ticket);
            }
            catch (OperationCanceledException)
            {
                string cancelledMessage = SpeechTimingPresenter.FormatCancelled();
                _ttsStatus.Text = cancelledMessage;
                FinishOperation(ticket, cancelledMessage);
            }
            catch (Exception ex)
            {
                string failedMessage = SpeechTimingPresenter.FormatFailed(ex.Message);
                _ttsStatus.Text = failedMessage;
                FinishOperation(ticket, failedMessage);
            }
        }

        private async Task TranscribeAsync()
        {
            string source = _sttSourcePath ?? string.Empty;
            if (source.Length == 0) { _status.Text = "Choose an audio file first."; return; }
            CreateOperationTicket? ticket = BeginOperation("Transcribing locally…");
            if (ticket == null) return;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(source, ticket.Token);
                TranscribeResponse result = await SpeechClient.Current.TranscribeAsync(
                    Convert.ToBase64String(bytes), Path.GetFileName(source), ticket.Token);
                _transcription.Text = result.Text;
                _status.Text = "Transcription completed locally"
                    + (string.IsNullOrWhiteSpace(result.LanguageDetected) ? "." : " · language " + result.LanguageDetected + ".");
                _lastOutput.Text = "Transcribed: " + Path.GetFileName(source) + " · " + result.DurationSeconds.ToString("0.0") + "s";
                FinishOperation(ticket);
            }
            catch (OperationCanceledException) { FinishOperation(ticket, "Transcription cancelled."); }
            catch (Exception ex) { FinishOperation(ticket, "Transcription failed: " + ex.Message); }
        }

        /// <summary>
        /// Starts a job and locks the run buttons. Returns null, and leaves the running job untouched, when another
        /// job is still running.
        /// </summary>
        private CreateOperationTicket? BeginOperation(string message)
        {
            CreateOperationTicket? ticket = _operations.TryBegin();
            if (ticket == null)
            {
                _status.Text = "A Create job is still running. Cancel it first, or wait for it to finish.";
                return null;
            }
            _operationMessageSticky = false;
            _progress.Value = 0;
            _progress.Visibility = Visibility.Visible;
            SetRunButtonsEnabled(false);
            _status.Text = message;
            return ticket;
        }

        /// <summary>
        /// Ends the job the ticket started and unlocks the run buttons once no job is running. A null ticket means the
        /// job never started (its input was refused), so the running job, if any, keeps its progress and buttons.
        /// </summary>
        private void FinishOperation(CreateOperationTicket? ticket, string? message = null)
        {
            if (ticket != null) _operations.Finish(ticket);
            if (!_operations.IsBusy)
            {
                _progress.Visibility = Visibility.Collapsed;
                SetRunButtonsEnabled(true);
            }
            if (!string.IsNullOrWhiteSpace(message))
            {
                _status.Text = message;
                // The result of the last job stays on screen: the setup card's periodic refresh must not replace it with "Create is ready".
                _operationMessageSticky = true;
            }
        }

        private bool _operationMessageSticky;

        private void SetRunButtonsEnabled(bool enabled)
        {
            foreach (Button button in _runButtons)
            {
                button.IsEnabled = enabled;
            }
        }

        private void CancelActive()
        {
            if (!_operations.CancelActive()) { _status.Text = "No Create job is active."; return; }
            _status.Text = "Cancellation requested…";
        }

        public void SetProvenance(MediaProvenanceRecord? record)
        {
            MediaProvenancePresentation presentation = MediaProvenanceFormatter.Format(record);
            _provenanceText.Text = presentation.Line;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_provenanceText, presentation.Line);
            _provenanceDetailsContent.Text = presentation.Details.Count > 0
                ? string.Join(Environment.NewLine, presentation.Details)
                : presentation.Line;
        }

        private void ToggleProvenanceDetails()
        {
            _provenanceDetailsPanel.Visibility = _provenanceDetailsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async Task<string?> PickFileAsync(string[] extensions)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (string extension in extensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
            return file?.Path;
        }

        private static Border Section(string title, UIElement content)
        {
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            body.Children.Add(content);
            return new Border { Padding = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = body };
        }

        private static StackPanel Labeled(string label, Control control)
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.68 });
            if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control)))
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
            panel.Children.Add(control);
            return panel;
        }

        private static NumberBox DimensionBox(double value) => new NumberBox
        {
            Minimum = 256,
            Maximum = 3840,
            Value = value,
            SmallChange = 64,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };

        private static int IntValue(NumberBox box, int fallback) =>
            double.IsNaN(box.Value) ? fallback : Math.Max(1, (int)Math.Round(box.Value));

        private static string RequireText(string? value, string error)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) throw new InvalidOperationException(error);
            return text;
        }
    }
}
