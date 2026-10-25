using System.Text.Json;

if (args.Length is not (3 or 4) || !string.Equals(args[0], "serve", StringComparison.Ordinal))
    return 2;

// Test-only knobs live beside the fixture model so parallel tests cannot interfere.
bool noThinkingControl = File.Exists(Path.Combine(args[1], "stub-no-thinking"));
bool omitAdapterEvidence = File.Exists(Path.Combine(args[1], "stub-no-adapter-evidence"));
string? pin = args.Length == 4 ? args[3] : null;
string[] devices = pin == null
    ? new[] { "test-only-device" }
    : new[] { "DmlExecutionProvider|GPU|test|" + pin.Split(':')[0] + "|" + pin.Split(':')[1] };

const int workerApiVersion = 4;
const string providerCancellationControl = "request-id-cancel-drain";

WriteEvent(new
{
    apiVersion = workerApiVersion,
    type = "ready",
    provider = args[2],
    modelPath = args[1],
    providerLibraryPath = "test-only-worker-stub",
    thinkingControl = noThinkingControl ? "not-exposed-by-template" : "jinja-enable-thinking",
    cancellationControl = providerCancellationControl,
    promptCountingControl = "exact-rendered-prompt-count",
    deviceEvidence = "single-compatible-ep-device",
    pinnedAdapter = pin,
    adapterEvidence = pin == null || omitAdapterEvidence ? null : "process-video-memory " + pin + "=2048MB",
    compatibleDevices = devices
});

string? pendingCancellationId = null;
while (Console.ReadLine() is { } line)
{
    using JsonDocument document = JsonDocument.Parse(line);
    JsonElement command = document.RootElement;
    string? operation = command.TryGetProperty("op", out JsonElement opValue) ? opValue.GetString() : null;
    string? id = command.TryGetProperty("id", out JsonElement idValue) ? idValue.GetString() : null;
    if (string.IsNullOrWhiteSpace(id)) return 3;

    if (string.Equals(operation, "cancel", StringComparison.Ordinal))
    {
        if (!string.Equals(id, pendingCancellationId, StringComparison.Ordinal)) return 4;
        WriteEvent(new { apiVersion = workerApiVersion, id, type = "cancelled" });
        pendingCancellationId = null;
        continue;
    }

    if (!string.Equals(operation, "generate", StringComparison.Ordinal)) return 5;
    JsonElement messages = command.GetProperty("messages");
    string prompt = messages[0].GetProperty("content").GetString() ?? string.Empty;
    if (prompt.StartsWith("cancel-", StringComparison.Ordinal))
    {
        pendingCancellationId = id;
        WriteEvent(new { apiVersion = workerApiVersion, id, type = "delta", content = "before-cancel:" + prompt });
        continue;
    }

    if (string.Equals(prompt, "crash", StringComparison.Ordinal)) return 73;

    WriteEvent(new { apiVersion = workerApiVersion, id, type = "delta", content = "result:" + prompt });
    WriteEvent(new { apiVersion = workerApiVersion, id, type = "done" });
}

return 0;

static void WriteEvent<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
