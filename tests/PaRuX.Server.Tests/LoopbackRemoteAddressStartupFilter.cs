using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace PaRuX.Server.Tests;

/// <summary>
/// In-process TestServer requests carry no remote address, and the server refuses to treat a missing address as
/// the local desktop. Test hosts that model the desktop caller install this filter so their requests arrive from
/// loopback, as Kestrel delivers local requests. An address a test sets explicitly is kept.
/// </summary>
internal sealed class LoopbackRemoteAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
            await continuation();
        });
        next(app);
    };
}
