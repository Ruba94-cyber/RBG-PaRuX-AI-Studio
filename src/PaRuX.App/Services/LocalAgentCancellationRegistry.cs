using System;
using System.Collections.Generic;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// Process-local cancellation registry for agent loops that do not flow through the server
    /// orchestrator. Registration/unregistration and cancellation are serialized by one gate so
    /// CancelAll can safely race an async loop finishing on a pool thread: a caller unregisters
    /// before disposing its CancellationTokenSource, and Unregister cannot pass the gate while a
    /// STOP operation is signaling that same source.
    /// </summary>
    public static class LocalAgentCancellationRegistry
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Guid, CancellationTokenSource> Active = new Dictionary<Guid, CancellationTokenSource>();

        public static Guid Register(CancellationTokenSource cts)
        {
            if (cts == null) throw new ArgumentNullException(nameof(cts));
            Guid id = Guid.NewGuid();
            lock (Gate) Active[id] = cts;
            return id;
        }

        public static void Unregister(Guid id)
        {
            lock (Gate) Active.Remove(id);
        }

        /// <summary>
        /// Cancels every currently registered local loop and returns the number signaled. The gate
        /// deliberately remains held while Cancel() is called; this prevents a finishing loop from
        /// unregistering and disposing the CTS between snapshot and cancellation.
        /// </summary>
        public static int CancelAll()
        {
            int cancelled = 0;
            lock (Gate)
            {
                foreach (CancellationTokenSource cts in Active.Values)
                {
                    try
                    {
                        cts.Cancel();
                        cancelled++;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Defensive only: well-behaved call sites unregister before Dispose().
                    }
                }
            }
            return cancelled;
        }
    }
}
