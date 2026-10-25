using System;
using System.Collections.Generic;

namespace PaRuX.Services
{
    public enum CodingAgentRepeatDecision
    {
        Allow,
        Steer,
        Stop
    }

    /// <summary>Prevents identical read-only actions from consuming model turns without new evidence.</summary>
    public sealed class CodingAgentRepeatGuard
    {
        private readonly Dictionary<string, (int Attempts, string Evidence)> _seen = new(StringComparer.Ordinal);
        public int Epoch { get; private set; }

        public CodingAgentRepeatDecision Evaluate(string key, out string evidence)
        {
            evidence = string.Empty;
            if (!_seen.TryGetValue(key, out var prior))
            {
                _seen[key] = (1, string.Empty);
                return CodingAgentRepeatDecision.Allow;
            }

            evidence = prior.Evidence;
            int attempts = prior.Attempts + 1;
            _seen[key] = (attempts, prior.Evidence);
            return attempts == 2 ? CodingAgentRepeatDecision.Steer : CodingAgentRepeatDecision.Stop;
        }

        public void Record(string key, string evidence)
        {
            if (_seen.TryGetValue(key, out var prior))
                _seen[key] = (prior.Attempts, evidence);
            else
                _seen[key] = (1, evidence);
        }

        public int AdvanceEpoch()
        {
            _seen.Clear();
            return ++Epoch;
        }

        public void Reset() => AdvanceEpoch();
    }
}
