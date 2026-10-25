using System;
using PaRuX.Contracts;

namespace PaRuX.Services;

/// <summary>Validates the loopback endpoint reported by PaRuX Core before App services use it.</summary>
internal static class ManagedRuntimeEndpoint
{
    internal static string? FromStatus(RuntimeManagerStatus? status)
    {
        string? value = status?.CurrentManagedProviderEndpoint?.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
            || endpoint is null
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !endpoint.IsLoopback
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0)
            return null;

        return endpoint.GetLeftPart(UriPartial.Authority);
    }
}
