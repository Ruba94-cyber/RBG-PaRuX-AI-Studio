namespace PaRuX.Server;

/// <summary>Puts a finished checkpoint download into ComfyUI's model folder without ever leaving a truncated file under its final name.</summary>
internal static class MediaCheckpointPlacement
{
    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/> through a ".part" file and checks the size. A complete file already
    /// at the destination is kept (the duplicate source is removed); a different-sized one (a crashed earlier copy) is replaced.
    /// </summary>
    internal static void MoveIntoPlace(string source, string destination, long expectedSize)
    {
        if (File.Exists(destination))
        {
            if (expectedSize <= 0 || new FileInfo(destination).Length == expectedSize)
            {
                if (File.Exists(source)) File.Delete(source);
                return;
            }
            File.Delete(destination);
        }
        if (!File.Exists(source)) throw new FileNotFoundException("The downloaded checkpoint was not found.", source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string part = destination + ".part";
        if (File.Exists(part)) File.Delete(part);
        File.Move(source, part);
        if (expectedSize > 0 && new FileInfo(part).Length != expectedSize)
        {
            long actual = new FileInfo(part).Length;
            File.Delete(part);
            throw new IOException("The checkpoint is " + actual + " bytes but " + expectedSize + " were expected; it was not installed.");
        }
        File.Move(part, destination);
    }
}
