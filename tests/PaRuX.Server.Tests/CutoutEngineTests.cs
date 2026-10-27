using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>Background removal: request validation, PNG alpha facts, the pinned-model downloader, the setup plan and the service guards. Hermetic.</summary>
public sealed class CutoutEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "parux-cutout-tests-" + Guid.NewGuid().ToString("N"));

    public CutoutEngineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    // ---- fixtures: header-only images (the validator reads headers, never decodes pixels) ----

    private static byte[] Png(int width, int height, byte colorType = 6, bool trns = false)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            bytes.AddRange(length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            bytes.AddRange(data);
            bytes.AddRange(new byte[4]); // CRC is not checked by the inspector
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; ihdr[9] = colorType;
        Chunk("IHDR", ihdr);
        if (trns) Chunk("tRNS", new byte[] { 0, 0 });
        Chunk("IDAT", new byte[] { 0x78, 0x9C });
        Chunk("IEND", Array.Empty<byte>());
        return bytes.ToArray();
    }

    private static byte[] Jpeg(int width, int height)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        b.AddRange(System.Text.Encoding.ASCII.GetBytes("JFIF\0"));
        b.AddRange(new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        return b.ToArray();
    }

    private static byte[] WebpLossless(int width, int height)
    {
        var b = new byte[30];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        System.Text.Encoding.ASCII.GetBytes("WEBPVP8L").CopyTo(b, 8);
        b[20] = 0x2F;
        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(21, 4), bits);
        return b;
    }

    private static byte[] WebpExtended(int width, int height)
    {
        var b = new byte[30];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        System.Text.Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(b, 8);
        int w = width - 1, h = height - 1;
        b[24] = (byte)w; b[25] = (byte)(w >> 8); b[26] = (byte)(w >> 16);
        b[27] = (byte)h; b[28] = (byte)(h >> 8); b[29] = (byte)(h >> 16);
        return b;
    }

    private static CutoutRemoveRequest Request(byte[] bytes) => new() { ImageBase64 = Convert.ToBase64String(bytes), FileName = "x.png" };

    // ---- request validation ----

    [Fact]
    public void Png_jpeg_and_webp_are_accepted_with_their_real_size_and_type()
    {
        CutoutImage png = CutoutImageInspector.Validate(Request(Png(640, 480)));
        Assert.Equal((".png", "image/png", 640, 480), (png.Extension, png.MediaType, png.Width, png.Height));
        CutoutImage jpg = CutoutImageInspector.Validate(Request(Jpeg(1024, 768)));
        Assert.Equal((".jpg", "image/jpeg", 1024, 768), (jpg.Extension, jpg.MediaType, jpg.Width, jpg.Height));
        CutoutImage webpL = CutoutImageInspector.Validate(Request(WebpLossless(300, 200)));
        Assert.Equal((".webp", 300, 200), (webpL.Extension, webpL.Width, webpL.Height));
        CutoutImage webpX = CutoutImageInspector.Validate(Request(WebpExtended(4000, 3000)));
        Assert.Equal((4000, 3000), (webpX.Width, webpX.Height));
    }

    [Fact]
    public void The_file_name_is_never_trusted_only_the_bytes()
    {
        var request = new CutoutRemoveRequest { ImageBase64 = Convert.ToBase64String(Png(10, 10)), FileName = "..\\..\\evil.exe" };
        Assert.Equal(".png", CutoutImageInspector.Validate(request).Extension);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 !!!")]
    public void Missing_or_malformed_base64_is_refused(string payload)
    {
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(new CutoutRemoveRequest { ImageBase64 = payload }));
    }

    [Fact]
    public void Null_request_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(null));
    }

    [Fact]
    public void Non_images_are_refused_even_when_named_png()
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes("MZ this is an executable, not a picture");
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(text)));
        Assert.Contains("PNG, JPEG or WebP", ex.Message);
        byte[] gif = System.Text.Encoding.ASCII.GetBytes("GIF89a\x01\0\x01\0");
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(gif)));
        byte[] svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(svg)));
    }

    [Fact]
    public void A_truncated_header_is_refused()
    {
        byte[] truncated = Png(10, 10)[..20];
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(truncated)));
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(new byte[] { 0xFF, 0xD8, 0xFF })));
    }

    [Fact]
    public void A_small_file_that_declares_a_huge_canvas_is_refused()
    {
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(Png(20_000, 20_000))));
        Assert.Contains("too large", ex.Message);
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(Png(8000, 8000)))); // 64 MP
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(Png(0, 100))));
        Assert.Equal(6000, CutoutImageInspector.Validate(Request(Png(6000, 6000))).Width); // 36 MP is the limit
    }

    [Fact]
    public void An_oversized_payload_is_refused_before_it_is_decoded()
    {
        string huge = new string('A', CutoutImageInspector.MaxEncodedBytes * 4 / 3 + 1024);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(new CutoutRemoveRequest { ImageBase64 = huge }));
        Assert.Contains("larger than", ex.Message);
        byte[] over = new byte[CutoutImageInspector.MaxEncodedBytes + 1];
        Png(10, 10).CopyTo(over, 0);
        Assert.Throws<InvalidDataException>(() => CutoutImageInspector.Validate(Request(over)));
    }

    // ---- PNG alpha helper ----

    [Fact]
    public void Png_alpha_is_detected_from_the_colour_type_or_a_trns_chunk()
    {
        Assert.True(PngAlpha.HasAlphaChannel(Png(4, 4, colorType: 6)));
        Assert.True(PngAlpha.HasAlphaChannel(Png(4, 4, colorType: 4)));
        Assert.False(PngAlpha.HasAlphaChannel(Png(4, 4, colorType: 2)));
        Assert.False(PngAlpha.HasAlphaChannel(Png(4, 4, colorType: 0)));
        Assert.True(PngAlpha.HasAlphaChannel(Png(4, 4, colorType: 2, trns: true)));
        Assert.True(PngAlpha.HasAlphaChannel(Png(4, 4, colorType: 3, trns: true)));
    }

    [Fact]
    public void Anything_that_is_not_a_png_has_no_alpha()
    {
        Assert.False(PngAlpha.HasAlphaChannel(Jpeg(10, 10)));
        Assert.False(PngAlpha.HasAlphaChannel(Array.Empty<byte>()));
        Assert.False(PngAlpha.HasAlphaChannel(Png(4, 4)[..20]));
    }

    // ---- pinned model ----

    [Fact]
    public void The_pinned_model_record_is_complete_and_points_at_its_own_commit()
    {
        Assert.Matches("^[0-9a-f]{64}$", CutoutModel.Sha256);
        Assert.Matches("^[0-9a-f]{40}$", CutoutModel.Revision);
        Assert.True(CutoutModel.Bytes > 100_000_000);
        Assert.Equal("MIT", CutoutModel.License);
        Assert.Equal("huggingface.co", CutoutModel.DownloadUri.Host);
        Assert.Equal("https", CutoutModel.DownloadUri.Scheme);
        Assert.Contains("/resolve/" + CutoutModel.Revision + "/", CutoutModel.DownloadUri.AbsolutePath);
        Assert.EndsWith(CutoutModel.RepositoryPath, CutoutModel.DownloadUri.AbsolutePath);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _make;
        public FakeHandler(Func<HttpResponseMessage> make) => _make = make;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_make());
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task A_download_with_the_pinned_size_and_digest_is_kept_and_reports_progress()
    {
        byte[] body = RandomNumberGenerator.GetBytes(300_000);
        string destination = Path.Combine(_dir, "model.onnx");
        long lastDone = 0;
        var progress = new Progress<(long Done, long Total)>(p => lastDone = Math.Max(lastDone, p.Done));
        using var http = new HttpClient(new FakeHandler(() => Ok(body)));

        await PinnedFileDownloader.DownloadAsync(http, new Uri("https://huggingface.co/x/y/resolve/abc/m.onnx"), destination, body.Length, Sha(body), progress, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.True(await PinnedFileDownloader.IsVerifiedAsync(destination, body.Length, Sha(body), CancellationToken.None));
    }

    [Fact]
    public async Task A_download_with_the_wrong_digest_is_deleted_and_never_published()
    {
        byte[] body = RandomNumberGenerator.GetBytes(100_000);
        string destination = Path.Combine(_dir, "model.onnx");
        using var http = new HttpClient(new FakeHandler(() => Ok(body)));

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileDownloader.DownloadAsync(
            http, new Uri("https://huggingface.co/x/y/resolve/abc/m.onnx"), destination, body.Length, new string('0', 64), null, CancellationToken.None));

        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task A_download_with_the_wrong_size_is_never_published()
    {
        byte[] body = RandomNumberGenerator.GetBytes(100_000);
        string destination = Path.Combine(_dir, "model.onnx");
        using var shorter = new HttpClient(new FakeHandler(() => Ok(body)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileDownloader.DownloadAsync(
            shorter, new Uri("https://huggingface.co/x/y/resolve/abc/m.onnx"), destination, body.Length + 5, Sha(body), null, CancellationToken.None));
        using var longer = new HttpClient(new FakeHandler(() => Ok(body)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileDownloader.DownloadAsync(
            longer, new Uri("https://huggingface.co/x/y/resolve/abc/m.onnx"), destination, body.Length - 5, Sha(body), null, CancellationToken.None));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task A_failed_http_status_and_plain_http_are_refused()
    {
        string destination = Path.Combine(_dir, "model.onnx");
        using var notFound = new HttpClient(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<IOException>(() => PinnedFileDownloader.DownloadAsync(
            notFound, new Uri("https://huggingface.co/x/y/resolve/abc/m.onnx"), destination, 10, new string('a', 64), null, CancellationToken.None));
        using var http = new HttpClient(new FakeHandler(() => Ok(new byte[10])));
        await Assert.ThrowsAsync<InvalidDataException>(() => PinnedFileDownloader.DownloadAsync(
            http, new Uri("http://huggingface.co/x/y/resolve/abc/m.onnx"), destination, 10, new string('a', 64), null, CancellationToken.None));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task An_existing_file_is_verified_by_size_and_digest()
    {
        byte[] body = RandomNumberGenerator.GetBytes(50_000);
        string path = Path.Combine(_dir, "m.onnx");
        await File.WriteAllBytesAsync(path, body);
        Assert.True(await PinnedFileDownloader.IsVerifiedAsync(path, body.Length, Sha(body), CancellationToken.None));
        Assert.False(await PinnedFileDownloader.IsVerifiedAsync(path, body.Length + 1, Sha(body), CancellationToken.None));
        Assert.False(await PinnedFileDownloader.IsVerifiedAsync(path, body.Length, new string('f', 64), CancellationToken.None));
        Assert.False(await PinnedFileDownloader.IsVerifiedAsync(Path.Combine(_dir, "missing.onnx"), 1, Sha(body), CancellationToken.None));
    }

    // ---- setup plan ----

    [Fact]
    public void Cutout_plan_needs_the_runtime_and_the_verified_model_and_records_both_licences()
    {
        MediaSetupPlan clean = MediaSetupPlanner.PlanCutout(runtimeInstalled: false, modelInstalled: false);
        Assert.Equal(new[] { "cutout-engine", "cutout-model" }, clean.Steps.Select(s => s.Id));
        Assert.All(clean.Steps, s => { Assert.Equal(MediaSetupStepState.Pending, s.State); Assert.False(string.IsNullOrWhiteSpace(s.License)); Assert.True(s.SizeBytes > 0); });
        MediaSetupStep model = clean.Steps.Single(s => s.Id == "cutout-model");
        Assert.Equal(CutoutModel.Bytes, model.SizeBytes);
        Assert.Equal(CutoutModel.Sha256, model.Sha256);
        Assert.Equal(CutoutModel.Revision, model.Revision);
        Assert.Equal("MIT", model.License);

        Assert.False(MediaSetupPlanner.PlanCutout(true, false).Ready);
        Assert.False(MediaSetupPlanner.PlanCutout(false, true).Ready);
        Assert.True(MediaSetupPlanner.PlanCutout(true, true).Ready);
        Assert.Equal("cutout", MediaSetupService.NormalizeType("cutout"));
        Assert.Equal("image", MediaSetupService.NormalizeType("nonsense"));
    }

    // ---- service guards (no Python needed) ----

    [Fact]
    public async Task Removal_refuses_bad_input_before_it_looks_for_the_engine()
    {
        var service = new CutoutService(Path.Combine(_dir, "engine"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveBackgroundAsync(new CutoutRemoveRequest { ImageBase64 = "" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RemoveBackgroundAsync(
            Request(System.Text.Encoding.UTF8.GetBytes("not an image")), CancellationToken.None));
    }

    [Fact]
    public async Task Removal_without_the_engine_fails_honestly_and_leaves_no_work_files()
    {
        string root = Path.Combine(_dir, "engine");
        var service = new CutoutService(root);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveBackgroundAsync(Request(Png(32, 32)), CancellationToken.None));
        Assert.Contains("not set up", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(root, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(root, "work")).Any());
    }

    [Fact]
    public async Task Status_of_a_clean_pc_says_not_installed_and_names_the_model_and_licence()
    {
        var service = new CutoutService(Path.Combine(_dir, "engine"));
        CutoutEngineStatus status = await service.GetStatusAsync(CancellationToken.None);
        Assert.False(status.Installed);
        Assert.False(status.RuntimeInstalled);
        Assert.False(status.ModelInstalled);
        Assert.Equal("MIT", status.ModelLicense);
        Assert.False(string.IsNullOrWhiteSpace(status.Model));
    }

    [Fact]
    public async Task A_wrong_sized_model_file_is_not_treated_as_installed()
    {
        string root = Path.Combine(_dir, "engine");
        var service = new CutoutService(root);
        Directory.CreateDirectory(Path.GetDirectoryName(service.ModelPath)!);
        await File.WriteAllBytesAsync(service.ModelPath, new byte[1234]);
        Assert.False(await service.IsModelVerifiedAsync(CancellationToken.None));
        Assert.False((await service.GetStatusAsync(CancellationToken.None)).ModelInstalled);
    }

    [Fact]
    public void The_engine_script_runs_on_the_cpu_only_and_writes_rgba()
    {
        Assert.Contains("CPUExecutionProvider", CutoutScript.Source);
        Assert.DoesNotContain("CUDAExecutionProvider", CutoutScript.Source);
        Assert.DoesNotContain("DmlExecutionProvider", CutoutScript.Source);
        Assert.Matches(new Regex(@"putalpha"), CutoutScript.Source);
        Assert.Contains("format='PNG'", CutoutScript.Source);
    }

    [Fact]
    public void The_last_json_line_of_the_engine_output_is_parsed_and_garbage_is_refused()
    {
        var facts = CutoutService.ParseLastJsonObject("warning: something\n{\"transparentRatio\": 0.42, \"seconds\": 3.5}\n");
        Assert.Equal(0.42, facts.GetProperty("transparentRatio").GetDouble());
        Assert.Throws<InvalidDataException>(() => CutoutService.ParseLastJsonObject("no json here"));
    }
}

/// <summary>
/// Runs the real engine end to end (managed Python, onnxruntime, the pinned BiRefNet-lite model) on a real picture. It downloads about
/// 250 MB, so it runs only when PARUX_REAL_CUTOUT_IMAGE names an input image; PARUX_REAL_CUTOUT_OUT names the output folder.
/// </summary>
internal sealed class RealCutoutFactAttribute : FactAttribute
{
    public const string ImageVariable = "PARUX_REAL_CUTOUT_IMAGE";

    public RealCutoutFactAttribute()
    {
        string? image = Environment.GetEnvironmentVariable(ImageVariable);
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
            Skip = "Set " + ImageVariable + " to an existing PNG/JPEG to run the real background-removal engine (downloads ~250 MB).";
    }
}

public sealed class CutoutRealEngineTests
{
    [RealCutoutFact]
    public async Task The_real_engine_installs_verifies_its_model_and_returns_a_transparent_png()
    {
        string image = Environment.GetEnvironmentVariable(RealCutoutFactAttribute.ImageVariable)!;
        string outDir = Environment.GetEnvironmentVariable("PARUX_REAL_CUTOUT_OUT") ?? Path.Combine(Path.GetTempPath(), "parux-real-cutout");
        Directory.CreateDirectory(outDir);
        string engineRoot = Path.Combine(outDir, "engine");
        var service = new CutoutService(engineRoot);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        await service.InstallRuntimeAsync(cts.Token);
        long lastDone = 0;
        await service.DownloadModelAsync(new Progress<(long Done, long Total)>(p => lastDone = p.Done), cts.Token);
        CutoutEngineStatus status = await service.GetStatusAsync(cts.Token);
        Assert.True(status.Installed, status.LastError);
        Assert.True(status.ModelInstalled);
        Assert.False(string.IsNullOrEmpty(status.OnnxRuntimeVersion));

        byte[] input = await File.ReadAllBytesAsync(image, cts.Token);
        MediaAsset asset = await service.RemoveBackgroundAsync(
            new CutoutRemoveRequest { ImageBase64 = Convert.ToBase64String(input), FileName = Path.GetFileName(image) }, cts.Token);

        byte[] png = Convert.FromBase64String(asset.Base64Data);
        Assert.Equal("image/png", asset.MediaType);
        Assert.True(PngAlpha.HasAlphaChannel(png));
        string output = Path.Combine(outDir, "cutout.png");
        await File.WriteAllBytesAsync(output, png, cts.Token);
        CutoutImage written = CutoutImageInspector.Validate(new CutoutRemoveRequest { ImageBase64 = asset.Base64Data });
        CutoutImage source = CutoutImageInspector.Validate(new CutoutRemoveRequest { ImageBase64 = Convert.ToBase64String(input) });
        Assert.Equal((source.Width, source.Height), (written.Width, written.Height));
    }
}
