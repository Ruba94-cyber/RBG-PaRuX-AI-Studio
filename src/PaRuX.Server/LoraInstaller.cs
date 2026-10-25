using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaRuX.Server;

/// <summary>What installing one LoRA file would do. Shown to the user; installing needs this exact plan confirmed.</summary>
public sealed record LoraInstallPlan(
    string SourceId, string ModelId, string VersionId, string FileId, string ModelName, string FileName,
    long SizeBytes, string? Sha256, string? DownloadUrl, string? BaseModel, IReadOnlyList<string> TriggerWords,
    bool Nsfw, LoraLicense License, bool Installable, string? Reason, string PlanHash);

public sealed record LoraInstallRequest(bool Consent, string PlanHash);

public sealed record LoraInstallResult(bool Ok, string Message, string? InstalledName);

/// <summary>Moves bytes for one confirmed plan into <paramref name="tempPath"/>. Production wires this to the
/// existing ModelDownloadService transfer machinery; tests use a fake.</summary>
public interface ILoraFileTransfer
{
    Task DownloadAsync(LoraInstallPlan plan, string tempPath, CancellationToken ct);
}

/// <summary>
/// Plans and performs a LoRA install. Never starts on its own: <see cref="InstallAsync"/> needs explicit consent bound
/// to the reviewed plan hash. The downloaded file must match the catalogue's size and SHA-256, is a safetensors file,
/// and is never allowed to replace an existing file.
/// </summary>
public sealed class LoraInstaller
{
    public const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private readonly string _folder;
    private readonly ILoraFileTransfer _transfer;

    public LoraInstaller(string lorasFolder, ILoraFileTransfer transfer)
    {
        _folder = lorasFolder ?? throw new ArgumentNullException(nameof(lorasFolder));
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
    }

    public static LoraInstallPlan Plan(LoraCatalogItem item, LoraVersionInfo version, LoraFileInfo file)
    {
        string? reason = null;
        string safeName = SafeFileName(file.Name);
        if (IsUnsafeWindowsName(file.Name) || IsUnsafeWindowsName(safeName))
            reason = "The file name is not allowed on Windows (reserved device name, trailing dot or space, or ':').";
        else if (!safeName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            reason = "Only safetensors files can be installed; pickle formats (.pt, .ckpt) can run code.";
        else if (file.Sha256 == null) reason = "The catalogue gives no SHA-256, so the download could not be verified.";
        else if (file.SizeBytes <= 0 || file.SizeBytes > MaxBytes) reason = "The catalogue reports no usable file size.";
        else if (file.DownloadUrl == null) reason = "The file has no trusted download address.";
        var plan = new LoraInstallPlan(item.SourceId, item.ModelId, version.VersionId, file.FileId, item.Name, safeName,
            file.SizeBytes, file.Sha256, file.DownloadUrl, version.BaseModel, version.TrainedWords, item.Nsfw, item.License,
            reason == null, reason, string.Empty);
        return plan with { PlanHash = Hash(plan) };
    }

    public async Task<LoraInstallResult> InstallAsync(LoraInstallPlan plan, LoraInstallRequest request, CancellationToken ct)
    {
        if (!request.Consent) return Refused("Installing needs your confirmation after reviewing the file, size and licence.");
        if (!string.Equals(request.PlanHash, plan.PlanHash, StringComparison.Ordinal) || Hash(plan with { PlanHash = string.Empty }) != plan.PlanHash)
            return Refused("The plan changed since you reviewed it. Review it again.");
        if (!plan.Installable || plan.Sha256 == null) return Refused(plan.Reason ?? "This file cannot be installed.");

        if (IsUnsafeWindowsName(plan.FileName)) return Refused("The file name is not allowed.");
        Directory.CreateDirectory(_folder);
        string target = Path.GetFullPath(Path.Combine(_folder, plan.FileName));
        if (!target.StartsWith(Path.GetFullPath(_folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Refused("The file name is not allowed.");
        if (File.Exists(target)) return Refused(plan.FileName + " is already installed. Rename or remove it first.");

        string temp = Path.Combine(_folder, plan.FileName + ".parux-part");
        try
        {
            await _transfer.DownloadAsync(plan, temp, ct).ConfigureAwait(false);
            if (!File.Exists(temp)) return Refused("The download produced no file.");
            if (new FileInfo(temp).Length != plan.SizeBytes) { Discard(temp); return Refused("The downloaded size does not match the catalogue. It was discarded."); }
            string actual;
            await using (FileStream s = File.OpenRead(temp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(actual, plan.Sha256, StringComparison.OrdinalIgnoreCase)) { Discard(temp); return Refused("The downloaded file does not match its SHA-256. It was discarded."); }
            File.Move(temp, target, overwrite: false);
            await File.WriteAllTextAsync(target + LoraLibrary.SidecarSuffix, JsonSerializer.Serialize(new
            {
                source = plan.SourceId + ":" + plan.ModelId + "/" + plan.VersionId, baseModel = plan.BaseModel,
                triggerWords = plan.TriggerWords, sha256 = plan.Sha256, nsfw = plan.Nsfw
            }), ct).ConfigureAwait(false);
            return new LoraInstallResult(true, "Installed " + plan.FileName + ".", plan.FileName);
        }
        catch (OperationCanceledException) { Discard(temp); throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
        {
            Discard(temp);
            return Refused("The download failed: " + ex.Message);
        }
        catch (Exception) { Discard(temp); throw; }
    }

    private static LoraInstallResult Refused(string message) => new(false, message, null);
    private static void Discard(string path) { try { File.Delete(path); } catch (IOException) { } }

    private static readonly System.Text.RegularExpressions.Regex ReservedDeviceName =
        new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True for names Windows treats specially: reserved devices (with or without extension), trailing dot/space, or ':' (streams).</summary>
    internal static bool IsUnsafeWindowsName(string name)
    {
        string leaf = name.Replace('\\', '/').Split('/')[^1].TrimStart();
        return leaf.Contains(':') || leaf.EndsWith('.') || leaf.EndsWith(' ') || ReservedDeviceName.IsMatch(leaf);
    }

    internal static string SafeFileName(string name)
    {
        string leaf = Path.GetFileName(name.Replace('\\', '/'));
        var sb = new StringBuilder();
        foreach (char c in leaf) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_');
        string cleaned = sb.ToString().Trim().TrimStart('.');
        return cleaned.Length == 0 ? "lora.bin" : cleaned;
    }

    private static string Hash(LoraInstallPlan p)
    {
        string canonical = string.Join("|", p.SourceId, p.ModelId, p.VersionId, p.FileId, p.FileName, p.SizeBytes, p.Sha256, p.DownloadUrl, p.Installable);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
