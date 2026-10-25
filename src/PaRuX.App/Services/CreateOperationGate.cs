using System;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// One Create job, identified by the instance that started it. Only the ticket that began a job can end it.
    /// </summary>
    public sealed class CreateOperationTicket
    {
        internal CreateOperationTicket(CancellationTokenSource source)
        {
            Source = source;
            Token = source.Token;
        }

        internal CancellationTokenSource Source { get; }

        /// <summary>Cancelled by <see cref="CreateOperationGate.CancelActive"/> while this job is the running one.</summary>
        public CancellationToken Token { get; }
    }

    /// <summary>
    /// Single-flight gate for the Create workspace: one generation, edit, 3D, speech or transcription job at a time.
    /// A second start is refused instead of cancelling the job that is running, so cancel always reaches the job the
    /// user can see. Finishing is identity-checked, so a late unwind of an older job cannot clear a newer job.
    /// </summary>
    public sealed class CreateOperationGate
    {
        private readonly object _sync = new();
        private CreateOperationTicket? _active;

        public bool IsBusy
        {
            get
            {
                lock (_sync)
                {
                    return _active != null;
                }
            }
        }

        /// <summary>Starts a job, or returns null while another job is still running.</summary>
        public CreateOperationTicket? TryBegin()
        {
            lock (_sync)
            {
                if (_active != null) return null;
                _active = new CreateOperationTicket(new CancellationTokenSource());
                return _active;
            }
        }

        /// <summary>Ends the job the ticket started. A ticket that is not the running job changes nothing.</summary>
        public void Finish(CreateOperationTicket ticket)
        {
            ArgumentNullException.ThrowIfNull(ticket);
            lock (_sync)
            {
                if (!ReferenceEquals(_active, ticket)) return;
                _active = null;
            }
            ticket.Source.Dispose();
        }

        /// <summary>Requests cancellation of the running job. Returns false when no job is running.</summary>
        public bool CancelActive()
        {
            lock (_sync)
            {
                if (_active == null) return false;
                _active.Source.Cancel();
                return true;
            }
        }
    }
}
