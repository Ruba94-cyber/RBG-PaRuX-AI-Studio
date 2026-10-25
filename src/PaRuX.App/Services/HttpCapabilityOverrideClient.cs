using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed class HttpCapabilityOverrideClient : ICapabilityOverrideClient
    {
        public Task<IReadOnlyList<ScopedRoutingOverrideDto>> ListOverridesAsync(CancellationToken ct = default) =>
            ModelRoutingClient.Current.GetCapabilityOverridesAsync(ct);

        public Task<ScopedRoutingOverrideDto?> GetOverrideAsync(string role, string profile, CancellationToken ct = default) =>
            ModelRoutingClient.Current.GetCapabilityOverrideAsync(role, profile, ct);

        public Task<ScopedRoutingOverrideDto> SetOverrideAsync(string role, string profile, SetScopedRoutingOverrideRequest request, CancellationToken ct = default) =>
            ModelRoutingClient.Current.SetCapabilityOverrideAsync(role, profile, request, ct);

        public async Task<bool> DeleteOverrideAsync(string role, string profile, CancellationToken ct = default)
        {
            try
            {
                await ModelRoutingClient.Current.DeleteCapabilityOverrideAsync(role, profile, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
