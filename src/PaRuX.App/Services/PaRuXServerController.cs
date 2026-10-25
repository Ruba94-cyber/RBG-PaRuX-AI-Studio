using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>Starts and authenticates the per-user PaRuX Server process.</summary>
    public sealed class PaRuXServerController
    {
        private static readonly HttpClient Http = CreateControlPlaneClient(TimeSpan.FromSeconds(2));

        internal static HttpClient CreateControlPlaneClient(TimeSpan timeout) =>
            PaRuXControlPlaneHttpClient.CreateNoRedirectClient(timeout);
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions(JsonSerializerDefaults.Web);

        public static PaRuXServerController Current { get; } = new PaRuXServerController();

        private readonly SemaphoreSlim _startGate = new SemaphoreSlim(1, 1);
        private ServerBootstrap? _bootstrap;

        private static string BootstrapPath => Path.Combine(AppPaths.PaRuXRoot, "server", "bootstrap.json");

        private PaRuXServerController() { }

        /// <param name="progress">Optional status sink for a caller that shows startup progress.
        /// Null is the ordinary case - 45 call sites across the client layer pass it - so it is
        /// annotated as such rather than leaving every one of them a warning.</param>
        public async Task<ServerBootstrap> EnsureRunningAsync(IProgress<string?>? progress, CancellationToken ct)
        {
            await _startGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ServerBootstrap? current = ReadBootstrap();
                ServerProbeResult probe = await ProbeServerAsync(current, ct).ConfigureAwait(false);
                if (probe == ServerProbeResult.AuthenticationRejected)
                    throw BootstrapAuthenticationException(current!);
                if (probe == ServerProbeResult.EndpointUnverified)
                    throw BootstrapEndpointException(current!);
                if (probe == ServerProbeResult.ProcessIdentityMismatch)
                    throw BootstrapProcessException(current!);
                if (probe == ServerProbeResult.Healthy)
                {
                    _bootstrap = current;
                    return current!;
                }

                if (progress != null) progress.Report("Starting PaRuX Server…");
                StartServerProcess();

                DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    current = ReadBootstrap();
                    probe = await ProbeServerAsync(current, ct).ConfigureAwait(false);
                    if (probe == ServerProbeResult.AuthenticationRejected)
                        throw BootstrapAuthenticationException(current!);
                    if (probe == ServerProbeResult.EndpointUnverified)
                        throw BootstrapEndpointException(current!);
                    if (probe == ServerProbeResult.ProcessIdentityMismatch)
                        throw BootstrapProcessException(current!);
                    if (probe == ServerProbeResult.Healthy)
                    {
                        _bootstrap = current;
                        if (progress != null) progress.Report(null);
                        return current!;
                    }
                }

                Log.Write("PaRuX Server startup timed out. Bootstrap="
                    + (current == null ? "missing" : current.Endpoint + " pid=" + current.ProcessId));
                throw new LocalAiException("PaRuX Server did not start. Check the local PaRuX logs and try again.");
            }
            finally
            {
                _startGate.Release();
            }
        }

        public ServerBootstrap GetCurrentBootstrap()
        {
            ServerBootstrap? value = _bootstrap ?? ReadBootstrap();
            if (value == null || string.IsNullOrWhiteSpace(value.Token)
                || !TryParseLocalServerEndpoint(value.Endpoint, out _))
                throw new LocalAiException("PaRuX Server is not available at a valid local endpoint.");
            return value;
        }

        public async Task<ServerBootstrap> RestartAsync(IProgress<string?>? progress, CancellationToken ct)
        {
            await _startGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ServerBootstrap? current = ReadBootstrap();
                if (current != null)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(current.ProcessId))
                        {
                            string expected = Path.GetFullPath(FindServerExecutable());
                            // MainModule can be null - for a process that exited between the
                            // lookup above and here, or one this session cannot inspect. Without
                            // this the identity check still failed closed, but as a bare
                            // NullReferenceException: the refusal was correct and the reason for
                            // it was unreadable. PaRuX is about to kill a process; when it cannot
                            // prove which one, it should say exactly that.
                            string? actualPath = process.MainModule?.FileName;
                            if (actualPath == null)
                                throw new LocalAiException(
                                    "Could not confirm which executable PID " + current.ProcessId
                                    + " is running, so it was not stopped.");
                            string actual = Path.GetFullPath(actualPath);
                            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                                throw new LocalAiException("Refused to stop an unexpected process while restarting PaRuX Server.");
                            process.Kill(true);
                            await process.WaitForExitAsync(ct).ConfigureAwait(false);
                        }
                    }
                    catch (ArgumentException) { }
                }
                _bootstrap = null;
                try { if (File.Exists(BootstrapPath)) File.Delete(BootstrapPath); } catch { }
            }
            finally
            {
                _startGate.Release();
            }

            return await EnsureRunningAsync(progress, ct).ConfigureAwait(false);
        }

        private static ServerBootstrap? ReadBootstrap()
        {
            try
            {
                if (!File.Exists(BootstrapPath)) return null;
                // The Core promotes a freshly written bootstrap with an atomic rename. Allow that
                // rename even when this short-lived read overlaps the ApplicationStarted callback.
                using (var stream = new FileStream(
                    BootstrapPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                    return JsonSerializer.Deserialize<ServerBootstrap>(stream, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private enum ServerProbeResult
        {
            Unavailable,
            Healthy,
            AuthenticationRejected,
            EndpointUnverified,
            ProcessIdentityMismatch
        }

        private static LocalAiException BootstrapAuthenticationException(ServerBootstrap bootstrap) =>
            new LocalAiException(
                "PaRuX Server at " + bootstrap.Endpoint + " rejected the saved local credentials (HTTP 401/403). "
                + "The bootstrap may belong to another PaRuX data folder. PaRuX left that server running; "
                + "restart the matching PaRuX Server, then retry.");

        private static LocalAiException BootstrapEndpointException(ServerBootstrap bootstrap) =>
            new LocalAiException(
                "PaRuX Server at " + bootstrap.Endpoint + " responded, but its protected identity endpoint "
                + "could not be verified. PaRuX left that server running; inspect its logs and retry.");

        private static LocalAiException BootstrapProcessException(ServerBootstrap bootstrap) =>
            new LocalAiException(
                "Bootstrap PID " + bootstrap.ProcessId + " at " + bootstrap.Endpoint
                + " is not the matching PaRuX.Server executable. PaRuX left that server "
                + "running; restart the matching PaRuX Server, then retry.");

        private static async Task<ServerProbeResult> ProbeServerAsync(ServerBootstrap? bootstrap, CancellationToken ct)
        {
            if (bootstrap == null || string.IsNullOrWhiteSpace(bootstrap.Endpoint)
                || string.IsNullOrWhiteSpace(bootstrap.Token)) return ServerProbeResult.Unavailable;
            if (!TryParseLocalServerEndpoint(bootstrap.Endpoint, out Uri? endpoint) || endpoint == null)
                return ServerProbeResult.Unavailable;
            // Bootstrap credentials are scoped to the exact PaRuX.Server executable. Do not
            // disclose them to an arbitrary loopback listener before establishing that identity.
            ServerProcessIdentity processIdentity = InspectServerProcess(bootstrap.ProcessId);
            if (processIdentity == ServerProcessIdentity.Missing)
                return ServerProbeResult.Unavailable;
            if (processIdentity != ServerProcessIdentity.Expected)
                return ServerProbeResult.ProcessIdentityMismatch;
            try
            {
                string baseAddress = endpoint.GetLeftPart(UriPartial.Authority);
                using var authRequest = new HttpRequestMessage(HttpMethod.Get, baseAddress + "/api/v1/system/data-root");
                authRequest.Headers.Add("X-PaRuX-Token", bootstrap.Token);
                using HttpResponseMessage authResponse = await Http.SendAsync(authRequest, ct).ConfigureAwait(false);
                if (authResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    || authResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return ServerProbeResult.AuthenticationRejected;
                if (!authResponse.IsSuccessStatusCode) return ServerProbeResult.EndpointUnverified;

                processIdentity = InspectServerProcess(bootstrap.ProcessId);
                return processIdentity switch
                {
                    ServerProcessIdentity.Expected => ServerProbeResult.Healthy,
                    ServerProcessIdentity.Missing => ServerProbeResult.Unavailable,
                    _ => ServerProbeResult.ProcessIdentityMismatch
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return ServerProbeResult.Unavailable;
            }
        }

        private static bool TryParseLocalServerEndpoint(string? value, out Uri? endpoint)
            => PaRuXControlPlaneHttpClient.TryParseLocalEndpoint(value, out endpoint);

        private enum ServerProcessIdentity
        {
            Missing,
            Expected,
            Unexpected
        }

        private static ServerProcessIdentity InspectServerProcess(int processId)
        {
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return ServerProcessIdentity.Missing; }

            using (process)
            try
            {
                if (process.HasExited) return ServerProcessIdentity.Missing;
                string? actualPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(actualPath)) return ServerProcessIdentity.Unexpected;
                string expectedPath = Path.GetFullPath(FindServerExecutable());
                return string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase)
                    ? ServerProcessIdentity.Expected
                    : ServerProcessIdentity.Unexpected;
            }
            catch (InvalidOperationException) when (HasExited(process))
            {
                // The process can exit between GetProcessById and reading MainModule. Treat that
                // as a stale bootstrap so the normal startup path can replace it.
                return ServerProcessIdentity.Missing;
            }
            catch { return ServerProcessIdentity.Unexpected; }
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        private static void StartServerProcess()
        {
            string executable = FindServerExecutable();
            var info = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
            Process? process = Process.Start(info);
            if (process == null) throw new LocalAiException("Windows could not start PaRuX Server.");
            ChildProcessJob.Assign(process);
        }

        private static string FindServerExecutable()
        {
            // PARUX_SERVER_PATH is the current branded override; PARUX_CORE_PATH is retained as a
            // compatibility fallback for existing dev machines.
            string? configured = Environment.GetEnvironmentVariable("PARUX_SERVER_PATH");
            if (string.IsNullOrWhiteSpace(configured))
                configured = Environment.GetEnvironmentVariable("PARUX_CORE_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            string besideApp = Path.Combine(AppContext.BaseDirectory, "PaRuX.Server", "PaRuX.Server.exe");
            if (File.Exists(besideApp)) return besideApp;

            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; depth < 9 && directory != null; depth++, directory = directory.Parent)
            {
                if (!directory.Name.Equals("src", StringComparison.OrdinalIgnoreCase)) continue;
                // A self-contained Release publish still sits below src during local release smoke.
                // Prefer its matching Release Core so the packaged UI cannot silently validate
                // against an older Debug binary left by a previous development run.
                string release = Path.Combine(directory.FullName, "PaRuX.Server", "bin", "Release", "net8.0", "PaRuX.Server.exe");
                if (File.Exists(release)) return release;
                string debug = Path.Combine(directory.FullName, "PaRuX.Server", "bin", "Debug", "net8.0", "PaRuX.Server.exe");
                if (File.Exists(debug)) return debug;
            }

            throw new LocalAiException(
                "PaRuX Server is missing from this installation. Repair or reinstall PaRuX.");
        }
    }
}
