using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Tells Core to start ComfyUI ahead of the first media job (Create opened, or a chat message that asks for an image or video).
    /// Core decides: Gaming Mode, a busy GPU, RAM or VRAM pressure all refuse, and the call never waits for ComfyUI. This is a hint
    /// only, so every failure is swallowed; the media job itself still starts what it needs.
    /// </summary>
    public static class MediaPrewarmClient
    {
        private static readonly HttpClient Http = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(10));
        private static long _lastRequestTicks;
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

        public static void Request(string reason)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastRequestTicks);
            if (last != 0 && now - last < MinInterval.Ticks) return;
            if (Interlocked.CompareExchange(ref _lastRequestTicks, now, last) != last) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    ServerBootstrap bootstrap = await PaRuXServerController.Current.EnsureRunningAsync(null, CancellationToken.None).ConfigureAwait(false);
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        bootstrap.Endpoint.TrimEnd('/') + "/api/v1/media/prewarm?reason=" + Uri.EscapeDataString(reason));
                    request.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                    using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex) { Log.Write("Media prewarm hint failed: " + ex.Message); }
            });
        }
    }
}
