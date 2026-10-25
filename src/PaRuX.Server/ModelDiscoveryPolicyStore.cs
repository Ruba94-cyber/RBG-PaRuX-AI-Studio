using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaRuX.Contracts;

namespace PaRuX.Server;

internal sealed class ModelDiscoveryPolicyStore
{
    internal const string FileName = "model-discovery-policy.json";
    internal const int MaximumDocumentBytes = 1024;

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly string _path;

    internal ModelDiscoveryPolicyStore() : this(ServerState.StateDirectory) { }

    internal ModelDiscoveryPolicyStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
    }

    internal ModelDiscoveryPolicyLoadResult Load()
    {
        lock (SyncRoot)
        {
            if (!File.Exists(_path))
                return new ModelDiscoveryPolicyLoadResult(new ModelDiscoveryPolicySettings(), null);

            try
            {
                byte[] document = ReadBoundedDocument();
                ModelDiscoveryPolicySettings? settings = JsonSerializer.Deserialize<ModelDiscoveryPolicySettings>(
                    document, JsonOptions);
                if (!ModelDiscoveryPolicy.IsValidSettings(settings))
                    return Warning("policy_file_corrupt");

                return new ModelDiscoveryPolicyLoadResult(settings!, null);
            }
            catch (PolicyFileTooLargeException)
            {
                return Warning("policy_file_too_large");
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException
                or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return Warning("policy_file_corrupt");
            }
        }
    }

    internal void Save(ModelDiscoveryPolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ModelDiscoveryPolicy.IsValidSettings(settings))
            throw new InvalidDataException("The model discovery policy is invalid.");

        byte[] document = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, JsonOptions));
        if (document.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The model discovery policy exceeds its size limit.");

        lock (SyncRoot)
        {
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(document);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private byte[] ReadBoundedDocument()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumDocumentBytes)
            throw new PolicyFileTooLargeException();

        byte[] buffer = new byte[MaximumDocumentBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0) break;
            length += read;
        }

        if (length > MaximumDocumentBytes)
            throw new PolicyFileTooLargeException();
        return buffer.AsSpan(0, length).ToArray();
    }

    private static ModelDiscoveryPolicyLoadResult Warning(string warningCode) =>
        new(new ModelDiscoveryPolicySettings(), warningCode);

    private sealed class PolicyFileTooLargeException : Exception { }
}
