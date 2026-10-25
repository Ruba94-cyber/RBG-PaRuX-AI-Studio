using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Models that Ollama and LM Studio already store. Discovery must only read: nothing in the other
/// application's store changes. Import hard-links into PaRuX's library, and copies only with explicit
/// consent and enough free space. Every store here is a synthetic fixture under a temporary folder, so
/// no real Ollama or LM Studio installation is read.
/// </summary>
public sealed class ExternalModelStoreImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-external-" + Guid.NewGuid().ToString("N"));
    private string OllamaRoot => Path.Combine(_root, "ollama");
    private string LmStudioRoot => Path.Combine(_root, "lmstudio");
    private string LibraryRoot => Path.Combine(_root, "library");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Discovery_lists_an_ollama_model_by_its_tag_and_says_it_is_text_only()
    {
        byte[] weights = Gguf(256);
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", weights);

        ExternalModelCandidate candidate = Assert.Single(Discover());

        Assert.Equal(ExternalModelStoreImporter.OllamaSource, candidate.Source);
        Assert.Equal("tinymodel:latest", candidate.DisplayName);
        Assert.Equal(blob, candidate.SourcePath);
        Assert.Equal(weights.Length, candidate.SizeBytes);
        Assert.Equal("tinymodel-latest.gguf", candidate.SuggestedFileName);
        Assert.False(candidate.AlreadyImported);
        Assert.Null(candidate.ProjectorSourcePath);
        Assert.Contains("text-only", candidate.TextOnlyNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Discovery_ignores_a_blob_that_is_not_a_gguf_file()
    {
        WriteOllamaModel("registry.ollama.ai", "library", "notmodel", "latest", Encoding.ASCII.GetBytes("not a model at all"));

        Assert.Empty(Discover());
    }

    [Fact]
    public void Discovery_lists_an_lm_studio_model_with_its_projector_and_never_lists_the_projector()
    {
        WriteLmStudioFile("publisher/repo/Qwen3-VL-8B-Q4_K_M.gguf", Gguf(300));
        WriteLmStudioFile("publisher/repo/mmproj-Qwen3-VL-8B-F16.gguf", Gguf(40));
        WriteLmStudioFile("publisher/repo/Plain-Q8_0.gguf", Encoding.ASCII.GetBytes("text"));

        ExternalModelCandidate candidate = Assert.Single(Discover().Where(c => c.Source == ExternalModelStoreImporter.LmStudioSource));

        Assert.Equal("publisher/repo/Qwen3-VL-8B-Q4_K_M.gguf", candidate.DisplayName);
        Assert.NotNull(candidate.ProjectorSourcePath);
        Assert.EndsWith("mmproj-Qwen3-VL-8B-F16.gguf", candidate.ProjectorSourcePath, StringComparison.Ordinal);
        Assert.Equal(340, candidate.CopySizeBytes);
        Assert.Null(candidate.TextOnlyNote);
    }

    [Fact]
    public void Discovery_does_not_change_the_other_applications_store()
    {
        WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", Gguf(128));
        WriteLmStudioFile("publisher/repo/Qwen3-VL-8B-Q4_K_M.gguf", Gguf(64));
        string[] before = Snapshot();

        _ = Discover();

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void A_model_already_in_the_library_is_flagged_as_imported()
    {
        byte[] weights = Gguf(200);
        WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", weights);
        Directory.CreateDirectory(LibraryRoot);
        File.WriteAllBytes(Path.Combine(LibraryRoot, "tinymodel-latest.gguf"), weights);

        ExternalModelCandidate candidate = Assert.Single(Discover());

        Assert.True(candidate.AlreadyImported);
    }

    [Fact]
    public void Import_hard_links_the_model_into_the_library_without_needing_free_space()
    {
        byte[] weights = Gguf(512);
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", weights);

        ManagedModelInfo model = ExternalModelStoreImporter.Import(
            blob, copyConsent: false, Roots(), LibraryRoot, freeSpace: _ => 0);

        Assert.Equal("tinymodel-latest.gguf", model.FileName);
        Assert.Equal(weights.Length, model.SizeBytes);
        Assert.True(File.Exists(blob));
        Assert.Equal(weights, File.ReadAllBytes(Path.Combine(LibraryRoot, "tinymodel-latest.gguf")));
    }

    [Fact]
    public void Import_refuses_a_copy_without_consent_when_no_link_is_possible()
    {
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", Gguf(64));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalModelStoreImporter.Import(blob, copyConsent: false, Roots(), LibraryRoot, hardLink: (_, _) => false, freeSpace: _ => long.MaxValue));

        Assert.Contains("consent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "tinymodel-latest.gguf")));
    }

    [Fact]
    public void Import_copies_only_with_consent_and_leaves_the_source_in_place()
    {
        byte[] weights = Gguf(96);
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", weights);

        ManagedModelInfo model = ExternalModelStoreImporter.Import(
            blob, copyConsent: true, Roots(), LibraryRoot, hardLink: (_, _) => false, freeSpace: _ => long.MaxValue);

        Assert.Equal(weights.Length, model.SizeBytes);
        Assert.Equal(weights, File.ReadAllBytes(blob));
    }

    [Fact]
    public void Import_cancellation_during_copy_removes_the_partial_destination()
    {
        byte[] weights = Gguf(3 * 1024 * 1024);
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", weights);
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAfterFirstChunkProgress(cancellation);

        Assert.ThrowsAny<OperationCanceledException>(() => ExternalModelStoreImporter.Import(
            blob, copyConsent: true, Roots(), LibraryRoot,
            hardLink: (_, _) => false,
            freeSpace: _ => long.MaxValue,
            cancellationToken: cancellation.Token,
            copyProgress: progress));

        Assert.InRange(progress.ReportedBytes, 1, weights.Length - 1);
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "tinymodel-latest.gguf")));
        Assert.False(Assert.Single(Discover()).AlreadyImported);
        Assert.True(File.Exists(blob));
    }

    [Fact]
    public void Import_copy_failure_during_copy_removes_the_partial_destination()
    {
        byte[] weights = Gguf(3 * 1024 * 1024);
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", weights);
        var progress = new ThrowAfterFirstChunkProgress();

        Assert.Throws<IOException>(() => ExternalModelStoreImporter.Import(
            blob, copyConsent: true, Roots(), LibraryRoot,
            hardLink: (_, _) => false,
            freeSpace: _ => long.MaxValue,
            copyProgress: progress));

        Assert.InRange(progress.ReportedBytes, 1, weights.Length - 1);
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "tinymodel-latest.gguf")));
        Assert.False(Assert.Single(Discover()).AlreadyImported);
        Assert.True(File.Exists(blob));
    }

    [Fact]
    public async Task Import_route_passes_request_aborted_to_the_importer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!string.Equals(context.Request.Path.Value, ExternalModelImportRoute.Path, StringComparison.Ordinal))
            {
                await next(context);
                return;
            }

            using var aborted = new CancellationTokenSource();
            context.RequestAborted = aborted.Token;
            aborted.Cancel();
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = 499;
            }
        });
        ExternalModelImportRoute.Map(app, Roots, () => LibraryRoot);

        await app.StartAsync();
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(ExternalModelImportRoute.Path,
            new ExternalModelImportRequest { SourcePath = Path.Combine(_root, "missing.gguf") });

        Assert.Equal(499, (int)response.StatusCode);
    }

    [Fact]
    public void Import_refuses_a_copy_that_would_leave_too_little_free_space()
    {
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", Gguf(96));

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalModelStoreImporter.Import(blob, copyConsent: true, Roots(), LibraryRoot, hardLink: (_, _) => false, freeSpace: _ => 0));

        Assert.Contains("Not enough free space", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "tinymodel-latest.gguf")));
    }

    [Fact]
    public void Import_refuses_any_path_that_discovery_did_not_return()
    {
        string stray = Path.Combine(_root, "elsewhere.gguf");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(stray, Gguf(32));

        Assert.Throws<InvalidDataException>(() =>
            ExternalModelStoreImporter.Import(stray, copyConsent: true, Roots(), LibraryRoot));
    }

    [Fact]
    public void Import_of_an_lm_studio_model_brings_its_projector_along()
    {
        WriteLmStudioFile("publisher/repo/Qwen3-VL-8B-Q4_K_M.gguf", Gguf(300));
        WriteLmStudioFile("publisher/repo/mmproj-Qwen3-VL-8B-F16.gguf", Gguf(40));
        ExternalModelCandidate candidate = Assert.Single(Discover().Where(c => c.Source == ExternalModelStoreImporter.LmStudioSource));

        ManagedModelInfo model = ExternalModelStoreImporter.Import(candidate.SourcePath, copyConsent: false, Roots(), LibraryRoot);

        Assert.Equal("mmproj-Qwen3-VL-8B-F16.gguf", model.ProjectorFileName);
        Assert.True(File.Exists(Path.Combine(LibraryRoot, "Qwen3-VL-8B-Q4_K_M.gguf")));
        Assert.True(File.Exists(Path.Combine(LibraryRoot, "mmproj-Qwen3-VL-8B-F16.gguf")));
    }

    [Fact]
    public void A_projector_in_another_folder_is_never_paired_with_the_model()
    {
        WriteLmStudioFile("publisher/repo/Qwen3-VL-8B-Q4_K_M.gguf", Gguf(300));
        WriteLmStudioFile("publisher/other/mmproj-Qwen3-VL-8B-F16.gguf", Gguf(40));

        ExternalModelCandidate candidate = Assert.Single(Discover().Where(c => c.Source == ExternalModelStoreImporter.LmStudioSource));

        Assert.Null(candidate.ProjectorSourcePath);
        Assert.NotNull(candidate.TextOnlyNote);
    }

    [Fact]
    public void A_store_path_that_names_a_vision_family_does_not_make_a_text_model_vision_capable()
    {
        // The store's own folder name carries a family token ("llava"). Only the path inside the store may decide.
        string root = Path.Combine(_root, "llava-store");
        string path = Path.Combine(root, "publisher", "repo", "Plain-Q8_0.gguf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Gguf(64));

        ExternalModelCandidate candidate = Assert.Single(ExternalModelStoreImporter.Discover(
            new ExternalModelStoreImporter.StoreRoots(null, new[] { root }), LibraryRoot));

        Assert.Null(candidate.TextOnlyNote);
        Assert.Null(candidate.ProjectorSourcePath);
    }

    [Fact]
    public void Import_keeps_the_copy_reserve_free_even_when_the_model_alone_would_fit()
    {
        string blob = WriteOllamaModel("registry.ollama.ai", "library", "tinymodel", "latest", Gguf(96));
        long justShort = 96 + ExternalModelStoreImporter.CopyReserveBytes - 1;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalModelStoreImporter.Import(blob, copyConsent: true, Roots(), LibraryRoot, hardLink: (_, _) => false, freeSpace: _ => justShort));

        Assert.Contains("Not enough free space", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "tinymodel-latest.gguf")));
    }

    [Fact]
    public void A_failed_import_removes_the_model_link_it_made_and_never_touches_the_other_store()
    {
        WriteLmStudioFile("publisher/repo/Qwen3-VL-8B-Q4_K_M.gguf", Gguf(300));
        WriteLmStudioFile("publisher/repo/mmproj-Qwen3-VL-8B-F16.gguf", Gguf(40));
        ExternalModelCandidate candidate = Assert.Single(Discover().Where(c => c.Source == ExternalModelStoreImporter.LmStudioSource));

        // The model links (the destination gets the bytes, as a hard link would), the projector cannot link and
        // copying needs consent, so the import fails after the model is in place. The model link made for this call must go.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExternalModelStoreImporter.Import(candidate.SourcePath, copyConsent: false, Roots(), LibraryRoot,
                hardLink: (destination, source) =>
                {
                    if (source.EndsWith("mmproj-Qwen3-VL-8B-F16.gguf", StringComparison.OrdinalIgnoreCase)) return false;
                    File.Copy(source, destination);
                    return true;
                },
                freeSpace: _ => long.MaxValue));

        Assert.Contains("consent", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "Qwen3-VL-8B-Q4_K_M.gguf")));
        Assert.False(File.Exists(Path.Combine(LibraryRoot, "mmproj-Qwen3-VL-8B-F16.gguf")));
        Assert.True(File.Exists(candidate.SourcePath));
    }

    private ExternalModelStoreImporter.StoreRoots Roots() => new(OllamaRoot,
        new[] { LmStudioRoot });

    private IReadOnlyList<ExternalModelCandidate> Discover() =>
        ExternalModelStoreImporter.Discover(Roots(), LibraryRoot);

    /// <summary>Writes an Ollama manifest and its model blob the way the Ollama store lays them out.</summary>
    private string WriteOllamaModel(string host, string ns, string name, string tag, byte[] weights)
    {
        string digest = Convert.ToHexString(SHA256.HashData(weights)).ToLowerInvariant();
        string blobsDirectory = Path.Combine(OllamaRoot, "blobs");
        Directory.CreateDirectory(blobsDirectory);
        string blob = Path.Combine(blobsDirectory, "sha256-" + digest);
        File.WriteAllBytes(blob, weights);

        string manifestDirectory = Path.Combine(OllamaRoot, "manifests", host, ns, name);
        Directory.CreateDirectory(manifestDirectory);
        string manifest = "{\"schemaVersion\":2,\"layers\":[" +
            "{\"mediaType\":\"application/vnd.ollama.image.template\",\"digest\":\"sha256:" + new string('0', 64) + "\",\"size\":1}," +
            "{\"mediaType\":\"application/vnd.ollama.image.model\",\"digest\":\"sha256:" + digest + "\",\"size\":" + weights.Length + "}]}";
        File.WriteAllText(Path.Combine(manifestDirectory, tag), manifest);
        return blob;
    }

    private void WriteLmStudioFile(string relative, byte[] bytes)
    {
        string path = Path.Combine(LmStudioRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Every file under the other application's store, with its length and last write time.</summary>
    private string[] Snapshot() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Where(p => !p.StartsWith(LibraryRoot, StringComparison.OrdinalIgnoreCase))
                .Select(p => p + "|" + new FileInfo(p).Length + "|" + new FileInfo(p).LastWriteTimeUtc.Ticks)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private static byte[] Gguf(int size)
    {
        byte[] bytes = new byte[size];
        "GGUF"u8.CopyTo(bytes);
        bytes[4] = 3; // GGUF version 3, little-endian
        return bytes;
    }

    private sealed class CancelAfterFirstChunkProgress(CancellationTokenSource cancellation) : IProgress<long>
    {
        public long ReportedBytes { get; private set; }

        public void Report(long value)
        {
            ReportedBytes = value;
            cancellation.Cancel();
        }
    }

    private sealed class ThrowAfterFirstChunkProgress : IProgress<long>
    {
        public long ReportedBytes { get; private set; }

        public void Report(long value)
        {
            ReportedBytes = value;
            throw new IOException("Synthetic copy observer failure.");
        }
    }

}
