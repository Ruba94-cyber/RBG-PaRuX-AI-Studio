using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PaRuX.Views
{
    /// <summary>The result strip of Create: a large inline preview of the last image plus the next things a person does with it.</summary>
    public sealed partial class CreateWorkspaceView
    {
        private readonly Image _resultImage = new Image { MaxHeight = 520, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        private readonly StackPanel _resultActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly MediaPlayerElement _resultAudio = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 720, Visibility = Visibility.Collapsed
        };
        private readonly System.Collections.Generic.List<Button> _imageOnlyResultButtons = new();
        private string? _resultPath;

        private static bool IsAudioFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".flac" or ".mp3" or ".wav" or ".ogg" or ".opus" or ".m4a";
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
        }

        private UIElement ThreedWithSetup()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("3d", "image to 3D"));
            panel.Children.Add(BuildThreedPanel());
            return panel;
        }

        private UIElement BuildResultStrip()
        {
            var panel = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(_resultImage, "Create_Result_Image");
            AutomationProperties.SetName(_resultImage, "Generated result");
            panel.Children.Add(_resultImage);
            AutomationProperties.SetAutomationId(_resultAudio, "Create_Result_Audio");
            AutomationProperties.SetName(_resultAudio, "Generated audio player");
            panel.Children.Add(_resultAudio);
            _resultActions.Children.Add(ResultButton("Open", "Create_Result_Open_Button", (_, _) => LaunchResult(false)));
            _resultActions.Children.Add(ResultButton("Show in folder", "Create_Result_Folder_Button", (_, _) => LaunchResult(true)));
            _imageOnlyResultButtons.Add(ResultButton("Edit this image", "Create_Result_Edit_Button", (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                _editSourcePath = _resultPath;
                _editSourceLabel.Text = _resultPath;
                _status.Text = "Result set as the source image in IMAGE EDIT below.";
            }));
            _imageOnlyResultButtons.Add(ResultButton("Animate this image", "Create_Result_Animate_Button", (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                _videoStartImagePath = _resultPath;
                _videoStartLabel.Text = "Start image: " + Path.GetFileName(_resultPath);
                _status.Text = "Result set as the first frame in VIDEO below.";
            }));
            _imageOnlyResultButtons.Add(ResultButton("Make 3D", "Create_Result_3D_Button", (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                _threedSourcePath = _resultPath;
                _threedSourceLabel.Text = _resultPath;
                SelectMode("3D");
                _status.Text = "Result set as the source image in IMAGE → 3D.";
            }));
            foreach (Button b in _imageOnlyResultButtons) _resultActions.Children.Add(b);
            panel.Children.Add(_resultActions);
            return panel;
        }

        private static Button ResultButton(string label, string automationId, RoutedEventHandler click)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += click;
            return button;
        }

        private void ShowResult(string path)
        {
            _resultPath = path;
            _resultActions.Visibility = Visibility.Visible;
            bool audio = IsAudioFile(path) && File.Exists(path);
            _resultAudio.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            if (audio)
            {
                try { _resultAudio.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path)); }
                catch (Exception ex) when (ex is UriFormatException or IOException) { _resultAudio.Visibility = Visibility.Collapsed; }
            }
            else _resultAudio.Source = null;
            foreach (Button b in _imageOnlyResultButtons) b.Visibility = IsImageFile(path) ? Visibility.Visible : Visibility.Collapsed;
            bool image = IsImageFile(path) && File.Exists(path);
            _resultImage.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            if (!image) { _resultImage.Source = null; return; }
            try { _resultImage.Source = new BitmapImage(new Uri(path)); }
            catch (Exception ex) when (ex is UriFormatException or IOException) { _resultImage.Visibility = Visibility.Collapsed; }
        }

        private void LaunchResult(bool inFolder)
        {
            if (_resultPath == null || !File.Exists(_resultPath)) { _status.Text = "The result file is no longer at " + _resultPath; return; }
            try
            {
                var start = inFolder
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + _resultPath + "\"")
                    : new ProcessStartInfo(_resultPath) { UseShellExecute = true };
                Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _status.Text = "Could not open the result: " + ex.Message;
            }
        }
    }
}
