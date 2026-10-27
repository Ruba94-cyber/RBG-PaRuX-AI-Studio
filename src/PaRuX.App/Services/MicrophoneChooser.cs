using System;
using System.Collections.Generic;
using System.Linq;

namespace PaRuX.Services
{
    /// <summary>Picks the capture device for voice input: the Windows default unless it is a virtual/streaming device that only records silence.</summary>
    public static class MicrophoneChooser
    {
        private static readonly string[] VirtualMarkers =
        {
            "steam streaming", "virtual", "vb-audio", "voicemeeter", "obs", "oculus", "cable output", "stereo mix", "loopback", "nvidia broadcast"
        };

        public sealed record Device(string Id, string Name, bool IsDefault);

        public static bool LooksVirtual(string? name) =>
            !string.IsNullOrWhiteSpace(name) && VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

        /// <summary>Null means "let Windows decide" (no devices found or nothing better than the default).</summary>
        public static string? Choose(IReadOnlyList<Device> devices, string? preferredId = null)
        {
            if (devices.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(preferredId) && devices.Any(d => d.Id == preferredId)) return preferredId;
            Device? def = devices.FirstOrDefault(d => d.IsDefault);
            if (def != null && !LooksVirtual(def.Name)) return def.Id;
            Device? real = devices.FirstOrDefault(d => !LooksVirtual(d.Name));
            return real?.Id ?? def?.Id;
        }
    }
}
