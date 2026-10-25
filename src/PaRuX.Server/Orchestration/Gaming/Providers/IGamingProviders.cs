using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

public interface IGamingTriggerProvider
{
    string Id { get; }
    string Name { get; }
    bool IsDetected { get; }
    Task<bool> IsTriggeredAsync(GamingProfileDto profile, CancellationToken ct);
}

public interface IGameLauncherProvider
{
    string Id { get; }
    string Name { get; }
    bool IsInstalled { get; }
    string? ExecutablePath { get; }
    Task<bool> LaunchAsync(GamingProfileDto profile, CancellationToken ct);
    Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct);
}

public interface IInputProvider
{
    string Id { get; }
    string Name { get; }
    bool IsInstalled { get; }
    string? ExecutablePath { get; }
    Task<bool> StartAsync(GamingProfileDto profile, CancellationToken ct);
    Task<bool> StopAsync(GamingProfileDto profile, CancellationToken ct);
}

public interface IGameSuspendProvider
{
    string Id { get; }
    string Name { get; }
    bool IsAvailable { get; }
    Task<bool> SuspendProcessAsync(int processId, CancellationToken ct);
    Task<bool> ResumeProcessAsync(int processId, CancellationToken ct);
}
