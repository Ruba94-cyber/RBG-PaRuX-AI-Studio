using System;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>Coordinates UI-only gaming behavior around the shared LocalAI marker.</summary>
    public sealed class GamingManager
    {
        public static GamingManager Current { get; } = new GamingManager();

        private bool _started;
        private bool _active;
        private int _refreshing;

        public bool IsActive => _active || LocalAiController.Current.IsGameActive();
        public event EventHandler? StateChanged;

        private GamingManager() { }

        public void Start()
        {
            if (_started) return;
            _started = true;
            LocalAiController.Current.StateChanged += OnLocalAiStateChanged;
            _ = RefreshAsync();
        }

        /// <summary>Starts Gaming Mode. A refusal comes back in the result with the server's reason, so the page can show it.</summary>
        public async Task<LocalAiController.ActionResult> EnterAsync(CancellationToken ct)
        {
            LocalAiController.ActionResult result = await LocalAiController.Current.RunActionAsync("GameStart", ct);
            await RefreshAsync();
            return result;
        }

        /// <summary>Ends Gaming Mode. A refusal comes back in the result with the server's reason, so the page can show it.</summary>
        public async Task<LocalAiController.ActionResult> ExitAsync(CancellationToken ct)
        {
            LocalAiController.ActionResult result = await LocalAiController.Current.RunActionAsync("GameStop", ct);
            await RefreshAsync();
            return result;
        }

        private void OnLocalAiStateChanged(object? sender, EventArgs e)
        {
            // LocalAiController raises this from its FileSystemWatcher callback thread,
            // not the UI thread. Everything RefreshAsync touches (ChatSession, AppWindow,
            // BubbleWin) is UI-thread-affine, so we must hop to the UI thread BEFORE
            // calling it rather than branching per-call inside RefreshAsync. Doing it
            // there let a background-thread call reach AppWindow/ChatSession directly,
            // which throws a WinRT exception with an empty Message (see PersistPosition
            // failed / StateChanged handler threw in parux.log at 16:24:38) and could
            // silently skip hiding the bubble or cancelling in-flight work on GameStart.
            var dispatcher = App.MainWin?.DispatcherQueue;
            if (dispatcher == null) return;
            if (dispatcher.HasThreadAccess) _ = RefreshAsync();
            else dispatcher.TryEnqueue(() => _ = RefreshAsync());
        }

        private Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) != 0) return Task.CompletedTask;
            try
            {
                bool active = LocalAiController.Current.IsGameActive();
                if (_active == active) return Task.CompletedTask;
                _active = active;

                if (active)
                {
                    ChatSession.Current.Cancel();
                    ChatSession.Current.Persist();
                }

                // Always called on the UI thread now (see OnLocalAiStateChanged); EnterAsync/
                // ExitAsync are awaited from UI button handlers, so this holds there too.
                if (active) App.BubbleWin?.HideBubble();
                else App.BubbleWin?.ApplyVisibilityMode();

                try { StateChanged?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { Log.Write("Gaming state handler failed: " + ex.GetType().Name + ": " + ex.Message); }
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
            return Task.CompletedTask;
        }
    }
}
