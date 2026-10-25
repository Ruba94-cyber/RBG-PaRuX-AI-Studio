using System;

namespace PaRuX.Services
{
    /// <summary>
    /// An error we can show the user verbatim: already phrased for humans,
    /// not a raw PowerShell stack trace.
    /// </summary>
    public class LocalAiException : Exception
    {
        public LocalAiException(string message) : base(message) { }
    }

    /// <summary>A deterministic preflight rejection: Core confirmed that a Candidate evaluation
    /// did not start, so the UI must not reconcile it as an unknown in-flight job.</summary>
    public sealed class CandidateEvaluationStartRejectedException : LocalAiException
    {
        public string ErrorCode { get; }

        public CandidateEvaluationStartRejectedException(string errorCode)
            : base("Candidate evaluation was rejected before it started: " + errorCode)
        {
            ErrorCode = errorCode;
        }
    }
}
