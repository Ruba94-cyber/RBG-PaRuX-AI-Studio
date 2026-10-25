using System.Net;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

[Collection("OverheadBenchmarkSerial")]
public sealed class DownloadDestructiveFlagGateTests
{
    private static readonly byte[] OriginalPayload = { 1, 3, 5, 7 };
    private static readonly byte[] ReplacementPayload = { 2, 4, 6, 8 };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Paired_device_destructive_flag_is_refused_before_service_and_owner_keeps_access(
        bool discardPartial, bool replaceExisting)
    {
        using DownloadGateFixture fixture = await DownloadGateFixture.CreateAsync();
        string fileName = "gate-" + Guid.NewGuid().ToString("N") + ".gguf";
        string filePath = Path.Combine(fixture.ModelsDirectory, fileName);
        string partialPath = filePath + ".partial";
        string partialIdentityPath = partialPath + ".source.json";

        if (discardPartial)
        {
            Directory.CreateDirectory(fixture.ModelsDirectory);
            await File.WriteAllBytesAsync(partialPath, OriginalPayload);
            await File.WriteAllTextAsync(partialIdentityPath, "{}");
        }
        else
        {
            await fixture.SeedManagedModelAsync(fileName);
        }

        fixture.Transport.Payload = ReplacementPayload;
        fixture.Transport.Reset();

        byte[] originalFile = discardPartial
            ? await File.ReadAllBytesAsync(partialPath)
            : await File.ReadAllBytesAsync(filePath);
        DateTime originalTimestamp = File.GetLastWriteTimeUtc(discardPartial ? partialPath : filePath);
        byte[]? originalIdentity = discardPartial ? await File.ReadAllBytesAsync(partialIdentityPath) : null;
        var request = Request(fileName, "alice/selected-model", discardPartial, replaceExisting);

        using HttpResponseMessage refused = await fixture.Paired.PostAsJsonAsync("/api/v1/models/downloads", request);
        await fixture.DrainUnexpectedSuccessAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await AssertTypedOwnerProblemAsync(refused);
        Assert.Equal(0, fixture.Transport.RequestCount);
        string protectedPath = discardPartial ? partialPath : filePath;
        Assert.Equal(originalFile, await File.ReadAllBytesAsync(protectedPath));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(protectedPath));
        if (originalIdentity is not null)
            Assert.Equal(originalIdentity, await File.ReadAllBytesAsync(partialIdentityPath));

        using HttpResponseMessage accepted = await fixture.Desktop.PostAsJsonAsync("/api/v1/models/downloads", request);
        ModelDownloadStatus completed = await fixture.ReadCompletedDownloadAsync(accepted);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("completed", completed.Status);
        Assert.True(fixture.Transport.RequestCount >= 2);
        Assert.Equal(ReplacementPayload, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task Paired_device_can_start_an_ordinary_download_after_destructive_flags_are_refused()
    {
        using DownloadGateFixture fixture = await DownloadGateFixture.CreateAsync();
        string refusedName = "gate-refused-" + Guid.NewGuid().ToString("N") + ".gguf";
        var destructive = Request(refusedName, "alice/selected-model", discardPartial: true);

        using HttpResponseMessage refused = await fixture.Paired.PostAsJsonAsync("/api/v1/models/downloads", destructive);
        await fixture.DrainUnexpectedSuccessAsync(refused);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await AssertTypedOwnerProblemAsync(refused);
        Assert.Equal(0, fixture.Transport.RequestCount);
        Assert.False(File.Exists(Path.Combine(fixture.ModelsDirectory, refusedName)));
        Assert.False(File.Exists(Path.Combine(fixture.ModelsDirectory, refusedName) + ".partial"));

        string ordinaryName = "gate-ordinary-" + Guid.NewGuid().ToString("N") + ".gguf";
        fixture.Transport.Payload = ReplacementPayload;
        using HttpResponseMessage accepted = await fixture.Paired.PostAsJsonAsync(
            "/api/v1/models/downloads", Request(ordinaryName, "alice/selected-model"));
        ModelDownloadStatus completed = await fixture.ReadCompletedDownloadAsync(accepted);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("completed", completed.Status);
        Assert.True(fixture.Transport.RequestCount >= 2);
        Assert.Equal(ReplacementPayload, await File.ReadAllBytesAsync(Path.Combine(fixture.ModelsDirectory, ordinaryName)));
    }

    [Fact]
    public async Task Paired_resume_does_not_inherit_a_replacement_flag_from_an_earlier_desktop_attempt()
    {
        using DownloadGateFixture fixture = await DownloadGateFixture.CreateAsync();
        string fileName = "gate-inherit-" + Guid.NewGuid().ToString("N") + ".gguf";
        string filePath = Path.Combine(fixture.ModelsDirectory, fileName);
        await fixture.SeedManagedModelAsync(fileName);

        // The desktop owner asks to replace the managed copy, and that attempt fails before any byte arrives.
        fixture.Transport.Payload = ReplacementPayload;
        fixture.Transport.FailTransferWith = HttpStatusCode.NotFound;
        ModelDownloadStatus failedAttempt;
        using (ModelDownloadService.UseModelsDirectoryForTests(fixture.ModelsDirectory))
        {
            failedAttempt = await fixture.Downloads.CreateDownloadAsync(
                Request(fileName, "alice/selected-model", replaceExisting: true), CancellationToken.None);
            ModelDownloadStatus terminal = await WaitForTerminalAsync(fixture.Downloads, failedAttempt.Id);
            Assert.Equal("failed", terminal.Status);
        }

        // A paired device resumes that attempt and asks for nothing destructive. The replacement must not come back with it.
        fixture.Transport.FailTransferWith = null;
        ModelDownloadRequest resume = Request(fileName, "alice/selected-model");
        resume.ResumeFromDownloadId = failedAttempt.Id;
        using HttpResponseMessage resumed = await fixture.Paired.PostAsJsonAsync("/api/v1/models/downloads", resume);
        await fixture.DrainUnexpectedSuccessAsync(resumed);

        Assert.Equal(OriginalPayload, await File.ReadAllBytesAsync(filePath));
    }

    private static ModelDownloadRequest Request(
        string fileName,
        string repoId,
        bool discardPartial = false,
        bool replaceExisting = false)
    {
        string revision = new('a', 40);
        return new ModelDownloadRequest
        {
            RepoId = repoId,
            Revision = revision,
            FileName = fileName,
            DownloadUrl = $"https://huggingface.co/{repoId}/resolve/{revision}/{fileName}",
            SizeBytes = ReplacementPayload.Length,
            DiscardPartial = discardPartial,
            ReplaceExisting = replaceExisting
        };
    }

    private static async Task AssertTypedOwnerProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(StatusCodes.Status403Forbidden, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Desktop owner required", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("desktop_owner_required", body.RootElement.GetProperty("code").GetString());
    }

    private sealed class DownloadGateFixture : IDisposable
    {
        private readonly string _root;
        private readonly ApiSecurityTests.PaRuXFactory _baseFactory;
        private readonly WebApplicationFactory<global::Program> _factory;
        private readonly HttpClient _transferClient;
        private readonly HttpClient _metadataClient;
        private readonly string _previousDataRoot;
        private readonly string _pairedDeviceToken;

        private DownloadGateFixture(
            string root,
            string modelsDirectory,
            ModelDownloadService downloads,
            GateTransport transport,
            HttpClient transferClient,
            HttpClient metadataClient,
            string previousDataRoot,
            string pairedDeviceToken,
            ApiSecurityTests.PaRuXFactory baseFactory,
            WebApplicationFactory<global::Program> factory)
        {
            _root = root;
            ModelsDirectory = modelsDirectory;
            Downloads = downloads;
            Transport = transport;
            _transferClient = transferClient;
            _metadataClient = metadataClient;
            _previousDataRoot = previousDataRoot;
            _pairedDeviceToken = pairedDeviceToken;
            _baseFactory = baseFactory;
            _factory = factory;
        }

        public string ModelsDirectory { get; }
        public ModelDownloadService Downloads { get; }
        public GateTransport Transport { get; }
        public HttpClient Desktop { get; private set; } = null!;
        public HttpClient Paired { get; private set; } = null!;

        public static async Task<DownloadGateFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "parux-download-gate-" + Guid.NewGuid().ToString("N"));
            string modelsDirectory = Path.Combine(root, "models");
            string stateDirectory = Path.Combine(root, "state");
            string previousDataRoot = PaRuXDataRoot.Path;
            ApiSecurityTests.PaRuXFactory? baseFactory = null;
            WebApplicationFactory<global::Program>? factory = null;
            HttpClient? transferClient = null;
            HttpClient? metadataClient = null;
            DownloadGateFixture? fixture = null;
            try
            {
                Directory.CreateDirectory(modelsDirectory);
                Directory.CreateDirectory(stateDirectory);
                PaRuXDataRoot.UseForTests(root);

                string pairedDeviceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                string pairedDeviceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pairedDeviceToken)));
                string remoteRegistryPath = Path.Combine(stateDirectory, "remote-access.json");
                await File.WriteAllTextAsync(remoteRegistryPath, JsonSerializer.Serialize(new
                {
                    enabled = true,
                    address = "10.43.0.1",
                    port = 55123,
                    certificateAddress = "10.43.0.1",
                    devices = new[]
                    {
                        new
                        {
                            id = Guid.NewGuid().ToString("N"),
                            name = "Download gate test client",
                            tokenHash = pairedDeviceHash,
                            createdUtc = DateTime.UtcNow,
                            lastSeenUtc = (DateTime?)null
                        }
                    }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

                var transport = new GateTransport(OriginalPayload);
                transferClient = new HttpClient(new GateMessageHandler(transport), disposeHandler: true);
                metadataClient = new HttpClient(new GateMessageHandler(transport), disposeHandler: true);
                var downloads = new ModelDownloadService(
                    httpClient: transferClient,
                    metadataProbe: metadataClient,
                    stateDirectory: Path.Combine(stateDirectory, "downloads"),
                    scanOrphanPartials: false);
                baseFactory = new ApiSecurityTests.PaRuXFactory();
                factory = baseFactory.WithWebHostBuilder(builder =>
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<ModelDownloadService>();
                        services.AddSingleton(downloads);
                        services.RemoveAll<RemoteAccessService>();
                        services.AddSingleton(new RemoteAccessService(remoteRegistryPath));
                        services.AddSingleton<IStartupFilter>(new ModelsDirectoryStartupFilter(modelsDirectory));
                        services.AddSingleton<IStartupFilter, SimulatedPrivateClientStartupFilter>();
                    }));

                fixture = new DownloadGateFixture(
                    root, modelsDirectory, downloads, transport, transferClient, metadataClient,
                    previousDataRoot, pairedDeviceToken, baseFactory, factory);
                fixture.InitializeClients();
                return fixture;
            }
            catch (Exception setupException)
            {
                Exception? cleanupException = null;
                if (fixture is not null)
                {
                    try { fixture.Dispose(); }
                    catch (Exception exception) { cleanupException = exception; }
                }
                else
                {
                    cleanupException = DisposeResources(factory, baseFactory, transferClient, metadataClient);
                    try { PaRuXDataRoot.UseForTests(previousDataRoot); }
                    catch (Exception exception) { cleanupException ??= exception; }
                    finally { DeleteRoot(root); }
                }

                if (cleanupException is not null)
                    throw new AggregateException("Download gate fixture setup and cleanup both failed.", setupException, cleanupException);

                ExceptionDispatchInfo.Capture(setupException).Throw();
                throw;
            }
        }

        public async Task SeedManagedModelAsync(string fileName)
        {
            Transport.Payload = OriginalPayload;
            using IDisposable scope = ModelDownloadService.UseModelsDirectoryForTests(ModelsDirectory);
            ModelDownloadStatus created = await Downloads.CreateDownloadAsync(
                Request(fileName, "alice/original-model"), CancellationToken.None);
            ModelDownloadStatus completed = await WaitForTerminalAsync(Downloads, created.Id);
            Assert.Equal("completed", completed.Status);
        }

        public async Task DrainUnexpectedSuccessAsync(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.OK) return;
            ModelDownloadStatus? created = await response.Content.ReadFromJsonAsync<ModelDownloadStatus>();
            if (created is not null) await WaitForTerminalAsync(Downloads, created.Id);
        }

        public async Task<ModelDownloadStatus> ReadCompletedDownloadAsync(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ModelDownloadStatus? created = await response.Content.ReadFromJsonAsync<ModelDownloadStatus>();
            Assert.NotNull(created);
            return await WaitForTerminalAsync(Downloads, created!.Id);
        }

        private void InitializeClients()
        {
            Desktop = _factory.CreateClient();
            Desktop.DefaultRequestHeaders.Add(
                "X-PaRuX-Token", _factory.Services.GetRequiredService<ServerState>().Token);
            Paired = _factory.CreateClient();
            Paired.DefaultRequestHeaders.Add("X-PaRuX-Test-Remote", "1");
            Paired.DefaultRequestHeaders.Add("X-PaRuX-Device-Token", _pairedDeviceToken);
        }

        public void Dispose()
        {
            Exception? disposalException = DisposeResources(
                Desktop, Paired, _factory, _baseFactory, _transferClient, _metadataClient);
            try { PaRuXDataRoot.UseForTests(_previousDataRoot); }
            finally
            {
                DeleteRoot(_root);
            }

            if (disposalException is not null)
                ExceptionDispatchInfo.Capture(disposalException).Throw();
        }

        private static Exception? DisposeResources(params IDisposable?[] resources)
        {
            Exception? firstException = null;
            foreach (IDisposable? resource in resources)
            {
                try { resource?.Dispose(); }
                catch (Exception exception) { firstException ??= exception; }
            }

            return firstException;
        }

        private static void DeleteRoot(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class ModelsDirectoryStartupFilter(string modelsDirectory) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    using IDisposable scope = ModelDownloadService.UseModelsDirectoryForTests(modelsDirectory);
                    await continuation();
                });
                next(app);
            };
        }

        private sealed class SimulatedPrivateClientStartupFilter : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    if (context.Request.Headers["X-PaRuX-Test-Remote"] == "1")
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse("10.43.0.7");
                        context.Request.Scheme = "https";
                    }
                    await continuation();
                });
                next(app);
            };
        }
    }

    private static async Task<ModelDownloadStatus> WaitForTerminalAsync(ModelDownloadService service, string id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            ModelDownloadStatus? status = service.Get(id);
            if (status is not null && status.Status is "completed" or "failed" or "cancelled") return status;
            await Task.Delay(15);
        }

        throw new TimeoutException("The test download did not reach a terminal state.");
    }

    private sealed class GateTransport(byte[] initialPayload)
    {
        private int _requestCount;
        private byte[] _payload = initialPayload;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public HttpStatusCode? FailTransferWith { get; set; }
        public byte[] Payload
        {
            get => Volatile.Read(ref _payload);
            set => Volatile.Write(ref _payload, value);
        }

        public void Reset() => Interlocked.Exchange(ref _requestCount, 0);
        public void RecordRequest() => Interlocked.Increment(ref _requestCount);
    }

    private sealed class GateMessageHandler(GateTransport transport) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            transport.RecordRequest();
            byte[] payload = transport.Payload;
            bool isHead = request.Method == HttpMethod.Head;
            if (!isHead && transport.FailTransferWith is { } failure)
                return Task.FromResult(new HttpResponseMessage(failure) { RequestMessage = request });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(isHead ? Array.Empty<byte>() : payload)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }
}
