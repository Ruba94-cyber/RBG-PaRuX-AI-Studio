using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>Host facts that, together with the GPU inventory, identify the machine an evaluation
/// ran on. The CPU identity is a hash of the registry Identifier/ProcessorNameString (class
/// strings, no serial); nothing here is a path, user name, machine name or serial number, so the
/// resulting fingerprint is safe to persist and publish.</summary>
internal sealed record HostEvaluationFacts(string? CpuIdentifier, int LogicalProcessors, string? OsBuild)
{
    private const ushort AllProcessorGroups = 0xFFFF;

    internal static HostEvaluationFacts Current() => new(ReadCpuIdentifier(), ReadLogicalProcessors(), ReadOsBuild());

    private static int ReadLogicalProcessors()
    {
        try
        {
            // Environment.ProcessorCount is capped to one processor group (and affinity); the
            // fingerprint must describe the machine, so count every active logical processor.
            if (OperatingSystem.IsWindows())
            {
                uint n = GetActiveProcessorCount(AllProcessorGroups);
                if (n > 0 && n < int.MaxValue) return (int)n;
                return 0;
            }
            return Environment.ProcessorCount;
        }
        catch { return 0; }
    }

    private static string? ReadCpuIdentifier()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? id = key?.GetValue("Identifier") as string;
            string? name = key?.GetValue("ProcessorNameString") as string;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;
            // Hashed, never stored: the brand string is a class label but need not be persisted raw.
            return "cpu-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                id.Trim() + "|" + name.Trim() + "|" + RuntimeInformation.OSArchitecture))).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>Major.Minor.Build only. The monthly UBR (cumulative-update revision) is deliberately
    /// excluded: GPU driver version is already fingerprinted, and no measured evidence shows UBR
    /// moves local inference performance, so including it would invalidate all evidence every
    /// Patch Tuesday for no demonstrated benefit. A feature-update build change still invalidates.</summary>
    private static string? ReadOsBuild()
    {
        try
        {
            Version v = Environment.OSVersion.Version;
            return v.Major <= 0 ? null : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);
}

/// <summary>Result of building the evaluation hardware identity. When <see cref="Complete"/> is
/// false the fingerprint must not be used as evidence identity: the missing attestation is named
/// explicitly rather than silently hashed as an empty string.</summary>
internal sealed record EvaluationHardwareAttestation(
    string Fingerprint, bool Complete, IReadOnlyList<string> Unavailable);

/// <summary>
/// Versioned (v2) evaluation hardware fingerprint. The "hw2-sha256:" prefix is the version; any
/// other prefix (notably the legacy "dxgi-sha256:" value that lacked driver/CPU/OS) is never equal
/// to a current fingerprint, so old persisted evidence mismatches (fails closed).
/// Included: per adapter vendor, device, dedicated VRAM and driver version; CPU identity hash;
/// logical processor count; OS build. Excluded on purpose (volatile or not performance identity):
/// shared and dedicated-system sizes (they follow the BIOS UMA carve-out / installed RAM and would
/// invalidate dGPU evidence when an iGPU setting changes), adapter name/index/LUID, free memory,
/// OS UBR. When the evaluated backend is CUDA only NVIDIA adapters are included, so a hybrid
/// iGPU appearing/disappearing does not change a CUDA fingerprint; Vulkan cannot be attributed to
/// one adapter in advance, so it keeps every hardware adapter.
/// </summary>
internal static class EvaluationHardwareFingerprint
{
    internal const string CurrentPrefix = "hw2-sha256:";
    internal const string LegacyPrefix = "dxgi-sha256:";
    private const uint NvidiaVendorId = 0x10de;

    internal static bool IsCurrentVersion(string? fingerprint) =>
        fingerprint is { Length: > 0 } && fingerprint.StartsWith(CurrentPrefix, StringComparison.Ordinal);

    internal static bool IsLegacyVersion(string? fingerprint) =>
        fingerprint is { Length: > 0 } && fingerprint.StartsWith(LegacyPrefix, StringComparison.Ordinal);

    internal static EvaluationHardwareAttestation Build(GpuInventoryStatus? inventory, HostEvaluationFacts facts,
        string? backend = null)
    {
        bool cudaScope = string.Equals(backend, "text-cuda", StringComparison.OrdinalIgnoreCase);
        var missing = new List<string>();
        var adapterTokens = new List<string>();

        if (inventory is not { Available: true })
        {
            missing.Add("gpu-inventory");
        }
        else
        {
            GpuAdapterStatus[] hardware = inventory.Adapters
                .Where(a => !a.IsSoftware && (!cudaScope || a.VendorId == NvidiaVendorId)).ToArray();
            if (hardware.Length == 0) missing.Add("gpu-adapter");
            foreach (GpuAdapterStatus a in hardware)
            {
                string driver = string.IsNullOrWhiteSpace(a.DriverVersion) ? "unavailable" : a.DriverVersion.Trim();
                if (driver == "unavailable") missing.Add($"gpu-driver:{a.VendorId:x4}:{a.DeviceId:x4}");
                adapterTokens.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{a.VendorId:x4}:{a.DeviceId:x4}:{a.DedicatedVideoMemoryBytes}:{driver}"));
            }
        }
        // Adapter enumeration order is not stable identity.
        adapterTokens.Sort(StringComparer.Ordinal);

        string cpu = string.IsNullOrWhiteSpace(facts.CpuIdentifier) ? "unavailable" : facts.CpuIdentifier.Trim();
        if (cpu == "unavailable") missing.Add("cpu");
        if (facts.LogicalProcessors <= 0) missing.Add("cpu-threads");
        string os = string.IsNullOrWhiteSpace(facts.OsBuild) ? "unavailable" : facts.OsBuild.Trim();
        if (os == "unavailable") missing.Add("os-build");

        string canonical = string.Join("|", "hw2",
            "scope=" + (cudaScope ? "cuda-nvidia" : "all-hardware"),
            "gpu=" + string.Join(";", adapterTokens),
            "cpu=" + cpu,
            "threads=" + Math.Max(0, facts.LogicalProcessors).ToString(CultureInfo.InvariantCulture),
            "os=" + os);
        string fingerprint = CurrentPrefix + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new EvaluationHardwareAttestation(fingerprint, missing.Count == 0, missing);
    }
}

/// <summary>
/// Runtime-dependency identity (v2): the launcher executable hash is supplied by the caller; this
/// adds the full SHA-256 of every sibling native library, so a swapped ggml/CUDA/Vulkan/cuBLAS DLL
/// next to an unchanged llama-server.exe changes the fingerprint (sizes alone can collide). GGUF
/// model files are never read here. Hashes are cached in-process only when the file's identity
/// (volume serial + file index + size + last-write + NTFS change time) is fully known and
/// unchanged; timestamps alone are not trusted, so any identity read failure means rehash.
/// </summary>
internal static class RuntimeDependencyFingerprint
{
    internal const string Prefix = "rtv2-sha256:";
    private const int MaxFiles = 512;
    private static readonly string[] Extensions = { ".dll", ".so", ".dylib", ".exe", ".spv" };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

    internal static async Task<string> ComputeAsync(string directory, string executableSha256Hex,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).ToArray();
        foreach (FileInfo f in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
            string? key = TryIdentityKey(s);
            if (key is null || !Cache.TryGetValue(key, out string? hash))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(s, cancellationToken)
                    .ConfigureAwait(false)).ToLowerInvariant();
                if (key is not null)
                {
                    // Re-check identity after hashing: a file modified mid-read is not cached.
                    if (TryIdentityKey(s) == key)
                    {
                        if (Cache.Count > 4096) Cache.Clear();
                        Cache[key] = hash;
                    }
                }
            }
            lines.Add(f.Name.ToLowerInvariant() + ":" + f.Length.ToString(CultureInfo.InvariantCulture) + ":" + hash);
        }
        string manifest = "exe=" + executableSha256Hex + "|" + string.Join(";", lines);
        return Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
    }

    /// <summary>Volume serial + file index + size + last-write + change time, or null if any is unreadable.</summary>
    private static string? TryIdentityKey(FileStream stream)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            SafeFileHandle handle = stream.SafeFileHandle;
            if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info)) return null;
            if (!GetFileInformationByHandleEx(handle, 0 /* FileBasicInfo */, out FILE_BASIC_INFO basic,
                    (uint)Marshal.SizeOf<FILE_BASIC_INFO>())) return null;
            if (basic.ChangeTime == 0) return null;
            long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            return string.Create(CultureInfo.InvariantCulture,
                $"{info.VolumeSerialNumber:x}:{info.FileIndexHigh:x8}:{info.FileIndexLow:x8}:{size}:{basic.LastWriteTime}:{basic.ChangeTime}");
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out FILE_BASIC_INFO info, uint size);
}
