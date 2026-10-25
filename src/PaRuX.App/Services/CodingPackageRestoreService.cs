using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    public sealed record CodingPackageRestoreResult(
        bool Success,
        string Ecosystem,
        string RelativeRoot,
        string CommandDescription,
        int ExitCode,
        string Output,
        string? Error = null);

    /// <summary>
    /// Executes only PaRuX-discovered dependency restore commands. The model selects an ecosystem and
    /// direct repository alias, never arbitrary command text. Both a task capability and PaRuX's
    /// global Internet switch must authorize the operation. The command still runs in the same
    /// repository sandbox state with scrubbed credentials; only the separate Windows InternetClient
    /// AppContainer identity is used for this operation. Internet policy is rechecked while the
    /// restore is running so turning the global switch OFF revokes the operation by cancelling the
    /// sandbox Job Object rather than merely affecting the next command. The active Core Execution
    /// Profile supplies the maximum network-restore duration; there is no independent fixed timeout.
    /// </summary>
    public static class CodingPackageRestoreService
    {
        private static readonly TimeSpan InternetPolicyRecheckInterval = TimeSpan.FromMilliseconds(750);

        public static async Task<CodingPackageRestoreResult> RestoreAsync(
            string workspaceRoot,
            string orchestratorTaskId,
            string? relativeRoot,
            string? ecosystem,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(orchestratorTaskId))
                return Failed("", relativeRoot, "Package restore requires a durable orchestrator coding task with explicit capability grants.");

            AgentTaskDto task;
            try { task = await AgentOrchestratorClient.Current.GetAsync(orchestratorTaskId, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(ecosystem, relativeRoot, "Could not read task permission grants from PaRuX Core: " + ex.Message);
            }

            bool anyRestoreGrant = task.GrantedCapabilities.Contains(
                CodingPackageRestoreDiscovery.SafeRestoreCapability, StringComparer.OrdinalIgnoreCase)
                || task.GrantedCapabilities.Contains(
                    CodingPackageRestoreDiscovery.ScriptedRestoreCapability, StringComparer.OrdinalIgnoreCase);
            if (!anyRestoreGrant)
                return Failed(ecosystem, relativeRoot,
                    "Package restore is not granted for this task. Grant coding.package-restore (no project scripts) or the stronger coding.package-restore.scripts authority before execution.");

            try
            {
                var internet = await InternetClient.Current.GetStatusAsync(ct).ConfigureAwait(false);
                if (!internet.Enabled)
                    return Failed(ecosystem, relativeRoot,
                        "PaRuX Internet is OFF. Package restore will not enable network access behind the user's global policy.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Failed(ecosystem, relativeRoot, "Could not read PaRuX Internet policy: " + ex.Message);
            }

            int maxRestoreMinutes;
            try
            {
                ExecutionProfileStatus profile = await ExecutionProfileClient.Current.GetAsync(null, ct).ConfigureAwait(false);
                maxRestoreMinutes = ResolveNetworkRestoreMinutes(profile.Effective);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Core already had to be reachable to read the task. A profile read failure here is
                // therefore an integrity/configuration failure; falling back to a hardcoded longer
                // network window would silently widen authority.
                return Failed(ecosystem, relativeRoot, "Could not resolve the active Execution Profile network budget: " + ex.Message);
            }

            string target;
            try { target = CodingPackageRestoreDiscovery.NormalizeRelativeRoot(relativeRoot); }
            catch (InvalidDataException ex) { return Failed(ecosystem, relativeRoot, ex.Message); }

            IReadOnlyList<CodingPackageRestoreDiscovery.RestoreCommand> allowed;
            try
            {
                allowed = CodingPackageRestoreDiscovery.AllowedFor(
                    workspaceRoot, target, task.GrantedCapabilities);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Failed(ecosystem, target, "Could not discover a safe restore command: " + ex.Message);
            }

            string requestedEcosystem = (ecosystem ?? string.Empty).Trim().ToLowerInvariant();
            List<CodingPackageRestoreDiscovery.RestoreCommand> matching = allowed
                .Where(candidate => requestedEcosystem.Length == 0
                    || string.Equals(candidate.Ecosystem, requestedEcosystem, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count == 0)
            {
                string discovered = string.Join(", ", allowed.Select(item => item.Ecosystem).Distinct(StringComparer.OrdinalIgnoreCase));
                return Failed(requestedEcosystem, target,
                    allowed.Count == 0
                        ? "No package restore operation for this repository is both discoverable and granted by the task's permission profile."
                        : "Requested ecosystem is not an allowed restore target here. Granted/discovered ecosystem(s): " + discovered + ".");
            }
            if (matching.Count > 1)
            {
                string options = string.Join(", ", matching.Select(item => item.Ecosystem).Distinct(StringComparer.OrdinalIgnoreCase));
                return Failed(requestedEcosystem, target,
                    "More than one restore ecosystem is available for this repository. Select exactly one ecosystem: " + options + ".");
            }

            CodingPackageRestoreDiscovery.RestoreCommand selected = matching[0];
            string exactCommand = ResolveManagedToolCommand(selected);
            if (exactCommand.Length == 0)
                return Failed(selected.Ecosystem, target,
                    "The required PaRuX-managed toolchain is not installed. PaRuX refused to fall back to a system executable.");

            string executionCommand = target == "."
                ? exactCommand
                : "pushd \"" + target + "\" && " + exactCommand;

            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task policyWatcher = WatchInternetPolicyAsync(operationCts);
            WindowsCodingSandbox.Result sandbox;
            try
            {
                sandbox = await WindowsCodingSandbox.RunAsync(
                    workspaceRoot,
                    executionCommand,
                    operationCts.Token,
                    timeout: TimeSpan.FromMinutes(maxRestoreMinutes),
                    networkMode: WindowsCodingSandbox.NetworkMode.InternetClient).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failed(selected.Ecosystem, target,
                    "Package restore was stopped because PaRuX Internet was disabled or its policy could no longer be confirmed. The sandbox process tree was terminated.");
            }
            finally
            {
                operationCts.Cancel();
                try { await policyWatcher.ConfigureAwait(false); }
                catch (OperationCanceledException) when (operationCts.IsCancellationRequested) { }
            }

            string output = sandbox.Error == null
                ? sandbox.Output
                : sandbox.Error + (string.IsNullOrWhiteSpace(sandbox.Output) ? string.Empty : "\n" + sandbox.Output);
            return new CodingPackageRestoreResult(
                sandbox.ExitCode == 0 && sandbox.Error == null,
                selected.Ecosystem,
                selected.RelativeRoot,
                selected.Command,
                sandbox.ExitCode,
                output,
                sandbox.ExitCode == 0 && sandbox.Error == null ? null : sandbox.Error ?? "Package restore exited with code " + sandbox.ExitCode + ".");
        }

        internal static int ResolveNetworkRestoreMinutes(ModelExecutionProfile? profile) =>
            Math.Clamp(profile?.Budget?.MaxNetworkRestoreMinutes ?? 15, 1, 60);

        private static async Task WatchInternetPolicyAsync(CancellationTokenSource operationCts)
        {
            while (!operationCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(InternetPolicyRecheckInterval, operationCts.Token).ConfigureAwait(false);
                    InternetStatus status = await InternetClient.Current.GetStatusAsync(operationCts.Token).ConfigureAwait(false);
                    if (!status.Enabled)
                    {
                        operationCts.Cancel();
                        return;
                    }
                }
                catch (OperationCanceledException) when (operationCts.IsCancellationRequested) { return; }
                catch
                {
                    // A network-enabled model-requested operation must fail closed if the local Core
                    // can no longer confirm that Internet remains enabled.
                    try { operationCts.Cancel(); } catch { }
                    return;
                }
            }
        }

        internal static string ResolveManagedToolCommand(CodingPackageRestoreDiscovery.RestoreCommand selected)
        {
            if (!string.Equals(selected.Ecosystem, "uv", StringComparison.OrdinalIgnoreCase))
                return selected.Command;

            string managedUv = Path.Combine(
                PaRuXDataRoot.Path, "ai-runtimes", "toolchain", "uv", "uv.exe");
            if (!File.Exists(managedUv)) return string.Empty;
            const string prefix = "uv ";
            if (!selected.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            return "\"" + managedUv.Replace("\"", string.Empty) + "\" " + selected.Command[prefix.Length..];
        }

        private static CodingPackageRestoreResult Failed(string? ecosystem, string? root, string error) =>
            new(false, ecosystem ?? string.Empty, string.IsNullOrWhiteSpace(root) ? "." : root!, string.Empty, -1, string.Empty, error);
    }
}
