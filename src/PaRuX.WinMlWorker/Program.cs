using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Windows.AI.MachineLearning;

const string TensorRtRtxProviderName = "NvTensorRtRtxExecutionProvider";
string command = args.Length == 0 ? "status" : args[0].Trim().ToLowerInvariant();

// Resident generation has a streaming JSONL protocol rather than the one-shot status document
// below. Dispatch before constructing the readiness response so stdout contains exactly one
// protocol shape for the lifetime of a serving worker.
if (command == "serve")
{
    string modelDirectory = args.Length > 1 ? args[1] : string.Empty;
    string providerName = args.Length > 2 ? args[2] : string.Empty;
    string adapterPin = args.Length > 3 ? args[3] : string.Empty;
    return await WinMlGenAiServer.RunAsync(modelDirectory, providerName, adapterPin);
}

string? requestedProviderName = args.Length > 1 ? args[1].Trim() : null;

try
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
    {
        Write(new WorkerResult
        {
            Success = false,
            Command = command,
            Error = "Windows ML optimized execution providers require Windows 11 24H2 (build 26100) or newer."
        });
        return 2;
    }

    ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
    IReadOnlyList<ExecutionProvider> providers = catalog.FindAllProviders().ToList();
    ExecutionProvider? tensorRt = FindProvider(providers, TensorRtRtxProviderName);

    switch (command)
    {
        case "status":
            Write(Status(command, providers, tensorRt));
            // A non-NVIDIA machine with a compatible Windows ML EP is a valid platform. TensorRT-RTX
            // absence must not make generic provider discovery look like a worker failure.
            return providers.Count == 0 ? 3 : 0;

        case "ensure-tensorrt-rtx":
            if (tensorRt == null)
            {
                Write(new WorkerResult
                {
                    Success = false,
                    Command = command,
                    Error = "NvTensorRtRtxExecutionProvider is not compatible with this device according to Windows ML.",
                    Providers = Providers(providers)
                });
                return 3;
            }

            ExecutionProviderReadyResult readyResult = await tensorRt.EnsureReadyAsync();
            if (readyResult.Status != ExecutionProviderReadyResultState.Success)
            {
                Write(new WorkerResult
                {
                    Success = false,
                    Command = command,
                    TensorRtRtxCompatible = true,
                    TensorRtRtxReady = false,
                    TensorRtRtxReadyState = tensorRt.ReadyState.ToString(),
                    Providers = Providers(providers),
                    Error = "Windows ML could not make NvTensorRtRtxExecutionProvider ready: "
                        + readyResult.Status + ". " + readyResult.DiagnosticText
                });
                return 4;
            }

            await catalog.RegisterCertifiedAsync();
            providers = catalog.FindAllProviders().ToList();
            tensorRt = FindProvider(providers, TensorRtRtxProviderName);
            WorkerResult ensured = Status(command, providers, tensorRt);
            if (!ensured.TensorRtRtxReady)
                ensured.Error = "Windows ML completed EnsureReadyAsync but the TensorRT-RTX provider is not in Ready state.";
            Write(ensured);
            return ensured.TensorRtRtxReady ? 0 : 5;

        case "ensure-provider":
            if (string.IsNullOrWhiteSpace(requestedProviderName))
            {
                Write(new WorkerResult
                {
                    Success = false,
                    Command = command,
                    Error = "ensure-provider requires the exact Windows ML execution-provider name.",
                    Providers = Providers(providers)
                });
                return 64;
            }

            if (BuiltInProviders.IsBuiltIn(requestedProviderName))
            {
                // CPU and DirectML are ONNX Runtime built-in execution providers: there is no Windows ML
                // catalog entry or download to make ready. They are reported only on this explicit,
                // provider-named request (never from `status`), so ordinary discovery/update surfaces and
                // AUTO routing are unchanged. The library identity is the exact onnxruntime.dll that serves.
                WorkerResult builtIn = Status(command, providers, tensorRt);
                ProviderInfo? builtInInfo = BuiltInProviders.Describe(requestedProviderName);
                builtIn.Success = builtInInfo != null;
                if (builtInInfo == null)
                    builtIn.Error = "The ONNX Runtime library that backs built-in provider " + requestedProviderName + " is missing.";
                else builtIn.Providers.Add(builtInInfo);
                Write(builtIn);
                return builtIn.Success ? 0 : 5;
            }

            ExecutionProvider? requestedProvider = FindProvider(providers, requestedProviderName);
            if (requestedProvider == null)
            {
                Write(new WorkerResult
                {
                    Success = false,
                    Command = command,
                    Error = "Windows ML provider is not compatible with this device: " + requestedProviderName + ".",
                    Providers = Providers(providers)
                });
                return 3;
            }

            ExecutionProviderReadyResult requestedReady = await requestedProvider.EnsureReadyAsync();
            if (requestedReady.Status != ExecutionProviderReadyResultState.Success)
            {
                Write(new WorkerResult
                {
                    Success = false,
                    Command = command,
                    TensorRtRtxCompatible = tensorRt != null,
                    TensorRtRtxReady = tensorRt?.ReadyState == ExecutionProviderReadyState.Ready,
                    TensorRtRtxReadyState = tensorRt?.ReadyState.ToString(),
                    Providers = Providers(providers),
                    Error = "Windows ML could not make " + requestedProvider.Name + " ready: "
                        + requestedReady.Status + ". " + requestedReady.DiagnosticText
                });
                return 4;
            }

            await catalog.RegisterCertifiedAsync();
            providers = catalog.FindAllProviders().ToList();
            tensorRt = FindProvider(providers, TensorRtRtxProviderName);
            requestedProvider = FindProvider(providers, requestedProviderName);
            WorkerResult genericEnsured = Status(command, providers, tensorRt);
            genericEnsured.Success = requestedProvider?.ReadyState == ExecutionProviderReadyState.Ready;
            if (!genericEnsured.Success)
                genericEnsured.Error = "Windows ML completed EnsureReadyAsync but " + requestedProviderName + " is not in Ready state.";
            Write(genericEnsured);
            return genericEnsured.Success ? 0 : 5;

        case "register-present":
            await catalog.RegisterCertifiedAsync();
            providers = catalog.FindAllProviders().ToList();
            tensorRt = FindProvider(providers, TensorRtRtxProviderName);
            Write(Status(command, providers, tensorRt));
            return 0;

        default:
            Write(new WorkerResult
            {
                Success = false,
                Command = command,
                Error = "Unknown command. Supported commands: status, ensure-tensorrt-rtx, ensure-provider <provider-name>, register-present, serve <model-directory> <provider-name>."
            });
            return 64;
    }
}
catch (Exception ex)
{
    Write(new WorkerResult
    {
        Success = false,
        Command = command,
        Error = ex.GetType().Name + ": " + ex.Message
    });
    return 1;
}

static ExecutionProvider? FindProvider(IEnumerable<ExecutionProvider> providers, string providerName) =>
    providers.FirstOrDefault(provider => string.Equals(provider.Name, providerName, StringComparison.OrdinalIgnoreCase));

static WorkerResult Status(string command, IReadOnlyList<ExecutionProvider> providers, ExecutionProvider? tensorRt)
{
    ProviderInfo? tensorRtInfo = tensorRt == null ? null : Provider(tensorRt);
    bool ready = tensorRt?.ReadyState == ExecutionProviderReadyState.Ready;
    return new WorkerResult
    {
        Success = providers.Count > 0,
        Command = command,
        TensorRtRtxCompatible = tensorRt != null,
        TensorRtRtxReady = ready,
        TensorRtRtxReadyState = tensorRt?.ReadyState.ToString(),
        TensorRtRtxLibraryPath = tensorRtInfo?.LibraryPath,
        TensorRtRtxLibraryFileVersion = tensorRtInfo?.LibraryFileVersion,
        TensorRtRtxLibrarySha256 = tensorRtInfo?.LibrarySha256,
        Providers = Providers(providers)
    };
}

static List<ProviderInfo> Providers(IEnumerable<ExecutionProvider> providers) => providers
    .Select(Provider)
    .OrderBy(provider => provider.Name, StringComparer.Ordinal)
    .ToList();

static ProviderInfo Provider(ExecutionProvider provider)
{
    string? path = string.IsNullOrWhiteSpace(provider.LibraryPath) ? null : provider.LibraryPath;
    string? version = null;
    string? sha256 = null;
    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
    {
        try { version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { }
        try
        {
            using FileStream stream = File.OpenRead(path);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch { }
    }
    return new ProviderInfo
    {
        Name = provider.Name,
        ReadyState = provider.ReadyState.ToString(),
        LibraryPath = path,
        LibraryFileVersion = version,
        LibrarySha256 = sha256
    };
}

static void Write(WorkerResult result)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

internal sealed class WorkerResult
{
    public bool Success { get; set; }
    public string Command { get; set; } = string.Empty;
    public bool TensorRtRtxCompatible { get; set; }
    public bool TensorRtRtxReady { get; set; }
    public string? TensorRtRtxReadyState { get; set; }
    public string? TensorRtRtxLibraryPath { get; set; }
    public string? TensorRtRtxLibraryFileVersion { get; set; }
    public string? TensorRtRtxLibrarySha256 { get; set; }
    public List<ProviderInfo> Providers { get; set; } = new();
    public string? Error { get; set; }
}

internal sealed class ProviderInfo
{
    public string Name { get; set; } = string.Empty;
    public string ReadyState { get; set; } = string.Empty;
    public string? LibraryPath { get; set; }
    public string? LibraryFileVersion { get; set; }
    public string? LibrarySha256 { get; set; }
}

internal static class BuiltInProviders
{
    internal static bool IsBuiltIn(string name) =>
        string.Equals(name, "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "DmlExecutionProvider", StringComparison.OrdinalIgnoreCase);

    internal static ProviderInfo? Describe(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
        if (!File.Exists(path)) return null;
        string? version = null;
        string? sha256 = null;
        try { version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { }
        try
        {
            using FileStream stream = File.OpenRead(path);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch { }
        return new ProviderInfo
        {
            Name = name.Equals("DmlExecutionProvider", StringComparison.OrdinalIgnoreCase)
                ? "DmlExecutionProvider" : "CPUExecutionProvider",
            ReadyState = "Ready",
            LibraryPath = path,
            LibraryFileVersion = version,
            LibrarySha256 = sha256
        };
    }
}
