using System;

namespace PaRuX.Services;

/// <summary>Runs cleanup once on each observed transition into gaming.</summary>
internal sealed class VoiceGamingTransition
{
    private readonly Action _onGaming;
    private bool _active;

    internal VoiceGamingTransition(Action onGaming) => _onGaming = onGaming ?? throw new ArgumentNullException(nameof(onGaming));

    internal void Observe(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active) _onGaming();
    }
}
