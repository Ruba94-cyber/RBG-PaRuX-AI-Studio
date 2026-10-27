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
    /// <summary>Create &gt; Edit &gt; Remove background: BiRefNet-lite cutout, locally, returning a transparent PNG.</summary>
    public sealed partial class CreateWorkspaceView
    {
        private readonly Button _removeBackgroundButton = new Button { Content = "Remove background" };

        private UIElement BuildCutoutControls()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("cutout", "background removal"));
            AutomationProperties.SetAutomationId(_removeBackgroundButton, "Create_Edit_RemoveBackground_Button");
            ToolTipService.SetToolTip(_removeBackgroundButton, "Cuts the subject out of the source image and saves a transparent PNG. Runs on this PC.");
            _removeBackgroundButton.Click += async (_, _) => await RemoveBackgroundAsync(_editSourcePath);
            _runButtons.Add(_removeBackgroundButton);
            panel.Children.Add(_removeBackgroundButton);
            return panel;
        }

        private async Task RemoveBackgroundAsync(string? sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) || !IsImageFile(sourcePath))
            {
                _status.Text = "Choose a source image first (PNG, JPEG or WebP).";
                return;
            }
            CreateOperationTicket? ticket = BeginOperation("Removing the background locally… (a few seconds to a minute on the CPU)");
            if (ticket == null) return;
            try
            {
                MediaAsset asset = await CutoutClient.Current.RemoveAsync(sourcePath, ticket.Token);
                string path = await CutoutClient.SaveAsync(asset, null, ticket.Token);
                await ArtifactCaptureService.Current.CaptureFileAsync(
                    path, "image", string.IsNullOrWhiteSpace(asset.FileName) ? Path.GetFileName(path) : asset.FileName, "create-workspace",
                    new { operation = "cutout", source = Path.GetFileName(sourcePath), mediaType = asset.MediaType, width = asset.Width, height = asset.Height, seconds = asset.DurationSeconds },
                    markFinal: true, CancellationToken.None);
                _lastOutput.Text = "Output: " + path;
                ShowResult(path);
                SetProvenance(new MediaProvenanceRecord { EngineFamily = "Local Cutout Engine", Model = "BiRefNet-lite (ONNX, MIT)", IsLocal = true });
                FinishOperation(ticket, "Background removed locally; the transparent PNG is registered in Artifact Workspace.");
            }
            catch (OperationCanceledException) { FinishOperation(ticket, "Background removal cancelled."); }
            catch (Exception ex) { FinishOperation(ticket, "Background removal failed: " + ex.Message); }
        }
    }
}
