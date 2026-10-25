using PaRuX.Contracts;
using System.Security.Cryptography;

namespace PaRuX.Server;

/// <summary>Optional CivitAI API token, protected at rest like the Hugging Face token. It is sent only to the exact
/// CivitAI source host (never a CDN, never in a URL) and is never returned by any route or written to a log.</summary>
internal static class LoraTokenStore
{
    private const string Purpose = "parux.lora.civitai-token";
    private const string FileName = "civitai-token.secret";

    public static bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= 256
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    public static bool Exists(string stateDirectory) => File.Exists(Path.Combine(stateDirectory, FileName));

    public static void Save(string stateDirectory, string token)
    {
        if (!IsValidToken(token)) throw new InvalidDataException("The token format is not recognized.");
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, FileName);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, SecretProtection.For(stateDirectory).Protect(Purpose, token));
        File.Move(temporary, path, overwrite: true);
    }

    public static string? Load(string stateDirectory)
    {
        try
        {
            string path = Path.Combine(stateDirectory, FileName);
            return File.Exists(path) ? SecretProtection.For(stateDirectory).Unprotect(Purpose, File.ReadAllText(path).Trim()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    public static bool Clear(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, FileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }
}

/// <summary>Production <see cref="ILoraFileTransfer"/>: runs the confirmed plan through ModelDownloadService's LoRA lane
/// (queue, stall/connect bounds, exact-host redirects, size and SHA-256 verification), then hands the verified file to the installer.</summary>
internal sealed class ModelDownloadLoraTransfer : ILoraFileTransfer
{
    private readonly ModelDownloadService _downloads;
    private readonly TimeSpan _poll;
    public ModelDownloadLoraTransfer(ModelDownloadService downloads, TimeSpan? poll = null)
    {
        _downloads = downloads;
        _poll = poll ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Id of the running download job, for progress reporting.</summary>
    public string? DownloadId { get; private set; }

    public async Task DownloadAsync(LoraInstallPlan plan, string tempPath, CancellationToken ct)
    {
        if (plan.DownloadUrl is null || plan.Sha256 is null) throw new InvalidDataException("The plan has no verifiable download.");
        string staging = "lora_" + plan.Sha256[..16].ToLowerInvariant() + ".safetensors";
        ModelDownloadStatus status = await _downloads.CreateLoraDownloadAsync(plan.DownloadUrl, staging, plan.SizeBytes, plan.Sha256, ct).ConfigureAwait(false);
        DownloadId = status.Id;
        bool moved = false;
        try
        {
            while (true)
            {
                ModelDownloadStatus? current = _downloads.Get(status.Id) ?? throw new IOException("The download job was not found.");
                if (current.Status == "completed") break;
                if (current.Status is "failed" or "cancelled") throw new IOException(current.Error ?? "The transfer ended as " + current.Status + ".");
                await Task.Delay(_poll, ct).ConfigureAwait(false);
            }
            string downloaded = Path.Combine(ModelDownloadService.ModelsDirectory, staging);
            if (!File.Exists(downloaded)) throw new IOException("The download finished but its file was not found.");
            File.Move(downloaded, tempPath, overwrite: true);
            moved = true;
        }
        catch (OperationCanceledException) { _downloads.Cancel(status.Id); await WaitForTerminalAsync(status.Id).ConfigureAwait(false); throw; }
        finally
        {
            // The staging file (unless handed on), its .partial and its provenance entry never outlive the attempt.
            CleanupStaging(ModelDownloadService.ModelsDirectory, staging, deleteFile: !moved);
        }
    }

    private async Task WaitForTerminalAsync(string id)
    {
        for (int i = 0; i < 100; i++)
        {
            if (_downloads.Get(id) is not { } s || s.Status is "completed" or "failed" or "cancelled") return;
            await Task.Delay(_poll).ConfigureAwait(false);
        }
    }

    internal static void CleanupStaging(string modelsDirectory, string staging, bool deleteFile)
    {
        string path = Path.Combine(modelsDirectory, staging);
        try
        {
            // The .partial file and its identity/source sidecars (".partial.*").
            var doomed = Directory.Exists(modelsDirectory)
                ? Directory.EnumerateFiles(modelsDirectory, staging + ".partial*").ToList() : new List<string>();
            if (deleteFile) doomed.Add(path);
            foreach (string p in doomed)
                try { File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { ModelDownloadProvenanceManifestStore.Remove(modelsDirectory, staging); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
}

/// <summary>Background LoRA installs, so a multi-GB transfer never holds an HTTP request open.</summary>
internal sealed class LoraInstallJobs
{
    internal sealed record JobState(string Id, string State, string? Message, string? DownloadId);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JobState> _jobs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelDownloadLoraTransfer> _transfers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();
    private readonly ModelDownloadService _downloads;
    private readonly string _folder;

    public LoraInstallJobs(ModelDownloadService downloads, string? folder = null)
    {
        _downloads = downloads;
        _folder = folder ?? LoraLibrary.DefaultFolder;
    }

    public JobState Start(LoraInstallPlan plan, LoraInstallRequest request)
    {
        // Refuse synchronously so the caller sees consent/plan problems at once.
        var transfer = new ModelDownloadLoraTransfer(_downloads);
        var installer = new LoraInstaller(_folder, transfer);
        if (!request.Consent) return new JobState("", "refused", "Installing needs your confirmation after reviewing the file, size and licence.", null);
        if (!string.Equals(request.PlanHash, plan.PlanHash, StringComparison.Ordinal))
            return new JobState("", "refused", "The plan changed since you reviewed it. Review it again.", null);
        if (!plan.Installable) return new JobState("", "refused", plan.Reason ?? "This file cannot be installed.", null);
        string id = Guid.NewGuid().ToString("N");
        _transfers[id] = transfer;
        var cts = new CancellationTokenSource();
        _cancellations[id] = cts;
        _jobs[id] = new JobState(id, "running", null, null);
        _ = Task.Run(async () =>
        {
            try
            {
                LoraInstallResult r = await installer.InstallAsync(plan, request, cts.Token).ConfigureAwait(false);
                _jobs[id] = new JobState(id, r.Ok ? "completed" : "failed", r.Message, transfer.DownloadId);
            }
            catch (OperationCanceledException) { _jobs[id] = new JobState(id, "cancelled", "The install was cancelled.", transfer.DownloadId); }
            catch (Exception ex) { _jobs[id] = new JobState(id, "failed", ex.Message, transfer.DownloadId); }
            finally { _cancellations.TryRemove(id, out _); cts.Dispose(); }
        });
        return _jobs[id];
    }

    /// <summary>Cancels a running install and its underlying download. False when the job is unknown or already finished.</summary>
    public bool Cancel(string id)
    {
        if (!_cancellations.TryGetValue(id, out CancellationTokenSource? cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    public JobState? Get(string id) =>
        _jobs.TryGetValue(id, out JobState? s) ? s with { DownloadId = _transfers.TryGetValue(id, out var t) ? t.DownloadId : s.DownloadId } : null;
}
