using System.Security.Cryptography;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server;

public sealed class ServerState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string DefaultEndpoint = "http://127.0.0.1:51123";

    public string Endpoint { get; } = DefaultEndpoint;
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string StateDirectory => Path.Combine(
        PaRuX.Contracts.PaRuXDataRoot.Path, "server");

    public static string BootstrapPath => Path.Combine(StateDirectory, "bootstrap.json");

    public void Publish()
    {
        Directory.CreateDirectory(StateDirectory);
        var bootstrap = new ServerBootstrap
        {
            Endpoint = Endpoint,
            Token = Token,
            ProcessId = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow
        };

        PublishAtomically(BootstrapPath, bootstrap);
        // Remove the pre-1.5.1 fixed-name staging file if a prior interrupted promotion left it.
        try { File.Delete(BootstrapPath + ".tmp"); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void PublishAtomically(string destinationPath, ServerBootstrap bootstrap)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = destinationPath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(bootstrap, JsonOptions));

        // Desktop polls the previous bootstrap while Core starts. Antivirus/indexers can also
        // hold a transient read handle. Promotion must remain atomic, but a single sharing
        // violation must not leave a healthy Core paired with stale credentials/PID forever.
        const int attempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(destinationPath))
                    File.Replace(temporary, destinationPath, destinationBackupFileName: null);
                else
                    File.Move(temporary, destinationPath);
                break;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
        }
    }

    public void RemoveIfOwned()
    {
        try
        {
            if (!File.Exists(BootstrapPath)) return;
            ServerBootstrap? value = JsonSerializer.Deserialize<ServerBootstrap>(File.ReadAllText(BootstrapPath));
            if (value?.ProcessId == Environment.ProcessId) File.Delete(BootstrapPath);
        }
        catch
        {
            // A stale bootstrap is harmless and will be replaced on the next start.
        }
    }
}
