using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// The guided "Set up image generation / video generation / image editing" card of Create. It reads the server's media setup
    /// state, hides itself once everything is installed and verified, and otherwise offers the one button that sets it up.
    /// </summary>
    public sealed class MediaSetupCard : StackPanel
    {
        private readonly string _type;
        private readonly string _label;
        private readonly DispatcherQueue _dispatcher;
        private readonly TextBlock _headline = new TextBlock { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        private readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
        private readonly Button _button = new Button();
        private CancellationTokenSource? _pollCts;
        private MediaSetupStatus? _status;

        public bool IsReady => _status?.Ready == true;
        public event Action<MediaSetupStatus>? StatusChanged;

        public MediaSetupCard(string type, string label, DispatcherQueue dispatcher)
        {
            _type = type;
            _label = label;
            _dispatcher = dispatcher;
            Spacing = 8;
            Padding = new Thickness(16);
            Visibility = Visibility.Collapsed;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            string id = "Create_Setup_" + type;
            AutomationProperties.SetAutomationId(this, id + "_Card");
            AutomationProperties.SetAutomationId(_headline, id + "_Headline");
            AutomationProperties.SetAutomationId(_detail, id + "_Detail");
            AutomationProperties.SetAutomationId(_progress, id + "_Progress");
            AutomationProperties.SetAutomationId(_button, id + "_Button");
            _button.Click += async (_, _) => await ButtonClickedAsync();
            Children.Add(_headline);
            Children.Add(_detail);
            Children.Add(_progress);
            Children.Add(_button);
        }

        public void StartPolling()
        {
            StopPolling();
            var cts = _pollCts = new CancellationTokenSource();
            _ = PollAsync(cts.Token);
        }

        public void StopPolling()
        {
            _pollCts?.Cancel();
            _pollCts = null;
        }

        private async Task PollAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    MediaSetupStatus status = await MediaSetupClient.Current.GetAsync(_type, ct).ConfigureAwait(false);
                    _dispatcher.TryEnqueue(() => Apply(status));
                    await Task.Delay(status.State == "running" ? 1500 : 5000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        Visibility = Visibility.Visible;
                        _headline.Text = "The " + _label + " setup status is unavailable";
                        _detail.Text = ex.Message;
                    });
                    try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
            }
        }

        private void Apply(MediaSetupStatus status)
        {
            _status = status;
            MediaSetupCardModel card = MediaSetupPresenter.Present(status, _label);
            Visibility = card.Visible ? Visibility.Visible : Visibility.Collapsed;
            _headline.Text = card.Headline;
            _detail.Text = card.Detail;
            _button.Content = card.ButtonLabel;
            _button.IsEnabled = card.ButtonEnabled;
            _progress.Visibility = card.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            _progress.IsIndeterminate = card.Indeterminate;
            if (!card.Indeterminate) _progress.Value = card.Progress;
            StatusChanged?.Invoke(status);
        }

        private async Task ButtonClickedAsync()
        {
            try
            {
                MediaSetupStatus next = _status?.State == "running"
                    ? await MediaSetupClient.Current.CancelAsync(_type, CancellationToken.None)
                    : await MediaSetupClient.Current.StartAsync(_type, CancellationToken.None);
                Apply(next);
            }
            catch (Exception ex)
            {
                _detail.Text = "The setup request failed: " + ex.Message;
            }
        }
    }
}
