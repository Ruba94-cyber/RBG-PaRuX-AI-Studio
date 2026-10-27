using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Services;
using Windows.Storage.Streams;

namespace PaRuX.Views
{
    /// <summary>
    /// "Mask inpaint" mode of Create > Image edit: the user paints (or erases) a mask over the loaded image with a brush
    /// of adjustable size, can invert or clear it, and the image plus mask go to PaRuX Core as an "inpaint" job. The mask
    /// itself lives in <see cref="InpaintMaskModel"/> and every rule and string in <see cref="CreateInpaintPresenter"/>;
    /// this file only draws the canvas and forwards pointer input.
    /// </summary>
    public sealed partial class CreateWorkspaceView
    {
        private const double InpaintDisplayMax = 480;

        private readonly StackPanel _inpaintPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly Grid _inpaintCanvas = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Image _inpaintSourceImage = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, IsHitTestVisible = false };
        private readonly Image _inpaintOverlayImage = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill, Opacity = 0.55, IsHitTestVisible = false };
        private readonly Slider _inpaintBrush = new Slider { Minimum = 1, Maximum = 100, Value = 12, Width = 240 };
        private readonly TextBlock _inpaintBrushLabel = new TextBlock { FontSize = 12, Opacity = 0.75 };
        private readonly RadioButton _inpaintPaintTool = new RadioButton { Content = "Paint", GroupName = "CreateInpaintTool", IsChecked = true };
        private readonly RadioButton _inpaintEraseTool = new RadioButton { Content = "Erase", GroupName = "CreateInpaintTool" };
        private readonly NumberBox _inpaintGrow = new NumberBox { Minimum = 0, Maximum = 64, Value = 6, SmallChange = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private readonly TextBlock _inpaintCoverage = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly StackPanel _inpaintResultStrip = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        private readonly Image _inpaintResultImage = new Image { MaxWidth = InpaintDisplayMax, MaxHeight = InpaintDisplayMax, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _inpaintResultCaption = new TextBlock { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        private InpaintMaskModel? _inpaintMask;
        private WriteableBitmap? _inpaintOverlay;
        private bool _inpaintDrawing;
        private (double X, double Y)? _inpaintLast;

        private void BuildInpaintControls(StackPanel editPanel)
        {
            AutomationProperties.SetAutomationId(_inpaintPanel, CreateInpaintPresenter.Panel);
            AutomationProperties.SetAutomationId(_inpaintCanvas, CreateInpaintPresenter.Canvas);
            AutomationProperties.SetName(_inpaintCanvas, "Mask canvas: paint over the area to replace");
            AutomationProperties.SetAutomationId(_inpaintBrush, CreateInpaintPresenter.BrushSlider);
            AutomationProperties.SetName(_inpaintBrush, "Brush size");
            AutomationProperties.SetAutomationId(_inpaintPaintTool, CreateInpaintPresenter.PaintTool);
            AutomationProperties.SetAutomationId(_inpaintEraseTool, CreateInpaintPresenter.EraseTool);
            AutomationProperties.SetAutomationId(_inpaintGrow, CreateInpaintPresenter.GrowBox);
            AutomationProperties.SetName(_inpaintGrow, "Edge blend in pixels");
            AutomationProperties.SetAutomationId(_inpaintCoverage, CreateInpaintPresenter.CoverageText);
            AutomationProperties.SetAutomationId(_inpaintResultStrip, CreateInpaintPresenter.ResultStrip);
            AutomationProperties.SetAutomationId(_inpaintResultImage, CreateInpaintPresenter.ResultImage);
            AutomationProperties.SetName(_inpaintResultImage, "Inpainted result");

            _inpaintCanvas.Children.Add(_inpaintSourceImage);
            _inpaintCanvas.Children.Add(_inpaintOverlayImage);
            _inpaintCanvas.PointerPressed += OnInpaintPointerPressed;
            _inpaintCanvas.PointerMoved += OnInpaintPointerMoved;
            _inpaintCanvas.PointerReleased += OnInpaintPointerEnd;
            _inpaintCanvas.PointerCanceled += OnInpaintPointerEnd;
            _inpaintCanvas.PointerCaptureLost += OnInpaintPointerEnd;

            _inpaintBrush.ValueChanged += (_, _) => _inpaintBrushLabel.Text = CreateInpaintPresenter.BrushLabel(_inpaintBrush.Value);
            _inpaintBrushLabel.Text = CreateInpaintPresenter.BrushLabel(_inpaintBrush.Value);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            tools.Children.Add(_inpaintPaintTool);
            tools.Children.Add(_inpaintEraseTool);
            var invert = new Button { Content = "Invert mask" };
            AutomationProperties.SetAutomationId(invert, CreateInpaintPresenter.InvertButton);
            invert.Click += (_, _) => { _inpaintMask?.Invert(); RefreshInpaintOverlay(); };
            var clear = new Button { Content = "Clear mask" };
            AutomationProperties.SetAutomationId(clear, CreateInpaintPresenter.ClearButton);
            clear.Click += (_, _) => { _inpaintMask?.Clear(); RefreshInpaintOverlay(); };
            tools.Children.Add(invert);
            tools.Children.Add(clear);

            var brushRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            brushRow.Children.Add(_inpaintBrush);
            brushRow.Children.Add(_inpaintBrushLabel);

            _inpaintPanel.Children.Add(_inpaintCanvas);
            _inpaintPanel.Children.Add(tools);
            _inpaintPanel.Children.Add(brushRow);
            _inpaintPanel.Children.Add(Labeled("Edge blend (pixels)", _inpaintGrow));
            _inpaintPanel.Children.Add(_inpaintCoverage);

            _inpaintResultStrip.Children.Add(new TextBlock { Text = "Result", FontSize = 12, Opacity = 0.68 });
            _inpaintResultStrip.Children.Add(_inpaintResultImage);
            _inpaintResultStrip.Children.Add(_inpaintResultCaption);

            editPanel.Children.Add(_inpaintPanel);
            editPanel.Children.Add(_inpaintResultStrip);
            RefreshInpaintCoverage();
        }

        private bool InpaintMode => SelectedEditType() == CreateInpaintPresenter.ModeTag;

        private void ApplyInpaintModeVisibility()
        {
            _inpaintPanel.Visibility = InpaintMode ? Visibility.Visible : Visibility.Collapsed;
            if (InpaintMode) RefreshInpaintCoverage();
        }

        /// <summary>Loads the chosen source into the canvas and starts a fresh mask with the same proportions.</summary>
        private async Task LoadInpaintSourceAsync()
        {
            _inpaintMask = null;
            _inpaintCanvas.Visibility = Visibility.Collapsed;
            _inpaintSourceImage.Source = null;
            _inpaintOverlayImage.Source = null;
            if (string.IsNullOrEmpty(_editSourcePath)) { RefreshInpaintCoverage(); return; }
            try
            {
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_editSourcePath);
                using IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
                _inpaintMask = InpaintMaskModel.ForSource(width, height);
                double scale = InpaintDisplayMax / Math.Max(width, height);
                _inpaintCanvas.Width = Math.Max(64, width * scale);
                _inpaintCanvas.Height = Math.Max(64, height * scale);
                _inpaintSourceImage.Source = bitmap;
                _inpaintOverlay = new WriteableBitmap(_inpaintMask.Width, _inpaintMask.Height);
                _inpaintOverlayImage.Source = _inpaintOverlay;
                _inpaintCanvas.Visibility = Visibility.Visible;
                RefreshInpaintOverlay();
            }
            catch (Exception ex)
            {
                _inpaintMask = null;
                _inpaintCoverage.Text = "This file could not be opened for masking: " + ex.Message;
            }
        }

        private void OnInpaintPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_inpaintMask == null) return;
            _inpaintDrawing = true;
            _inpaintCanvas.CapturePointer(e.Pointer);
            (double x, double y) = ToCell(e);
            _inpaintLast = (x, y);
            Paint(e, x, y, x, y);
            e.Handled = true;
        }

        private void OnInpaintPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_inpaintDrawing || _inpaintMask == null || _inpaintLast is not { } last) return;
            (double x, double y) = ToCell(e);
            Paint(e, last.X, last.Y, x, y);
            _inpaintLast = (x, y);
            e.Handled = true;
        }

        private void OnInpaintPointerEnd(object sender, PointerRoutedEventArgs e)
        {
            if (!_inpaintDrawing) return;
            _inpaintDrawing = false;
            _inpaintLast = null;
            try { _inpaintCanvas.ReleasePointerCaptures(); } catch { }
            RefreshInpaintCoverage();
        }

        private (double X, double Y) ToCell(PointerRoutedEventArgs e)
        {
            Windows.Foundation.Point p = e.GetCurrentPoint(_inpaintCanvas).Position;
            double w = Math.Max(1, _inpaintCanvas.ActualWidth), h = Math.Max(1, _inpaintCanvas.ActualHeight);
            return (Math.Clamp(p.X / w, 0, 1) * _inpaintMask!.Width, Math.Clamp(p.Y / h, 0, 1) * _inpaintMask.Height);
        }

        private void Paint(PointerRoutedEventArgs e, double x0, double y0, double x1, double y1)
        {
            // The right mouse button always erases, so a quick correction does not need the tool switch.
            bool erase = _inpaintEraseTool.IsChecked == true || e.GetCurrentPoint(_inpaintCanvas).Properties.IsRightButtonPressed;
            _inpaintMask!.StampLine(x0, y0, x1, y1, _inpaintMask.RadiusFor(_inpaintBrush.Value), erase);
            RefreshInpaintOverlay();
        }

        private void RefreshInpaintOverlay()
        {
            if (_inpaintMask == null || _inpaintOverlay == null) { RefreshInpaintCoverage(); return; }
            using Stream pixels = _inpaintOverlay.PixelBuffer.AsStream();
            var row = new byte[_inpaintMask.Width * 4];
            for (int y = 0; y < _inpaintMask.Height; y++)
            {
                for (int x = 0; x < _inpaintMask.Width; x++)
                {
                    bool painted = _inpaintMask[x, y] >= 128;
                    int i = x * 4;
                    row[i] = painted ? (byte)40 : (byte)0;       // B
                    row[i + 1] = painted ? (byte)40 : (byte)0;   // G
                    row[i + 2] = painted ? (byte)230 : (byte)0;  // R
                    row[i + 3] = painted ? (byte)255 : (byte)0;  // A (premultiplied)
                }
                pixels.Write(row, 0, row.Length);
            }
            _inpaintOverlay.Invalidate();
            RefreshInpaintCoverage();
        }

        private void RefreshInpaintCoverage() =>
            _inpaintCoverage.Text = CreateInpaintPresenter.Coverage(_inpaintMask != null, _inpaintMask?.PaintedFraction ?? 0);

        /// <summary>Adds the mask and edge blend to an inpaint request, or throws the reason the run must not start.</summary>
        private void ApplyInpaintRequest(PaRuX.Contracts.MediaJobRequest request)
        {
            string? blocked = CreateInpaintPresenter.BlockingReason(
                !string.IsNullOrEmpty(_editSourcePath) && _inpaintMask != null,
                string.IsNullOrWhiteSpace(_editPrompt.Text),
                _inpaintMask?.PaintedFraction ?? 0);
            if (blocked != null) throw new InvalidOperationException(blocked);
            request.MaskImageBase64 = Convert.ToBase64String(_inpaintMask!.ToPng());
            request.MaskGrowPixels = (int)Math.Clamp(double.IsNaN(_inpaintGrow.Value) ? 6 : _inpaintGrow.Value, 0, 64);
            ApplyLoraStack(request);
        }

        /// <summary>Shows the finished image in the result strip under the canvas.</summary>
        private async Task ShowInpaintResultAsync(string path)
        {
            try
            {
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                _inpaintResultImage.Source = bitmap;
                _inpaintResultCaption.Text = CreateInpaintPresenter.ResultCaption(path);
                _inpaintResultStrip.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _inpaintResultCaption.Text = "The result was saved but could not be previewed: " + ex.Message;
                _inpaintResultStrip.Visibility = Visibility.Visible;
            }
        }
    }
}
