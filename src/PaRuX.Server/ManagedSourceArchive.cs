using System.IO.Compression;

namespace PaRuX.Server;

/// <summary>Downloads immutable, commit-addressed GitHub source archives without requiring Git.
/// Extraction rejects traversal/absolute paths and atomically promotes the completed tree.</summary>
public static class ManagedSourceArchive
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task EnsureGithubCommitAsync(
        string owner, string repository, string commitSha, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) return;
        if (!SafeSlug(owner) || !SafeSlug(repository) || commitSha.Length != 40 || commitSha.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Managed source archive identity is invalid.");

        string parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Managed source destination has no parent directory.");
        Directory.CreateDirectory(parent);
        string archive = destination + ".zip.download";
        string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            var uri = new Uri($"https://github.com/{owner}/{repository}/archive/{commitSha}.zip");
            using HttpResponseMessage response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, ct).ConfigureAwait(false);

            Directory.CreateDirectory(staging);
            using ZipArchive zip = ZipFile.OpenRead(archive);
            string? commonRoot = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/').Split('/', 2)[0])
                .FirstOrDefault(root => !string.IsNullOrWhiteSpace(root));
            if (string.IsNullOrWhiteSpace(commonRoot)) throw new InvalidDataException("Managed source archive was empty.");

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string normalized = entry.FullName.Replace('\\', '/');
                if (!normalized.StartsWith(commonRoot + "/", StringComparison.Ordinal)) continue;
                string relative = normalized[(commonRoot.Length + 1)..];
                if (relative.Length == 0) continue;
                string output = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                string prefix = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Managed source archive attempted path traversal.");
                if (normalized.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output, overwrite: true);
            }

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
        }
        finally
        {
            try { File.Delete(archive); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static bool SafeSlug(string value) => !string.IsNullOrWhiteSpace(value)
        && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
