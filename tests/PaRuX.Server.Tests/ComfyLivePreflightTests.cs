using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Live preflight against a fake ComfyUI on 127.0.0.1 with an ephemeral port. No external network is used:
/// the fake answers only /object_info and /system_stats with the bodies each test supplies.
/// </summary>
public sealed class ComfyLivePreflightTests
{
    private const string UnetFile = "z_image_turbo_nvfp4.safetensors";

    private static string ObjectInfo(
        IEnumerable<string>? unetOptions = null,
        IEnumerable<string>? clipOptions = null,
        IEnumerable<string>? clipTypes = null,
        bool includeWanNode = true)
    {
        string Options(IEnumerable<string>? values) => "[[" + string.Join(",", (values ?? Array.Empty<string>()).Select(v => "\"" + v + "\"")) + "],{}]";
        var nodes = new List<string>
        {
            "\"UNETLoader\":{\"input\":{\"required\":{\"unet_name\":" + Options(unetOptions ?? new[] { UnetFile })
                + ",\"weight_dtype\":[[\"default\",\"fp8\"],{}]}},\"output\":[\"MODEL\"]}",
            "\"CLIPLoader\":{\"input\":{\"required\":{\"clip_name\":" + Options(clipOptions ?? new[] { "qwen_3_4b.safetensors" })
                + ",\"type\":" + Options(clipTypes ?? new[] { "stable_diffusion", "lumina2", "wan", "flux2" }) + "}}}",
            "\"VAELoader\":{\"input\":{\"required\":{\"vae_name\":" + Options(new[] { "ae.safetensors", "wan2.2_vae.safetensors" }) + "}}}",
            "\"KSampler\":{\"input\":{\"required\":{\"seed\":[\"INT\",{\"default\":0}],\"steps\":[\"INT\",{\"default\":20}]}}}"
        };
        if (includeWanNode) nodes.Add("\"Wan22ImageToVideoLatent\":{\"input\":{\"required\":{}}}");
        return "{" + string.Join(",", nodes) + "}";
    }

    private static JsonObject Body(params (string Id, string ClassType, JsonObject Inputs)[] nodes)
    {
        var graph = new JsonObject();
        foreach ((string id, string classType, JsonObject inputs) in nodes)
            graph[id] = new JsonObject { ["class_type"] = classType, ["inputs"] = inputs };
        return new JsonObject { ["prompt"] = graph, ["client_id"] = "test" };
    }

    private static JsonObject ZImageGraph(string unet = UnetFile, string clip = "qwen_3_4b.safetensors", string clipType = "lumina2") =>
        Body(
            ("1", "UNETLoader", new JsonObject { ["unet_name"] = unet, ["weight_dtype"] = "default" }),
            ("2", "CLIPLoader", new JsonObject { ["clip_name"] = clip, ["type"] = clipType }),
            ("3", "VAELoader", new JsonObject { ["vae_name"] = "ae.safetensors" }),
            ("4", "KSampler", new JsonObject { ["seed"] = 1, ["model"] = new JsonArray("1", 0) }));

    private static string AddNode(string objectInfo, string classType)
    {
        JsonObject catalog = JsonNode.Parse(objectInfo)!.AsObject();
        catalog[classType] = new JsonObject { ["input"] = new JsonObject { ["required"] = new JsonObject() } };
        return catalog.ToJsonString();
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        return (T)await Assert.ThrowsAsync<T>(action);
    }

    [Fact]
    public async Task Preflight_passes_when_the_running_comfyui_offers_every_value_the_graph_sets()
    {
        using var server = new FakeComfyServer(ObjectInfo(), "{\"system\":{\"comfyui_version\":\"0.9.0\"}}");
        using var http = new HttpClient();

        await ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None);
    }

    [Fact]
    public async Task A_missing_checkpoint_is_refused_as_a_typed_component_error_with_its_folder()
    {
        using var server = new FakeComfyServer(ObjectInfo(unetOptions: new[] { "other.safetensors" }), null);
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None));

        Assert.Equal("component_missing", error.Error.Reason);
        Assert.Equal("diffusion_models", error.Error.ComfyFolder);
        Assert.Equal(UnetFile, error.Error.ExpectedFileName);
        Assert.Contains("Copy " + UnetFile, error.Error.NextAction);
        Assert.Equal(error.Message, error.Error.Message);
    }

    [Fact]
    public async Task A_missing_text_encoder_names_the_registry_repository_and_the_next_action()
    {
        using var server = new FakeComfyServer(ObjectInfo(clipOptions: Array.Empty<string>()), null);
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None));

        Assert.Equal("component_missing", error.Error.Reason);
        Assert.Equal("text_encoders", error.Error.ComfyFolder);
        Assert.Equal("qwen_3_4b.safetensors", error.Error.ExpectedFileName);
        Assert.Equal("Comfy-Org/z_image_turbo", error.Error.Repository);
        Assert.Contains("Download qwen_3_4b.safetensors from Comfy-Org/z_image_turbo", error.Error.NextAction);
    }

    [Fact]
    public async Task A_missing_node_is_refused_and_flagged_as_an_update_required_problem()
    {
        using var server = new FakeComfyServer(ObjectInfo(includeWanNode: false), null);
        using var http = new HttpClient();
        JsonObject graph = Body(("1", "Wan22ImageToVideoLatent", new JsonObject()));

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, graph, CancellationToken.None));

        Assert.Equal("node_missing", error.Error.Reason);
        Assert.True(error.Error.UpdateRequired);
        Assert.Contains("Wan22ImageToVideoLatent", error.Error.Message);
    }

    [Fact]
    public async Task A_clip_type_this_comfyui_does_not_offer_is_refused_as_update_required()
    {
        using var server = new FakeComfyServer(ObjectInfo(clipTypes: new[] { "stable_diffusion" }), null);
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None));

        Assert.Equal("value_not_offered", error.Error.Reason);
        Assert.True(error.Error.UpdateRequired);
        Assert.Contains("'lumina2'", error.Error.Message);
    }

    [Fact]
    public async Task Preflight_fails_closed_when_nothing_is_listening()
    {
        int port = FreePort();
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, new Uri("http://127.0.0.1:" + port + "/"), ZImageGraph(), CancellationToken.None));

        Assert.Equal("comfyui_unreachable", error.Error.Reason);
        Assert.Contains("No job was submitted", error.Message);
    }

    [Fact]
    public async Task Preflight_gives_up_within_the_probe_timeout_when_comfyui_is_silent()
    {
        using var server = new FakeComfyServer(ObjectInfo(), null, delayMilliseconds: 15_000);
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None));

        Assert.Equal("comfyui_unreachable", error.Error.Reason);
        Assert.Contains("did not answer", error.Message);
    }

    [Fact]
    public void Catalog_treats_only_array_first_specs_as_combo_inputs()
    {
        ComfyNodeCatalog catalog = ComfyLivePreflight.ParseCatalog(
            "{\"UNETLoader\":{\"input\":{\"required\":{\"unet_name\":[[\"a.safetensors\"],{}],\"steps\":[\"INT\",{\"default\":1}]}}}}",
            "0.9.0");

        Assert.Contains("a.safetensors", catalog.ComboOptions["UNETLoader.unet_name"]);
        Assert.False(catalog.ComboOptions.ContainsKey("UNETLoader.steps"));
        Assert.Contains("UNETLoader", catalog.NodeClasses);
        Assert.Equal("0.9.0", catalog.ComfyUiVersion);
    }

    [Fact]
    public void Links_and_numeric_values_are_never_checked_against_combo_options()
    {
        ComfyNodeCatalog catalog = new(
            new HashSet<string>(new[] { "UNETLoader", "KSampler" }, StringComparer.Ordinal),
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["UNETLoader.unet_name"] = new HashSet<string>(new[] { UnetFile }, StringComparer.Ordinal)
            },
            null);
        JsonObject graph = Body(
            ("1", "UNETLoader", new JsonObject { ["unet_name"] = UnetFile }),
            ("4", "KSampler", new JsonObject { ["seed"] = 5, ["model"] = new JsonArray("1", 0) }));

        Assert.Empty(ComfyLivePreflight.Validate(graph, catalog));
    }

    [Fact]
    public void Node_error_value_not_in_list_becomes_a_typed_missing_file()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"1\":{\"class_type\":\"UNETLoader\",\"errors\":[{\"type\":\"value_not_in_list\",\"message\":\"Value not in list\","
            + "\"details\":\"unet_name: 'missing_model.safetensors' not in (list of length 3)\"}]}}");

        IReadOnlyList<MediaDependencyError> errors = ComfyLivePreflight.MapNodeErrors(document.RootElement);

        MediaDependencyError error = Assert.Single(errors);
        Assert.Equal("component_missing", error.Reason);
        Assert.Equal("missing_model.safetensors", error.ExpectedFileName);
        Assert.Equal("diffusion_models", error.ComfyFolder);
    }

    [Fact]
    public void A_refusal_body_with_node_errors_throws_the_typed_reason()
    {
        const string body = "{\"error\":{\"type\":\"prompt_outputs_failed_validation\"},\"node_errors\":{\"1\":{\"class_type\":\"UNETLoader\","
            + "\"errors\":[{\"type\":\"value_not_in_list\",\"details\":\"unet_name: 'gone.safetensors' not in []\"}]}}}";

        MediaDependencyException error = Assert.Throws<MediaDependencyException>(() => ComfyLivePreflight.ThrowTypedRefusal(body));

        Assert.Equal("gone.safetensors", error.Error.ExpectedFileName);
    }

    [Fact]
    public void A_body_without_node_errors_does_not_throw()
    {
        ComfyLivePreflight.ThrowTypedRefusal("{\"error\":\"busy\"}");
        ComfyLivePreflight.ThrowTypedRefusal("not json at all");
    }

    [Fact]
    public async Task A_running_comfyui_older_than_the_family_minimum_is_refused_as_too_old()
    {
        using var server = new FakeComfyServer(ObjectInfo(), "{\"system\":{\"comfyui_version\":\"0.9.0\"}}");
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None, "0.37.0"));

        Assert.Equal("comfyui_too_old", error.Error.Reason);
        Assert.True(error.Error.UpdateRequired);
        Assert.Contains("0.9.0", error.Message);
        Assert.Contains("0.37.0", error.Error.NextAction);
    }

    [Theory]
    [InlineData("0.37.0")]
    [InlineData("0.40.1")]
    [InlineData("v0.38.0-dev")]
    public async Task A_running_comfyui_at_or_above_the_family_minimum_is_accepted(string runningVersion)
    {
        using var server = new FakeComfyServer(ObjectInfo(), "{\"system\":{\"comfyui_version\":\"" + runningVersion + "\"}}");
        using var http = new HttpClient();

        await ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None, "0.37.0");
    }

    [Fact]
    public async Task An_unreadable_comfyui_version_is_refused_when_a_minimum_is_declared()
    {
        using var server = new FakeComfyServer(ObjectInfo(), null);
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None, "0.37.0"));

        Assert.Equal("comfyui_too_old", error.Error.Reason);
        Assert.Contains("could not read the running ComfyUI version", error.Message);
    }

    [Fact]
    public async Task Without_a_declared_minimum_the_comfyui_version_is_not_checked()
    {
        using var server = new FakeComfyServer(ObjectInfo(), null);
        using var http = new HttpClient();

        await ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_oversized_object_info_is_refused_as_unreachable_before_it_is_parsed(bool declaredLength)
    {
        // With a declared length the size check fails first; without one, the streamed read must stop at the same cap.
        string oversized = "{\"Pad\":\"" + new string('x', ComfyLivePreflight.MaxCatalogBytes) + "\"}";
        using var server = new FakeComfyServer(oversized, null, omitContentLength: !declaredLength);
        using var http = new HttpClient();

        MediaDependencyException error = await ExpectAsync<MediaDependencyException>(() =>
            ComfyLivePreflight.EnsureSubmittableAsync(http, server.BaseAddress, ZImageGraph(), CancellationToken.None));

        Assert.Equal("comfyui_unreachable", error.Error.Reason);
        Assert.Contains("larger than the preflight limit", error.Message);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Minimal HTTP/1.1 ComfyUI fake on loopback with an ephemeral port.</summary>
    private sealed class FakeComfyServer : IDisposable
    {
        private const string PromptId = "loopback-prompt";
        private const string _saveNode = "save";
        private readonly TcpListener _listener;
        private string _objectInfo;
        private readonly string? _systemStats;
        private readonly int _delayMilliseconds;
        private readonly bool _omitContentLength;
        private readonly CancellationTokenSource _stop = new();
        private string? _submittedPrompt;
        private int _objectInfoRequests;
        private int _promptRequests;
        private int _historyRequests;

        public FakeComfyServer(string objectInfo, string? systemStats, int delayMilliseconds = 0, bool omitContentLength = false)
        {
            _objectInfo = objectInfo;
            _systemStats = systemStats;
            _delayMilliseconds = delayMilliseconds;
            _omitContentLength = omitContentLength;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseAddress = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
            _ = Task.Run(AcceptLoopAsync);
        }

        public Uri BaseAddress { get; }
        public int ObjectInfoRequests => Volatile.Read(ref _objectInfoRequests);
        public int PromptRequests => Volatile.Read(ref _promptRequests);
        public int HistoryRequests => Volatile.Read(ref _historyRequests);
        public string? SubmittedPrompt => Volatile.Read(ref _submittedPrompt);

        public void SetObjectInfo(string objectInfo) => Volatile.Write(ref _objectInfo, objectInfo);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync();
                    string[] requestParts = requestLine?.Split(' ') ?? Array.Empty<string>();
                    string method = requestParts.Length > 0 ? requestParts[0] : string.Empty;
                    string target = requestParts.Length > 1 ? requestParts[1] : "/";
                    string route = target.TrimStart('/').Split('?')[0];
                    int contentLength = 0;
                    string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                    {
                        int separator = header.IndexOf(':');
                        if (separator > 0 && header[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(header[(separator + 1)..].Trim(), out contentLength);
                    }
                    if (method == "POST" && route == "prompt" && contentLength > 0)
                    {
                        char[] requestBody = new char[contentLength];
                        int read = 0;
                        while (read < requestBody.Length)
                        {
                            int count = await reader.ReadAsync(requestBody.AsMemory(read));
                            if (count == 0) break;
                            read += count;
                        }
                        Volatile.Write(ref _submittedPrompt, new string(requestBody, 0, read));
                    }

                    int status = 404;
                    string body = "{}";
                    if (method == "GET" && route == "object_info")
                    {
                        Interlocked.Increment(ref _objectInfoRequests);
                        status = 200;
                        body = Volatile.Read(ref _objectInfo);
                    }
                    else if (method == "GET" && route == "system_stats" && _systemStats != null) { status = 200; body = _systemStats; }
                    else if (method == "POST" && route == "prompt" && _saveNode != null)
                    {
                        Interlocked.Increment(ref _promptRequests);
                        status = 200;
                        body = "{\"prompt_id\":\"" + PromptId + "\"}";
                    }
                    else if (method == "GET" && route == "history/" + PromptId && _saveNode != null)
                    {
                        Interlocked.Increment(ref _historyRequests);
                        status = 200;
                        body = JsonSerializer.Serialize(new Dictionary<string, object>
                        {
                            [PromptId] = new
                            {
                                status = new { status_str = "success", completed = true },
                                outputs = new Dictionary<string, object>
                                {
                                    [_saveNode] = new { images = new[] { new { filename = "result.png", subfolder = "test" } } }
                                }
                            }
                        });
                    }
                    if (_delayMilliseconds > 0 && route == "object_info") await Task.Delay(_delayMilliseconds);

                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    // Without a declared length the body runs to the close, so the reader's streamed cap must stop it.
                    string length = _omitContentLength ? string.Empty : "Content-Length: " + payload.Length + "\r\n";
                    string head = "HTTP/1.1 " + status + (status == 200 ? " OK" : " Not Found") + "\r\n"
                        + "Content-Type: application/json\r\n" + length + "Connection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
