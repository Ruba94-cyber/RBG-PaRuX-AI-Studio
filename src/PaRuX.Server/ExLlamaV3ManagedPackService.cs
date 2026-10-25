using System.Diagnostics;
using System.Security.Cryptography;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed record ExLlamaV3PackOption(
    string Tag,
    DateTime? PublishedUtc,
    long WheelSizeBytes,
    string WheelName,
    bool Installed,
    bool Active);

/// <summary>
/// PaRuX-owned ExLlamaV3 pack lifecycle. Every release is installed side-by-side under the managed
/// runtime root and can only become active through an explicit SetActive call (the future Engine
/// Race promotion path). Installation never invokes system Python, pip, CUDA build tools or MSVC.
/// </summary>
public sealed class ExLlamaV3ManagedPackService
{
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;
    private readonly Func<GpuInventoryStatus> _detectGpuInventory;

    public ExLlamaV3ManagedPackService(
        HttpClient? httpClient = null,
        string? root = null,
        Func<GpuInventoryStatus>? detectGpuInventory = null)
    {
        _http = httpClient ?? SharedHttp;
        _root = root ?? Path.Combine(
            PaRuX.Contracts.PaRuXDataRoot.Path, "ai-runtimes", "exllamav3");
        _detectGpuInventory = detectGpuInventory ?? (() => new GpuInventoryService().GetStatus());
    }

    public string? GetActiveTag()
    {
        try
        {
            string path = ActiveStatePath;
            if (!File.Exists(path)) return null;
            string tag = File.ReadAllText(path).Trim();
            return IsInstalled(tag) ? tag : null;
        }
        catch { return null; }
    }

    public IReadOnlyList<string> ListInstalledTags()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(_root)
                .Select(Path.GetFileName)
                .Where(tag => !string.IsNullOrWhiteSpace(tag) && !tag!.StartsWith(".", StringComparison.Ordinal))
                .Where(tag => IsInstalled(tag))
                .Select(tag => tag!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public bool IsInstalled(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        try
        {
            ValidateTag(tag);
            return File.Exists(ManagedPythonRuntime.VenvPython(VenvRoot(tag)));
        }
        catch { return false; }
    }

    public string RequireInstalledPython(string tag)
    {
        ValidateTag(tag);
        string python = ManagedPythonRuntime.VenvPython(VenvRoot(tag));
        if (!File.Exists(python))
            throw new InvalidOperationException("ExLlamaV3 pack is not installed: " + tag);
        return python;
    }

    public async Task<IReadOnlyList<ExLlamaV3PackOption>> ListAvailableAsync(CancellationToken ct)
    {
        List<RuntimeReleaseInfo> releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        string? active = GetActiveTag();
        var result = new List<ExLlamaV3PackOption>();
        foreach (RuntimeReleaseInfo release in releases)
        {
            RuntimeReleaseAsset? wheel = ExLlamaV3BuildCatalog.SelectWindowsWheel(release.Assets);
            if (wheel == null) continue;
            try { RuntimeArtifactSecurityPolicy.ValidateAsset(wheel, ExLlamaV3BuildCatalog.Owner, ExLlamaV3BuildCatalog.Repo); }
            catch (InvalidDataException) { continue; }
            result.Add(new ExLlamaV3PackOption(
                release.Tag,
                release.PublishedUtc,
                wheel.SizeBytes,
                wheel.Name,
                IsInstalled(release.Tag),
                string.Equals(active, release.Tag, StringComparison.Ordinal)));
        }
        return result;
    }

    public async Task<string?> GetLatestCompatibleTagAsync(CancellationToken ct)
    {
        List<RuntimeReleaseInfo> releases = await GetReleasesAsync(ct).ConfigureAwait(false);
        return ExLlamaV3BuildCatalog.SelectLatestCompatible(releases)?.Tag;
    }

    public async Task<string> InstallAsync(string tag, IProgress<string>? progress, CancellationToken ct)
    {
        ValidateTag(tag);
        // Refused before the gate, the release lookup, the CUDA PyTorch download or any pip work.
        ExLlamaV3BuildCatalog.RequireNvidiaCudaGpu(_detectGpuInventory());
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled(tag)) return PackageRoot(tag);

            RuntimeReleaseInfo release = await GetReleaseByTagAsync(tag, ct).ConfigureAwait(false);
            RuntimeArtifactSecurityPolicy.ValidateReleaseIdentity(release, tag);
            RuntimeReleaseAsset wheel = ExLlamaV3BuildCatalog.RequireVerifiedWheel(release);

            Directory.CreateDirectory(_root);
            string staging = Path.Combine(_root, ".staging-" + Guid.NewGuid().ToString("N"));
            string final = PackageRoot(tag);
            try
            {
                Directory.CreateDirectory(staging);
                string venv = Path.Combine(staging, "venv");
                progress?.Report("Preparing PaRuX-managed Python " + ExLlamaV3BuildCatalog.PythonVersion + "...");
                await ManagedPythonRuntime.EnsureVenvAsync(
                    venv, ExLlamaV3BuildCatalog.PythonVersion, ct).ConfigureAwait(false);

                progress?.Report("Installing CUDA PyTorch " + ExLlamaV3BuildCatalog.TorchVersion + "...");
                await ManagedPythonRuntime.PipInstallAsync(
                    venv,
                    new[]
                    {
                        "torch==" + ExLlamaV3BuildCatalog.TorchVersion,
                        "--index-url", ExLlamaV3BuildCatalog.TorchIndexUrl
                    },
                    ct,
                    TimeSpan.FromMinutes(30)).ConfigureAwait(false);

                string wheelPath = Path.Combine(staging, wheel.Name);
                progress?.Report("Downloading verified ExLlamaV3 " + tag + " wheel...");
                await DownloadVerifiedAssetAsync(wheel, wheelPath, progress, ct).ConfigureAwait(false);

                progress?.Report("Installing ExLlamaV3 " + tag + "...");
                await ManagedPythonRuntime.PipInstallAsync(
                    venv, new[] { wheelPath }, ct, TimeSpan.FromMinutes(30)).ConfigureAwait(false);

                progress?.Report("Validating ExLlamaV3 CUDA import...");
                string validation = await ValidateImportAsync(venv, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "validation.txt"), validation, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "release-tag.txt"), tag, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(staging, "wheel-sha256.txt"),
                    wheel.Digest ?? string.Empty, ct).ConfigureAwait(false);

                try { File.Delete(wheelPath); } catch { }
                if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
                Directory.Move(staging, final);
                progress?.Report("ExLlamaV3 " + tag + " installed side-by-side. Engine Race promotion is still required.");
                return final;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    public void SetActive(string tag)
    {
        ValidateTag(tag);
        if (!IsInstalled(tag)) throw new InvalidOperationException("ExLlamaV3 pack is not installed: " + tag);
        Directory.CreateDirectory(_root);
        string temp = ActiveStatePath + ".tmp";
        File.WriteAllText(temp, tag);
        File.Move(temp, ActiveStatePath, overwrite: true);
    }

    public void ClearActive()
    {
        try { File.Delete(ActiveStatePath); } catch { }
    }

    public void Remove(string tag)
    {
        ValidateTag(tag);
        if (string.Equals(GetActiveTag(), tag, StringComparison.Ordinal))
            throw new InvalidOperationException("Cannot remove the active ExLlamaV3 pack. Activate a different version or clear it first.");
        string directory = PackageRoot(tag);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string PackageRoot(string tag) => Path.Combine(_root, tag);
    private string VenvRoot(string tag) => Path.Combine(PackageRoot(tag), "venv");
    private string ActiveStatePath => Path.Combine(_root, "active-version.txt");

    private async Task<List<RuntimeReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{ExLlamaV3BuildCatalog.Owner}/{ExLlamaV3BuildCatalog.Repo}/releases?per_page=10");
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return RuntimeBuildCatalog.ParseReleases(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private async Task<RuntimeReleaseInfo> GetReleaseByTagAsync(string tag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{ExLlamaV3BuildCatalog.Owner}/{ExLlamaV3BuildCatalog.Repo}/releases/tags/{Uri.EscapeDataString(tag)}");
        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        RuntimeReleaseInfo? release = RuntimeBuildCatalog.ParseReleases("[" + json + "]").SingleOrDefault();
        return release ?? throw new InvalidDataException("ExLlamaV3 release metadata was empty or invalid.");
    }

    private async Task DownloadVerifiedAssetAsync(
        RuntimeReleaseAsset asset,
        string destination,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        RuntimeArtifactSecurityPolicy.ValidateAsset(asset, ExLlamaV3BuildCatalog.Owner, ExLlamaV3BuildCatalog.Repo);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RuntimeArtifactSecurityPolicy.ValidateDownloadResponse(response, asset);

        long downloaded = 0;
        byte[] buffer = new byte[1024 * 1024];
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            RuntimeArtifactSecurityPolicy.ThrowIfDownloadWouldExceedBounds(downloaded, read, asset);
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            sha.AppendData(buffer, 0, read);
            downloaded += read;
            if (asset.SizeBytes > 0)
                progress?.Report("Downloading ExLlamaV3: " + (downloaded * 100 / asset.SizeBytes) + "%");
        }
        await target.FlushAsync(ct).ConfigureAwait(false);
        RuntimeArtifactSecurityPolicy.ValidateDownloadedByteCount(downloaded, asset);

        string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        string expected = asset.Digest!["sha256:".Length..].Trim().ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException("Downloaded ExLlamaV3 wheel failed SHA-256 verification.");
    }

    private static async Task<string> ValidateImportAsync(string venv, CancellationToken ct)
    {
        string python = ManagedPythonRuntime.VenvPython(venv);
        var info = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(
            "import exllamav3, torch; "
            + "assert torch.cuda.is_available(), 'CUDA is not available to managed PyTorch'; "
            + "print('exllamav3=' + str(getattr(exllamav3, '__version__', 'import-ok')) + ';torch=' + torch.__version__ + ';cuda=' + str(torch.version.cuda))");
        ManagedPythonRuntime.ApplyEnvironment(info);

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        string output = (await stdout.ConfigureAwait(false)).Trim();
        string error = (await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Managed ExLlamaV3 validation failed: " + (error.Length > 0 ? error : output));
        return output;
    }

    private static void ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 80 || tag.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' )))
            throw new InvalidDataException("ExLlamaV3 release tag contains unsupported characters.");
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PaRuX-ExLlamaV3/1.0");
        return http;
    }
}
