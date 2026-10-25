using Microsoft.Extensions.Hosting;

namespace PaRuX.Server.ProjectManager;

/// <summary>
/// Reconciles tasks left in the repository-provisioning state by an interrupted server process
/// before the HTTP server begins accepting requests.
/// </summary>
public sealed class ProjectProvisioningRecoveryHostedService : IHostedService
{
    private readonly ProjectManagerService _projects;

    public ProjectProvisioningRecoveryHostedService(ProjectManagerService projects) => _projects = projects;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _projects.RecoverInterruptedRepositoryProvisioning();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
