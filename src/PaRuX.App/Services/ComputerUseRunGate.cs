using System;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// Process-wide single-flight gate for Computer Use. The mouse and keyboard are one shared device: two
    /// runs interleaving clicks and keystrokes would each act inside the other's window. Only one run may
    /// hold the gate at a time. A run that cannot take it is refused before it sends any input, and the
    /// caller decides whether to report that to the user or to retry later.
    /// </summary>
    internal sealed class ComputerUseRunGate
    {
        private readonly SemaphoreSlim _semaphore;

        public ComputerUseRunGate(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
        }

        public static ComputerUseRunGate Shared { get; } = new ComputerUseRunGate(new SemaphoreSlim(1, 1));

        /// <summary>True while some run holds the gate. Advisory only: take it with <see cref="TryEnter"/>.</summary>
        public bool IsBusy => _semaphore.CurrentCount == 0;

        /// <summary>Takes the gate without waiting. Returns null while another run holds it.</summary>
        public Lease? TryEnter() => _semaphore.Wait(0) ? new Lease(_semaphore) : null;

        /// <summary>Holds the gate until disposed. Disposing a second time releases nothing, so a stale
        /// handle can never free a gate that a later run has taken.</summary>
        public sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _semaphore;

            internal Lease(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                SemaphoreSlim? held = Interlocked.Exchange(ref _semaphore, null);
                held?.Release();
            }
        }
    }
}
