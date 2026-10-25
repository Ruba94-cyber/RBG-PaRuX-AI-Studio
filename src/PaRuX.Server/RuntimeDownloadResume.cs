namespace PaRuX.Server;

/// <summary>
/// Decides how an interrupted runtime archive download continues. A partial file is only ever continued with a byte range that the
/// response itself proves: the range start and the asset total must match. A server that ignores the range sends the whole file, and
/// the partial file is then started again. Either way the finished archive is still checked against the advertised SHA-256 before
/// anything is extracted, so resuming never trusts the bytes that were already on disk.
/// </summary>
internal static class RuntimeDownloadResume
{
    /// <summary>The file a download streams into, beside its target. The target only appears once every byte is present.</summary>
    internal const string PartialSuffix = ".partial";

    /// <param name="RangeFrom">The byte offset the request asks for. Zero means a whole-file request.</param>
    internal readonly record struct Plan(long RangeFrom)
    {
        /// <summary>True when the request asks the server for the rest of the file from <see cref="RangeFrom"/>.</summary>
        internal bool Resumes => RangeFrom > 0;
    }

    /// <summary>The request plan for a partial file of <paramref name="partialBytes"/> bytes toward an asset of
    /// <paramref name="assetBytes"/>. An empty partial, or one that already holds the whole asset, is not continued.</summary>
    internal static Plan PlanFor(long partialBytes, long assetBytes)
    {
        if (assetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(assetBytes));
        return partialBytes > 0 && partialBytes < assetBytes ? new Plan(partialBytes) : new Plan(0);
    }

    /// <summary>The Content-Range value a 206 response must carry when it continues from <paramref name="rangeFrom"/>.</summary>
    internal static string ExpectedContentRange(long rangeFrom, long assetBytes) =>
        "bytes " + rangeFrom + "-" + (assetBytes - 1) + "/" + assetBytes;
}
