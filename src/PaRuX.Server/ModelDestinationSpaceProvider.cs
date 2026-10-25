namespace PaRuX.Server;

/// <summary>Reads free bytes on the volume used by PaRuX-managed model downloads. A null result
/// means the destination volume could not be inspected; it must not be represented as zero bytes.</summary>
public interface IModelDestinationSpaceProvider
{
    long? GetAvailableBytes(string destinationDirectory);
}

/// <summary>Windows/Linux-compatible volume probe. It never creates the target directory or returns
/// the path to clients; only the current user's available free-space count crosses the API.</summary>
public sealed class ModelDestinationSpaceProvider : IModelDestinationSpaceProvider
{
    public long? GetAvailableBytes(string destinationDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destinationDirectory)) return null;
            string fullPath = Path.GetFullPath(destinationDirectory);
            string? volumeRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(volumeRoot)) return null;

            var volume = new DriveInfo(volumeRoot);
            if (!volume.IsReady) return null;
            long available = volume.AvailableFreeSpace;
            return available >= 0 ? available : null;
        }
        catch
        {
            // Unsupported, unavailable or invalid destinations are an explicit unknown.
            return null;
        }
    }
}
