using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaRuX.Services
{
    /// <summary>
    /// Chromium settings that limit the developer browser's network egress while PaRuX's Internet switch is OFF.
    ///
    /// The Fetch guard in BrowserRequestAdmissionPolicy sees HTTP(S) requests before they are sent. It does not
    /// cover WebSocket handshakes or WebRTC transports. A browser launched while Internet is OFF is therefore pointed
    /// at a PaRuX-owned sink (BrowserOfflineEgressSink) that closes every proxied connection, and WebRTC is told to
    /// disable non-proxied UDP. The sink never sees UDP, so that flag carries the WebRTC limit. Loopback keeps
    /// Chromium's default direct route, so local dev servers and the DevTools endpoint keep working. A browser launched
    /// while Internet is ON gets none of these settings. If the switch goes OFF while it runs, its HTTP(S) requests are
    /// refused. BrowserInternetSwitchWatch then closes it within about one poll interval, which ends its WebSocket and
    /// WebRTC traffic. The next browser action relaunches it in the OFF mode.
    ///
    /// Scope: these are browser-process settings. They are not an operating-system firewall, and they do not
    /// constrain a child process that ignores them. The live egress probe has not been run against this change.
    /// </summary>
    internal static class BrowserOfflineEgressPolicy
    {
        public const string WebRtcIpHandlingSwitch = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp";

        /// <summary>The switches to add while Internet is OFF, routing non-loopback traffic to the sink port.</summary>
        public static IReadOnlyList<string> SwitchesFor(int sinkPort)
        {
            if (sinkPort <= 0 || sinkPort > 65535) throw new ArgumentOutOfRangeException(nameof(sinkPort));
            return new[]
            {
                "--proxy-server=http://127.0.0.1:" + sinkPort.ToString(CultureInfo.InvariantCulture),
                WebRtcIpHandlingSwitch
            };
        }
    }
}
