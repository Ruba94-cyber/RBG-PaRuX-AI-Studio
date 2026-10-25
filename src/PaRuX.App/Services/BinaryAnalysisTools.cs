using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// Read-only binary inspection primitives for PaRuX's local Binary Analysis agent.
    /// The model may reason freely over the returned evidence; this class defines the execution
    /// boundary. It never patches the target, never turns model text into a shell command and every
    /// inspected path is confined to a user-selected workspace without following reparse points.
    /// </summary>
    public static class BinaryAnalysisTools
    {
        public const int DefaultMaxStrings = 500;
        public const int DefaultMinStringLength = 5;
        public const int MaxRenderedToolChars = 40_000;
        private const long EntropySampleLimitBytes = 32L * 1024 * 1024;
        private const int MaxPeSections = 4096;

        public sealed class BinarySectionInfo
        {
            public string Name { get; set; } = string.Empty;
            public uint VirtualAddress { get; set; }
            public uint VirtualSize { get; set; }
            public uint RawSize { get; set; }
            public uint Characteristics { get; set; }
            public double Entropy { get; set; }
        }

        public sealed class BinaryAnalysisResult
        {
            public string RelativePath { get; set; } = string.Empty;
            public string Sha256 { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public string Format { get; set; } = "unknown";
            public string Architecture { get; set; } = "unknown";
            public string Endianness { get; set; } = "unknown";
            public bool Is64Bit { get; set; }
            public bool IsManagedDotNet { get; set; }
            public string EntryPoint { get; set; } = string.Empty;
            public string ImageBase { get; set; } = string.Empty;
            public string Subsystem { get; set; } = string.Empty;
            public DateTimeOffset? PeTimestampUtc { get; set; }
            public double Entropy { get; set; }
            public List<BinarySectionInfo> Sections { get; } = new();
            public List<string> Notes { get; } = new();

            public string Render()
            {
                var sb = new StringBuilder();
                sb.AppendLine("Path: " + RelativePath);
                sb.AppendLine("SHA-256: " + Sha256);
                sb.AppendLine("Size: " + SizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
                sb.AppendLine("Format: " + Format);
                sb.AppendLine("Architecture: " + Architecture + (Is64Bit ? " (64-bit)" : string.Empty));
                sb.AppendLine("Endianness: " + Endianness);
                sb.AppendLine("Managed .NET: " + (IsManagedDotNet ? "yes" : "no"));
                if (!string.IsNullOrWhiteSpace(EntryPoint)) sb.AppendLine("Entry point: " + EntryPoint);
                if (!string.IsNullOrWhiteSpace(ImageBase)) sb.AppendLine("Image base: " + ImageBase);
                if (!string.IsNullOrWhiteSpace(Subsystem)) sb.AppendLine("Subsystem: " + Subsystem);
                if (PeTimestampUtc.HasValue)
                    sb.AppendLine("PE timestamp (UTC): " + PeTimestampUtc.Value.ToString("O", CultureInfo.InvariantCulture));
                sb.AppendLine("Entropy: " + Entropy.ToString("0.000", CultureInfo.InvariantCulture) + " bits/byte");

                if (Sections.Count > 0)
                {
                    sb.AppendLine("Sections:");
                    foreach (BinarySectionInfo section in Sections)
                    {
                        sb.Append("- ").Append(section.Name)
                          .Append(" VA=0x").Append(section.VirtualAddress.ToString("X", CultureInfo.InvariantCulture))
                          .Append(" VS=0x").Append(section.VirtualSize.ToString("X", CultureInfo.InvariantCulture))
                          .Append(" raw=").Append(section.RawSize.ToString(CultureInfo.InvariantCulture))
                          .Append(" entropy=").Append(section.Entropy.ToString("0.000", CultureInfo.InvariantCulture))
                          .Append(" flags=0x").Append(section.Characteristics.ToString("X8", CultureInfo.InvariantCulture))
                          .AppendLine();
                    }
                }

                foreach (string note in Notes) sb.AppendLine("Note: " + note);
                return sb.ToString().TrimEnd();
            }
        }

        public sealed class ExternalToolInfo
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string? Path { get; set; }
            public bool Available => !string.IsNullOrWhiteSpace(Path);
        }

        public static async Task<BinaryAnalysisResult> AnalyzeAsync(
            string workspaceRoot, string relativePath, CancellationToken ct)
        {
            string fullPath = BinaryWorkspacePathPolicy.ResolveSafeFile(workspaceRoot, relativePath);
            var info = new FileInfo(fullPath);
            string root = Path.GetFullPath(workspaceRoot);
            var result = new BinaryAnalysisResult
            {
                RelativePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/'),
                SizeBytes = info.Length,
                Sha256 = await ComputeSha256Async(fullPath, ct).ConfigureAwait(false),
                Entropy = await ComputeEntropyAsync(
                    fullPath, 0, Math.Min(info.Length, EntropySampleLimitBytes), ct).ConfigureAwait(false)
            };

            int headerLength = checked((int)Math.Min(info.Length, 4096L));
            byte[] header = new byte[headerLength];
            await using (FileStream fs = new(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int offset = 0;
                while (offset < header.Length)
                {
                    int read = await fs.ReadAsync(header.AsMemory(offset), ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset != header.Length) Array.Resize(ref header, offset);
            }

            if (LooksLikePe(header))
                await ParsePeAsync(fullPath, header, result, ct).ConfigureAwait(false);
            else if (LooksLikeElf(header))
                ParseElf(header, result);
            else if (LooksLikeMachO(header))
                ParseMachO(header, result);
            else
                result.Notes.Add(
                    "No supported executable container signature was recognized; hash, size, entropy and string extraction remain available.");

            if (result.Entropy >= 7.4)
                result.Notes.Add(
                    "High whole-file entropy can be caused by compression, encryption, packed resources or already-compressed media; it is a signal, not a verdict.");

            return result;
        }

        public static async Task<List<string>> ExtractStringsAsync(
            string workspaceRoot, string relativePath, CancellationToken ct,
            int minLength = DefaultMinStringLength, int maxResults = DefaultMaxStrings)
        {
            if (minLength < 3 || minLength > 128) throw new ArgumentOutOfRangeException(nameof(minLength));
            if (maxResults < 1 || maxResults > 10_000) throw new ArgumentOutOfRangeException(nameof(maxResults));

            string fullPath = BinaryWorkspacePathPolicy.ResolveSafeFile(workspaceRoot, relativePath);
            byte[] bytes = await ReadBoundedAsync(fullPath, 128L * 1024 * 1024, ct).ConfigureAwait(false);
            var results = new List<string>(Math.Min(maxResults, 512));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var claimed = new bool[bytes.Length];
            ExtractAscii(bytes, minLength, maxResults, results, seen, claimed);
            // A UTF-16LE string can start at either byte parity relative to the file - it is not
            // guaranteed to fall on an even offset just because the file itself does. Scanning only
            // stride-2-from-0 silently misses every wide string that starts at an odd offset (which
            // is the common case once any odd-length ASCII/section data precedes it), so both
            // alignments are scanned and deduplicated via the shared `seen` set. `claimed` keeps
            // either pass from reinterpreting a byte the ASCII pass already matched.
            if (results.Count < maxResults)
                ExtractUtf16Le(bytes, 0, minLength, maxResults, results, seen, claimed);
            if (results.Count < maxResults)
                ExtractUtf16Le(bytes, 1, minLength, maxResults, results, seen, claimed);
            return results;
        }

        public static async Task<List<ExternalToolInfo>> ProbeExternalToolsAsync(CancellationToken ct)
        {
            return new List<ExternalToolInfo>
            {
                new() { Id = "llvm-objdump", DisplayName = "LLVM objdump", Path = await FindExecutableAsync("llvm-objdump.exe", ct).ConfigureAwait(false) },
                new() { Id = "objdump", DisplayName = "GNU objdump", Path = await FindExecutableAsync("objdump.exe", ct).ConfigureAwait(false) },
                new() { Id = "dumpbin", DisplayName = "Microsoft DUMPBIN", Path = await FindExecutableAsync("dumpbin.exe", ct).ConfigureAwait(false) },
                new() { Id = "radare2", DisplayName = "radare2", Path = await FindExecutableAsync("radare2.exe", ct).ConfigureAwait(false) ?? await FindExecutableAsync("r2.exe", ct).ConfigureAwait(false) },
                new() { Id = "ghidra-headless", DisplayName = "Ghidra analyzeHeadless", Path = await FindGhidraHeadlessAsync(ct).ConfigureAwait(false) }
            };
        }

        public static async Task<(string ToolId, int ExitCode, string Output)> DisassembleAsync(
            string workspaceRoot, string relativePath, string? preferredTool, CancellationToken ct)
        {
            string target = BinaryWorkspacePathPolicy.ResolveSafeFile(workspaceRoot, relativePath);
            List<ExternalToolInfo> tools = await ProbeExternalToolsAsync(ct).ConfigureAwait(false);
            ExternalToolInfo? selected = null;

            if (!string.IsNullOrWhiteSpace(preferredTool))
                selected = tools.FirstOrDefault(t =>
                    t.Available && string.Equals(t.Id, preferredTool, StringComparison.OrdinalIgnoreCase));

            selected ??= tools.FirstOrDefault(t => t.Available && t.Id != "ghidra-headless");
            if (selected == null)
            {
                return ("none", -1,
                    "No supported read-only disassembler was found. Install LLVM objdump, GNU objdump, Visual Studio DUMPBIN or radare2; Ghidra is detected separately for decompilation workflows.");
            }

            var args = new List<string>();
            switch (selected.Id)
            {
                case "llvm-objdump":
                    args.Add("--all-headers");
                    args.Add("--disassemble");
                    args.Add("--demangle");
                    args.Add(target);
                    break;
                case "objdump":
                    args.Add("-x");
                    args.Add("-d");
                    args.Add("-C");
                    args.Add(target);
                    break;
                case "dumpbin":
                    args.Add("/headers");
                    args.Add("/imports");
                    args.Add("/exports");
                    args.Add("/disasm");
                    args.Add(target);
                    break;
                case "radare2":
                    args.Add("-q");
                    args.Add("-A");
                    args.Add("-c");
                    args.Add("iI;iS;ii;ie;afl");
                    args.Add(target);
                    break;
                default:
                    return (selected.Id, -1,
                        "The selected tool is discoverable but does not have a fixed read-only adapter yet.");
            }

            (int exitCode, string output) = await RunProcessAsync(
                selected.Path!, Path.GetFullPath(workspaceRoot), args, TimeSpan.FromMinutes(5), ct)
                .ConfigureAwait(false);
            if (output.Length > MaxRenderedToolChars)
                output = output[..MaxRenderedToolChars]
                    + "\n… (tool output truncated by PaRuX; rerun the tool manually for the complete listing)";
            return (selected.Id, exitCode, output);
        }

        public static string RenderToolStatus(IEnumerable<ExternalToolInfo> tools) =>
            string.Join("\n", tools.Select(t =>
                "- " + t.DisplayName + ": " + (t.Available ? t.Path : "not found")));

        private static bool LooksLikePe(byte[] header) =>
            header.Length >= 64 && header[0] == (byte)'M' && header[1] == (byte)'Z';

        private static bool LooksLikeElf(byte[] header) =>
            header.Length >= 20 && header[0] == 0x7F && header[1] == (byte)'E'
            && header[2] == (byte)'L' && header[3] == (byte)'F';

        private static bool LooksLikeMachO(byte[] header)
        {
            if (header.Length < 4) return false;
            uint magic = BitConverter.ToUInt32(header, 0);
            return magic is 0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE
                or 0xCAFEBABE or 0xBEBAFECA;
        }

        private static async Task ParsePeAsync(
            string path, byte[] initialHeader, BinaryAnalysisResult result, CancellationToken ct)
        {
            result.Format = "PE/COFF";
            result.Endianness = "little";
            if (initialHeader.Length < 0x40)
            {
                result.Notes.Add("Truncated DOS header.");
                return;
            }

            int peOffset = BitConverter.ToInt32(initialHeader, 0x3C);
            if (peOffset < 0 || peOffset > 16 * 1024 * 1024)
            {
                result.Notes.Add("Invalid PE header offset.");
                return;
            }

            await using FileStream fs = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (peOffset + 24L > fs.Length)
            {
                result.Notes.Add("PE header offset lies outside the file.");
                return;
            }

            fs.Position = peOffset;
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            uint signature = br.ReadUInt32();
            if (signature != 0x00004550)
            {
                result.Notes.Add("MZ header is present but PE signature is invalid.");
                return;
            }

            ushort machine = br.ReadUInt16();
            ushort declaredSectionCount = br.ReadUInt16();
            uint timestamp = br.ReadUInt32();
            br.ReadUInt32();
            br.ReadUInt32();
            ushort optionalHeaderSize = br.ReadUInt16();
            br.ReadUInt16();

            result.Architecture = PeMachineName(machine);
            try { result.PeTimestampUtc = timestamp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timestamp); }
            catch (ArgumentOutOfRangeException)
            {
                result.Notes.Add("PE timestamp is outside the supported Unix-time range.");
            }

            long optionalStart = fs.Position;
            if (optionalHeaderSize < 2 || optionalStart + optionalHeaderSize > fs.Length)
            {
                result.Notes.Add("Optional header is absent or truncated.");
                return;
            }

            ushort magic = br.ReadUInt16();
            result.Is64Bit = magic == 0x20B;
            if (magic != 0x10B && magic != 0x20B)
                result.Notes.Add("Unrecognized PE optional-header magic 0x"
                    + magic.ToString("X", CultureInfo.InvariantCulture) + ".");

            if (optionalHeaderSize >= 70)
            {
                fs.Position = optionalStart + 16;
                uint entryPoint = br.ReadUInt32();
                result.EntryPoint = "RVA 0x" + entryPoint.ToString("X", CultureInfo.InvariantCulture);

                fs.Position = optionalStart + (result.Is64Bit ? 24 : 28);
                ulong imageBase = result.Is64Bit ? br.ReadUInt64() : br.ReadUInt32();
                result.ImageBase = "0x" + imageBase.ToString("X", CultureInfo.InvariantCulture);

                fs.Position = optionalStart + 68;
                result.Subsystem = PeSubsystemName(br.ReadUInt16());
            }

            int dataDirectoryOffset = result.Is64Bit ? 112 : 96;
            if (optionalHeaderSize >= dataDirectoryOffset + 15 * 8)
            {
                fs.Position = optionalStart + dataDirectoryOffset + 14 * 8;
                uint clrRva = br.ReadUInt32();
                uint clrSize = br.ReadUInt32();
                result.IsManagedDotNet = clrRva != 0 && clrSize != 0;
            }

            int sectionCount = Math.Min((int)declaredSectionCount, MaxPeSections);
            if (declaredSectionCount > MaxPeSections)
                result.Notes.Add("PE section count exceeded the PaRuX parser safety cap; only the first "
                    + MaxPeSections.ToString(CultureInfo.InvariantCulture) + " entries were considered.");

            long sectionTable = optionalStart + optionalHeaderSize;
            for (int i = 0; i < sectionCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                long sectionOffset = sectionTable + i * 40L;
                if (sectionOffset + 40 > fs.Length)
                {
                    result.Notes.Add("Section table is truncated.");
                    break;
                }

                fs.Position = sectionOffset;
                string name = Encoding.ASCII.GetString(br.ReadBytes(8)).TrimEnd('\0');
                uint virtualSize = br.ReadUInt32();
                uint virtualAddress = br.ReadUInt32();
                uint rawSize = br.ReadUInt32();
                uint rawPointer = br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt16();
                br.ReadUInt16();
                uint characteristics = br.ReadUInt32();

                double entropy = 0;
                if (rawSize > 0 && rawPointer < fs.Length)
                {
                    long readable = Math.Min((long)rawSize, fs.Length - rawPointer);
                    entropy = await ComputeEntropyAsync(
                        path, rawPointer, Math.Min(readable, 16L * 1024 * 1024), ct).ConfigureAwait(false);
                }

                result.Sections.Add(new BinarySectionInfo
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name,
                    VirtualAddress = virtualAddress,
                    VirtualSize = virtualSize,
                    RawSize = rawSize,
                    Characteristics = characteristics,
                    Entropy = entropy
                });
            }
        }

        private static void ParseElf(byte[] header, BinaryAnalysisResult result)
        {
            result.Format = "ELF";
            result.Is64Bit = header[4] == 2;
            result.Endianness = header[5] == 2 ? "big" : "little";
            bool little = header[5] != 2;
            ushort machine = little
                ? (ushort)(header[18] | header[19] << 8)
                : (ushort)(header[18] << 8 | header[19]);
            result.Architecture = ElfMachineName(machine);
            result.Notes.Add(
                "ELF container recognized. Detailed symbol/relocation/disassembly evidence is available through the external objdump/radare2 adapters when installed.");
        }

        private static void ParseMachO(byte[] header, BinaryAnalysisResult result)
        {
            uint magic = BitConverter.ToUInt32(header, 0);
            bool fat = magic is 0xCAFEBABE or 0xBEBAFECA;
            result.Format = fat ? "Mach-O universal/fat" : "Mach-O";

            if (fat)
            {
                result.Architecture = "universal/fat";
                result.Endianness = magic == 0xBEBAFECA ? "big" : "little/swapped";
                result.Notes.Add(
                    "Mach-O fat container recognized. Individual slice architectures should be inspected with an installed LLVM/GNU objdump adapter.");
                return;
            }

            bool bigEndian = magic is 0xCEFAEDFE or 0xCFFAEDFE;
            result.Is64Bit = magic is 0xFEEDFACF or 0xCFFAEDFE;
            result.Endianness = bigEndian ? "big" : "little";
            if (header.Length >= 8)
            {
                uint cpuType = ReadUInt32(header, 4, littleEndian: !bigEndian);
                result.Architecture = MachCpuTypeName(cpuType);
            }
            result.Notes.Add(
                "Mach-O container recognized. Detailed load-command and symbol inspection should use an installed LLVM/GNU objdump adapter.");
        }

        private static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        {
            if (offset < 0 || offset + 4 > data.Length) return 0;
            return littleEndian
                ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
                : (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
        }

        private static string PeMachineName(ushort machine) => machine switch
        {
            0x014c => "x86",
            0x8664 => "x86-64/AMD64",
            0x01c0 => "ARM",
            0x01c4 => "ARMv7 Thumb-2",
            0xAA64 => "ARM64",
            0x0200 => "IA-64",
            _ => "PE machine 0x" + machine.ToString("X4", CultureInfo.InvariantCulture)
        };

        private static string ElfMachineName(ushort machine) => machine switch
        {
            3 => "x86",
            8 => "MIPS",
            40 => "ARM",
            62 => "x86-64/AMD64",
            183 => "ARM64/AArch64",
            243 => "RISC-V",
            _ => "ELF machine " + machine.ToString(CultureInfo.InvariantCulture)
        };

        private static string MachCpuTypeName(uint cpuType) => cpuType switch
        {
            7 => "x86",
            0x01000007 => "x86-64/AMD64",
            12 => "ARM",
            0x0100000C => "ARM64/AArch64",
            18 => "PowerPC",
            0x01000012 => "PowerPC64",
            _ => "Mach-O CPU type 0x" + cpuType.ToString("X8", CultureInfo.InvariantCulture)
        };

        private static string PeSubsystemName(ushort subsystem) => subsystem switch
        {
            1 => "Native",
            2 => "Windows GUI",
            3 => "Windows Console",
            7 => "POSIX Console",
            9 => "Windows CE GUI",
            10 => "EFI Application",
            11 => "EFI Boot Service Driver",
            12 => "EFI Runtime Driver",
            13 => "EFI ROM",
            14 => "Xbox",
            16 => "Windows Boot Application",
            _ => "PE subsystem " + subsystem.ToString(CultureInfo.InvariantCulture)
        };

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            await using FileStream fs = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using SHA256 sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static async Task<double> ComputeEntropyAsync(
            string path, long offset, long count, CancellationToken ct)
        {
            if (count <= 0) return 0;
            long[] frequencies = new long[256];
            long total = 0;
            byte[] buffer = new byte[128 * 1024];
            await using FileStream fs = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            fs.Position = Math.Max(0, offset);
            long remaining = Math.Min(count, Math.Max(0, fs.Length - fs.Position));
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await fs.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) break;
                for (int i = 0; i < read; i++) frequencies[buffer[i]]++;
                total += read;
                remaining -= read;
            }
            if (total == 0) return 0;
            double entropy = 0;
            foreach (long frequency in frequencies)
            {
                if (frequency == 0) continue;
                double p = (double)frequency / total;
                entropy -= p * Math.Log(p, 2);
            }
            return entropy;
        }

        private static async Task<byte[]> ReadBoundedAsync(
            string path, long maxBytes, CancellationToken ct)
        {
            var info = new FileInfo(path);
            int length = checked((int)Math.Min(info.Length, maxBytes));
            byte[] data = new byte[length];
            await using FileStream fs = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int offset = 0;
            while (offset < data.Length)
            {
                int read = await fs.ReadAsync(data.AsMemory(offset), ct).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }
            if (offset != data.Length) Array.Resize(ref data, offset);
            return data;
        }

        private static void ExtractAscii(
            byte[] bytes, int minLength, int maxResults,
            List<string> results, HashSet<string> seen, bool[] claimed)
        {
            var sb = new StringBuilder();
            int runStart = -1;
            void Flush(int end)
            {
                if (sb.Length >= minLength)
                {
                    string value = sb.ToString();
                    if (seen.Add(value)) results.Add(value);
                    for (int i = runStart; i < end; i++) claimed[i] = true;
                }
                sb.Clear();
                runStart = -1;
            }

            for (int i = 0; i < bytes.Length && results.Count < maxResults; i++)
            {
                byte b = bytes[i];
                if (b is >= 0x20 and <= 0x7E || b == (byte)'\t')
                {
                    if (runStart < 0) runStart = i;
                    sb.Append((char)b);
                }
                else Flush(i);
            }
            if (results.Count < maxResults) Flush(bytes.Length);
        }

        private static void ExtractUtf16Le(
            byte[] bytes, int startOffset, int minLength, int maxResults,
            List<string> results, HashSet<string> seen, bool[] claimed)
        {
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length >= minLength)
                {
                    string value = sb.ToString();
                    if (seen.Add(value)) results.Add(value);
                }
                sb.Clear();
            }

            for (int i = startOffset; i + 1 < bytes.Length && results.Count < maxResults; i += 2)
            {
                // A byte already claimed by a real ASCII match cannot also be the start of a
                // genuine wide-char run: that would require the same byte to simultaneously be
                // narrow text and (paired with a coincidental following 0x00) an unrelated
                // wide-char low byte, which happens by pure accident right where a narrow string
                // ends and a null separator follows - not a real UTF-16LE string.
                if (claimed[i] || claimed[i + 1]) { Flush(); continue; }
                ushort c = (ushort)(bytes[i] | bytes[i + 1] << 8);
                if (c is >= 0x20 and <= 0x7E || c == '\t') sb.Append((char)c);
                else Flush();
            }
            if (results.Count < maxResults) Flush();
        }

        private static async Task<string?> FindExecutableAsync(string fileName, CancellationToken ct)
        {
            (int exit, string output) = await RunProcessAsync(
                "where.exe", Environment.CurrentDirectory, new[] { fileName }, TimeSpan.FromSeconds(5), ct)
                .ConfigureAwait(false);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(File.Exists);
        }

        private static async Task<string?> FindGhidraHeadlessAsync(CancellationToken ct)
        {
            string? onPath = await FindExecutableAsync("analyzeHeadless.bat", ct).ConfigureAwait(false);
            if (onPath != null) return onPath;

            string? ghidraHome = Environment.GetEnvironmentVariable("GHIDRA_HOME");
            if (!string.IsNullOrWhiteSpace(ghidraHome))
            {
                string candidate = Path.Combine(ghidraHome, "support", "analyzeHeadless.bat");
                if (File.Exists(candidate)) return candidate;
            }

            string[] commonRoots =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ghidra"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ghidra")
            };
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (string root in commonRoots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                try
                {
                    string? candidate = Directory.EnumerateFiles(root, "analyzeHeadless.bat", options).FirstOrDefault();
                    if (candidate != null) return candidate;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return null;
        }

        private static async Task<(int ExitCode, string Output)> RunProcessAsync(
            string executable, string workingDirectory, IEnumerable<string> arguments,
            TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (string arg in arguments) psi.ArgumentList.Add(arg);

                using var process = new Process { StartInfo = psi };
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (-1, "Tool timed out after "
                        + timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " seconds and was terminated.");
                }

                string output = await stdout.ConfigureAwait(false);
                string error = await stderr.ConfigureAwait(false);
                return (process.ExitCode,
                    (output + (string.IsNullOrWhiteSpace(error) ? string.Empty : "\n[stderr]\n" + error)).Trim());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (-1, "Could not execute tool: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Shared containment policy for all Binary Analysis readers/decompilers. String-prefix checks
    /// alone are insufficient because a junction or symlink inside the workspace can point outside
    /// it. PaRuX rejects reparse-point roots and every reparse point in the resolved path before
    /// opening the file.
    /// </summary>
    internal static class BinaryWorkspacePathPolicy
    {
        public static string ResolveSafeFile(string workspaceRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
                throw new InvalidOperationException("Binary analysis workspace is not configured.");
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("A relative artifact path is required.", nameof(relativePath));

            string root = Path.GetFullPath(workspaceRoot);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("Binary analysis workspace does not exist: " + root);
            if (IsReparsePoint(root))
                throw new InvalidOperationException(
                    "Binary analysis workspace roots that are symbolic links or junctions are not accepted.");

            string full = Path.GetFullPath(Path.Combine(root, relativePath));
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That artifact path would escape the binary analysis workspace.");

            EnsureNoReparseTraversal(root, full);
            if (!File.Exists(full))
                throw new FileNotFoundException("Binary artifact does not exist.", relativePath);
            return full;
        }

        private static void EnsureNoReparseTraversal(string root, string full)
        {
            string relative = Path.GetRelativePath(root, full);
            string current = root;
            foreach (string segment in relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                    throw new InvalidOperationException(
                        "Binary analysis does not follow symbolic links or junctions outside the selected workspace.");
            }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }
    }
}
