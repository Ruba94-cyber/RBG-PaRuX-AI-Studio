namespace PaRuX.Server;

public sealed partial class RagStore
{
    internal void InvalidateManagedProviderState() => InvalidateVectorAcceleration();
}
