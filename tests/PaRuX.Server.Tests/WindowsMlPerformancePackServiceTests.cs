using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class WindowsMlPerformancePackServiceTests
{
    [Fact]
    public void Worker_json_is_parsed_without_exposing_transport_details()
    {
        string json = "{\"success\":true,\"command\":\"status\",\"tensorRtRtxCompatible\":true,"
            + "\"tensorRtRtxReady\":true,\"tensorRtRtxReadyState\":\"Ready\",\"providers\":["
            + "{\"name\":\"NvTensorRtRtxExecutionProvider\",\"readyState\":\"Ready\",\"libraryPath\":\"C:\\\\ep\\\\trt.dll\"}]}";

        WindowsMlPerformancePackStatus status = WindowsMlPerformancePackService.ParseWorkerOutput(json);

        Assert.True(status.WorkerAvailable);
        Assert.True(status.SupportedOs);
        Assert.True(status.TensorRtRtxCompatible);
        Assert.True(status.TensorRtRtxReady);
        Assert.Equal("Ready", status.TensorRtRtxReadyState);
        Assert.Contains(status.Providers, provider => provider.Name == "NvTensorRtRtxExecutionProvider");
    }

    [Fact]
    public void Provider_lookup_is_case_insensitive_and_does_not_require_tensorrt()
    {
        string json = "{\"success\":true,\"command\":\"status\",\"tensorRtRtxCompatible\":false,"
            + "\"tensorRtRtxReady\":false,\"providers\":["
            + "{\"name\":\"ExampleNpuExecutionProvider\",\"readyState\":\"Ready\",\"librarySha256\":"
            + "\"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\"}]}";

        WindowsMlPerformancePackStatus status = WindowsMlPerformancePackService.ParseWorkerOutput(json);
        WindowsMlProviderInfo? provider = status.FindProvider("examplenpuexecutionprovider");

        Assert.False(status.TensorRtRtxCompatible);
        Assert.NotNull(provider);
        Assert.Equal("Ready", provider!.ReadyState);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", provider.LibrarySha256);
    }

    [Fact]
    public void Missing_provider_lookup_fails_closed()
    {
        string json = "{\"success\":true,\"command\":\"status\",\"providers\":["
            + "{\"name\":\"ExampleGpuExecutionProvider\",\"readyState\":\"Ready\"}]}";

        WindowsMlPerformancePackStatus status = WindowsMlPerformancePackService.ParseWorkerOutput(json);

        Assert.Null(status.FindProvider("missing-provider"));
        Assert.Null(status.FindProvider(" "));
    }

    [Fact]
    public void Last_json_line_wins_after_non_json_diagnostics()
    {
        string output = "diagnostic line\n{\"success\":false,\"tensorRtRtxCompatible\":false,"
            + "\"tensorRtRtxReady\":false,\"error\":\"not compatible\"}\n";

        WindowsMlPerformancePackStatus status = WindowsMlPerformancePackService.ParseWorkerOutput(output);

        Assert.False(status.TensorRtRtxCompatible);
        Assert.Equal("not compatible", status.Error);
    }

    [Fact]
    public void Empty_or_malformed_worker_output_fails_closed()
    {
        Assert.Throws<InvalidDataException>(() => WindowsMlPerformancePackService.ParseWorkerOutput(""));
        Assert.ThrowsAny<Exception>(() => WindowsMlPerformancePackService.ParseWorkerOutput("not-json"));
    }
}
