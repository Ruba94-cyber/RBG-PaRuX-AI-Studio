using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// The GameStart/GameStop runtime actions flip the persisted game marker, but the cancellation token that refuses
/// in-flight and new AI requests used to be reset only by the Gaming guard's next poll. The first request after
/// GameStop therefore got "Gaming Mode activated" with 409 although status already said Gaming was off.
/// </summary>
public sealed class GamingActionTokenTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "parux-game-act-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _root;
    private readonly IDisposable _state;

    public GamingActionTokenTests()
    {
        _root = LocalAiPaths.UseRootForTests(Path.Combine(_work, "localai"));
        _state = LocalAiPaths.UseStateDirForTests(Path.Combine(_work, "state"));
    }

    public void Dispose()
    {
        _state.Dispose();
        _root.Dispose();
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    [Fact]
    public async Task GameStop_action_lifts_the_gaming_cancellation_immediately_and_GameStart_raises_it()
    {
        var runtime = new LocalRuntimeService(new Env(), new LocalAiProcessManager(0), new ExternalTextProviderService());

        LocalRuntimeService.RuntimeResult start = await runtime.RunActionAsync("GameStart", CancellationToken.None);
        Assert.True(start.Success, start.Message);
        Assert.True(runtime.GamingCancellationToken.IsCancellationRequested);

        LocalRuntimeService.RuntimeResult stop = await runtime.RunActionAsync("GameStop", CancellationToken.None);
        Assert.True(stop.Success, stop.Message);
        Assert.False(runtime.GamingCancellationToken.IsCancellationRequested);
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PaRuX.Server.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
