using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;
using Microsoft.Windows.AI.MachineLearning;

internal static class WinMlGenAiServer
{
    internal const int ApiVersion = 4;
    private const int MaximumMessages = 256;
    private const int MaximumPromptCharacters = 1_000_000;
    private const int MaximumOutputTokens = 32_768;
    private const string ThinkingControlJinja = "jinja-enable-thinking";
    private const string RequestCancellationControl = "request-id-cancel-drain";
    private const string PromptCountingControl = "exact-rendered-prompt-count";
    private const string SingleCompatibleDeviceEvidence = "single-compatible-ep-device";
    private static readonly object OutputGate = new();
    private static readonly IReadOnlyDictionary<string, string> GenAiProviderNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NvTensorRtRtxExecutionProvider"] = "NvTensorRtRtx",
            ["VitisAIExecutionProvider"] = "VitisAI",
            ["OpenVINOExecutionProvider"] = "OpenVINO",
            ["QNNExecutionProvider"] = "QNN",
            // ONNX Runtime built-in providers (no Windows ML catalog entry): explicit evidence/manual use only.
            ["CPUExecutionProvider"] = "cpu",
            ["DmlExecutionProvider"] = "dml"
        };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> RunAsync(string modelDirectory, string providerName, string adapterPin = "")
    {
        string requestId = string.Empty;
        CancellationTokenSource? activeCancellation = null;
        Task? activeGeneration = null;
        string? activeRequestId = null;
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
                throw new PlatformNotSupportedException("Windows ML GenAI serving requires Windows 11 24H2 (build 26100) or newer.");

            string modelPath = Path.GetFullPath(modelDirectory ?? string.Empty);
            ValidateModelLayout(modelPath);
            providerName = RequireProviderName(providerName);
            string genAiProviderName = ToGenAiProviderName(providerName);
            // DirectML enumerates every adapter; it is served only on one exact PCI vendor:device.
            uint pinVendor = 0, pinDevice = 0;
            bool pinned = !string.IsNullOrWhiteSpace(adapterPin);
            bool directMl = string.Equals(providerName, "DmlExecutionProvider", StringComparison.OrdinalIgnoreCase);
            if (pinned && !TryParseAdapterPin(adapterPin, out pinVendor, out pinDevice))
                throw new InvalidDataException("The adapter pin must look like vvvv:dddd (hex PCI vendor:device).");
            if (pinned && !directMl)
                throw new InvalidDataException("Only the DirectML provider accepts an adapter pin.");
            if (directMl && !pinned)
                throw new InvalidDataException("DirectML requires an explicit adapter pin; refusing to choose an adapter implicitly.");

            string effectiveProviderName;
            string effectiveProviderLibrary;
            if (BuiltInProviders.IsBuiltIn(providerName))
            {
                ProviderInfo builtIn = BuiltInProviders.Describe(providerName)
                    ?? throw new FileNotFoundException("The ONNX Runtime library for built-in provider " + providerName + " is unavailable.");
                effectiveProviderName = builtIn.Name;
                effectiveProviderLibrary = builtIn.LibraryPath!;
            }
            else
            {
                ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
                ExecutionProvider provider = catalog.FindAllProviders().FirstOrDefault(item =>
                    string.Equals(item.Name, providerName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        "Windows ML does not report the requested execution provider as compatible with this PC: " + providerName + ".");

                if (provider.ReadyState != ExecutionProviderReadyState.Ready)
                {
                    ExecutionProviderReadyResult ready = await provider.EnsureReadyAsync();
                    if (ready.Status != ExecutionProviderReadyResultState.Success)
                        throw new InvalidOperationException(
                            "Windows ML could not make " + provider.Name + " ready: " + ready.Status + ". " + ready.DiagnosticText);
                    provider = catalog.FindAllProviders().FirstOrDefault(item =>
                        string.Equals(item.Name, providerName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("Windows ML provider disappeared after EnsureReadyAsync: " + providerName + ".");
                }
                if (provider.ReadyState != ExecutionProviderReadyState.Ready)
                    throw new InvalidOperationException("Windows ML provider did not reach Ready state: " + provider.Name + ".");
                if (string.IsNullOrWhiteSpace(provider.LibraryPath) || !File.Exists(provider.LibraryPath))
                    throw new FileNotFoundException("Windows ML provider library is unavailable after readiness.", provider.LibraryPath);

                OrtEnv.Instance().RegisterExecutionProviderLibrary(provider.Name, provider.LibraryPath);
                effectiveProviderName = provider.Name;
                effectiveProviderLibrary = provider.LibraryPath;
            }
            string[] compatibleDevices = OrtEnv.Instance().GetEpDevices()
                .Where(device => (!pinned || (device.HardwareDevice.VendorId == pinVendor && device.HardwareDevice.DeviceId == pinDevice))
                    && string.Equals(device.EpName, effectiveProviderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.EpName, genAiProviderName, StringComparison.OrdinalIgnoreCase))
                .Select(DescribeDevice)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            using var oga = new OgaHandle();
            using var config = new Config(modelPath);
            config.ClearProviders();
            config.AppendProvider(genAiProviderName);
            if (pinned)
            {
                // The ORT DirectML provider addresses adapters by DXGI index. Resolve the PCI pin to
                // exactly one non-software adapter now (every spawn) and fail closed otherwise.
                int dxgiIndex = DxgiAdapterIndex.Resolve(pinVendor, pinDevice);
                config.SetProviderOption(genAiProviderName, "device_id", dxgiIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)); // best effort; execution is verified below
            }
            using var model = new Model(config);
            using var tokenizer = new Tokenizer(model);
            string? adapterEvidence = pinned ? DxgiAdapterIndex.VerifyExecution(pinVendor, pinDevice, WeightBytes(modelPath)) : null;
            string chatTemplate = await File.ReadAllTextAsync(Path.Combine(modelPath, "chat_template.jinja"));
            if (string.IsNullOrWhiteSpace(chatTemplate))
                throw new InvalidDataException("chat_template.jinja is empty.");
            bool thinkingControllable = SupportsThinkingControl(chatTemplate);

            Write(new WorkerEvent
            {
                Id = string.Empty,
                Type = "ready",
                Provider = effectiveProviderName,
                ProviderLibraryPath = effectiveProviderLibrary,
                ModelPath = modelPath,
                ThinkingControl = thinkingControllable ? ThinkingControlJinja : "not-exposed-by-template",
                CancellationControl = RequestCancellationControl,
                PromptCountingControl = PromptCountingControl,
                PinnedAdapter = pinned ? adapterPin.Trim().ToLowerInvariant() : null,
                AdapterEvidence = adapterEvidence,
                CompatibleDevices = compatibleDevices.ToList(),
                DeviceEvidence = compatibleDevices.Length switch
                {
                    1 => SingleCompatibleDeviceEvidence,
                    0 => "provider-ready-no-ep-device-enumeration",
                    _ => "multiple-compatible-ep-devices"
                }
            });

            // Keep reading stdin while one resident generation runs. Cancellation remains independently
            // receivable; exact prompt counting is admitted only while no generation is active so the
            // tokenizer/model objects stay single-flight and protocol events cannot interleave.
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ServeCommand command;
                try
                {
                    command = JsonSerializer.Deserialize<ServeCommand>(line, JsonOptions)
                        ?? throw new InvalidDataException("The worker command is empty.");
                    requestId = command.Id ?? string.Empty;
                    ValidateCommand(command);
                }
                catch (Exception ex)
                {
                    WriteError(requestId, ex);
                    continue;
                }

                if (string.Equals(command.Op, "cancel", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(activeRequestId, command.Id, StringComparison.Ordinal))
                    {
                        WriteError(command.Id ?? string.Empty,
                            new InvalidOperationException("No active Windows ML request matches this cancellation id."));
                        continue;
                    }

                    if (activeGeneration is { IsCompleted: false }) activeCancellation?.Cancel();
                    continue;
                }

                if (activeGeneration != null)
                {
                    if (!activeGeneration.IsCompleted)
                    {
                        WriteError(command.Id ?? string.Empty,
                            new InvalidOperationException("Windows ML worker is single-flight and another generation is still active."));
                        continue;
                    }
                    await activeGeneration.ConfigureAwait(false);
                    activeCancellation?.Dispose();
                    activeCancellation = null;
                    activeGeneration = null;
                    activeRequestId = null;
                }

                if (string.Equals(command.Op, "count_prompt", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        int promptTokens = CountPromptTokens(command, tokenizer, chatTemplate);
                        Write(new WorkerEvent
                        {
                            Id = command.Id ?? string.Empty,
                            Type = "prompt_count",
                            PromptTokens = promptTokens
                        });
                    }
                    catch (Exception ex) { WriteError(command.Id ?? string.Empty, ex); }
                    continue;
                }

                activeCancellation = new CancellationTokenSource();
                activeRequestId = command.Id;
                CancellationToken generationToken = activeCancellation.Token;
                activeGeneration = RunGenerationCommandAsync(
                    command, model, tokenizer, chatTemplate, generationToken);
            }

            if (activeGeneration != null)
            {
                if (!activeGeneration.IsCompleted) activeCancellation?.Cancel();
                await activeGeneration.ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception ex)
        {
            WriteError(requestId, ex);
            return 1;
        }
        finally
        {
            activeCancellation?.Dispose();
        }
    }

    private static async Task RunGenerationCommandAsync(
        ServeCommand command,
        Model model,
        Tokenizer tokenizer,
        string chatTemplate,
        CancellationToken cancellationToken)
    {
        try
        {
            await GenerateAsync(command, model, tokenizer, chatTemplate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            WriteError(command.Id ?? string.Empty, ex);
        }
    }

    private static (string Prompt, int PromptTokens) RenderPrompt(
        ServeCommand command,
        Tokenizer tokenizer,
        string chatTemplate)
    {
        string messagesJson = JsonSerializer.Serialize(
            command.Messages.Select(message => new
            {
                role = NormalizeRole(message.Role),
                content = message.Content ?? string.Empty
            }),
            JsonOptions);
        string effectiveTemplate = BindThinkingPreference(chatTemplate, command.EnableThinking);
        string prompt = tokenizer.ApplyChatTemplate(effectiveTemplate, messagesJson, "[]", true);
        using Sequences sequences = tokenizer.Encode(prompt);
        int promptTokens = sequences.NumSequences == 0 ? 0 : checked((int)sequences[0].Length);
        if (promptTokens <= 0) throw new InvalidDataException("The tokenizer produced an empty prompt.");
        return (prompt, promptTokens);
    }

    private static int CountPromptTokens(ServeCommand command, Tokenizer tokenizer, string chatTemplate) =>
        RenderPrompt(command, tokenizer, chatTemplate).PromptTokens;

    private static async Task GenerateAsync(
        ServeCommand command,
        Model model,
        Tokenizer tokenizer,
        string chatTemplate,
        CancellationToken cancellationToken)
    {
        (string prompt, int promptTokens) = RenderPrompt(command, tokenizer, chatTemplate);
        using Sequences sequences = tokenizer.Encode(prompt);

        int maxNewTokens = Math.Clamp(command.MaxNewTokens, 1, MaximumOutputTokens);
        long maxLengthLong = (long)promptTokens + maxNewTokens;
        if (maxLengthLong > int.MaxValue)
            throw new InvalidDataException("Requested prompt plus output length is too large.");

        using var parameters = new GeneratorParams(model);
        parameters.SetSearchOption("max_length", (double)maxLengthLong);
        double temperature = double.IsFinite(command.Temperature) ? Math.Clamp(command.Temperature, 0d, 2d) : 0.7d;
        if (temperature <= 0.0001d)
        {
            parameters.SetSearchOption("do_sample", false);
        }
        else
        {
            parameters.SetSearchOption("do_sample", true);
            parameters.SetSearchOption("temperature", temperature);
            parameters.SetSearchOption("top_p", 0.9d);
        }

        using var generator = new Generator(model, parameters);
        generator.AppendTokenSequences(sequences);
        using TokenizerStream stream = tokenizer.CreateStream();
        int generated = 0;
        try
        {
            while (!generator.IsDone() && generated < maxNewTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                cancellationToken.ThrowIfCancellationRequested();
                int? lastToken = GetLastToken(generator);
                if (lastToken is null) continue;
                string chunk = stream.Decode(lastToken.Value);
                generated++;
                if (!string.IsNullOrEmpty(chunk))
                {
                    Write(new WorkerEvent
                    {
                        Id = command.Id ?? string.Empty,
                        Type = "delta",
                        Content = chunk,
                        PromptTokens = promptTokens,
                        GeneratedTokens = generated
                    });
                }
                await Task.Yield();
            }

            Write(new WorkerEvent
            {
                Id = command.Id ?? string.Empty,
                Type = "done",
                PromptTokens = promptTokens,
                GeneratedTokens = generated
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Write(new WorkerEvent
            {
                Id = command.Id ?? string.Empty,
                Type = "cancelled",
                PromptTokens = promptTokens,
                GeneratedTokens = generated
            });
            throw;
        }
    }

    internal static string BindThinkingPreference(string chatTemplate, bool enableThinking) =>
        ChatTemplateThinking.Bind(chatTemplate, enableThinking);

    internal static bool SupportsThinkingControl(string chatTemplate) =>
        ChatTemplateThinking.Supports(chatTemplate);

    private static string DescribeDevice(OrtEpDevice epDevice)
    {
        OrtHardwareDevice hardware = epDevice.HardwareDevice;
        string vendor = (hardware.Vendor ?? string.Empty).Replace('|', '_').Trim();
        return string.Join('|',
            epDevice.EpName,
            hardware.Type.ToString(),
            vendor,
            hardware.VendorId.ToString("x4"),
            hardware.DeviceId.ToString("x4"));
    }

    private static int? GetLastToken(Generator generator)
    {
        ReadOnlySpan<int> sequence = generator.GetSequence(0);
        return sequence.IsEmpty ? null : sequence[^1];
    }

    internal static void ValidateModelLayout(string modelPath)
    {
        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException("Windows ML GenAI serving requires an ONNX model directory: " + modelPath);
        if (!File.Exists(Path.Combine(modelPath, "genai_config.json")))
            throw new InvalidDataException("Windows ML GenAI serving requires genai_config.json in the ONNX model directory.");
        if (!File.Exists(Path.Combine(modelPath, "chat_template.jinja")))
            throw new InvalidDataException("Windows ML GenAI serving requires chat_template.jinja in the ONNX model directory.");
        if (!Directory.EnumerateFiles(modelPath, "*.onnx", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("Windows ML GenAI model directory contains no ONNX graph.");
    }

    private static void ValidateCommand(ServeCommand command)
    {
        if (command.ApiVersion != ApiVersion)
            throw new InvalidDataException("Unsupported Windows ML GenAI worker protocol version: " + command.ApiVersion + ".");
        if (string.IsNullOrWhiteSpace(command.Id)) throw new InvalidDataException("Worker command id is required.");
        string op = command.Op?.Trim().ToLowerInvariant() ?? string.Empty;
        if (op == "cancel") return;
        if (op is not ("generate" or "count_prompt"))
            throw new InvalidDataException("Unsupported Windows ML GenAI worker operation: " + command.Op + ".");
        if (command.Messages == null || command.Messages.Count is < 1 or > MaximumMessages)
            throw new InvalidDataException("Windows ML GenAI requires 1..256 messages.");
        long characters = 0;
        foreach (ServeMessage message in command.Messages)
        {
            _ = NormalizeRole(message.Role);
            characters += message.Content?.Length ?? 0;
            if (characters > MaximumPromptCharacters)
                throw new InvalidDataException("Windows ML GenAI message payload exceeds the PaRuX safety bound.");
        }
        if (op == "count_prompt") return;
        if (command.MaxNewTokens is < 1 or > MaximumOutputTokens)
            throw new InvalidDataException("maxNewTokens must be in 1..32768.");
        if (!double.IsFinite(command.Temperature) || command.Temperature is < 0 or > 2)
            throw new InvalidDataException("temperature must be finite and in 0..2.");
    }

    private static string NormalizeRole(string? role)
    {
        string value = (role ?? string.Empty).Trim().ToLowerInvariant();
        return value is "system" or "user" or "assistant" or "tool"
            ? value
            : throw new InvalidDataException("Unsupported Windows ML GenAI message role: " + value + ".");
    }

    private static long WeightBytes(string modelPath) =>
        Directory.EnumerateFiles(modelPath, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".onnx_data", StringComparison.OrdinalIgnoreCase))
            .Sum(file => new FileInfo(file).Length);

    private static bool TryParseAdapterPin(string? pin, out uint vendorId, out uint deviceId)
    {
        vendorId = 0;
        deviceId = 0;
        string[] parts = (pin ?? string.Empty).Trim().Split(':');
        return parts.Length == 2 && parts[0].Length == 4 && parts[1].Length == 4
            && uint.TryParse(parts[0], System.Globalization.NumberStyles.AllowHexSpecifier, null, out vendorId)
            && uint.TryParse(parts[1], System.Globalization.NumberStyles.AllowHexSpecifier, null, out deviceId);
    }

    private static string RequireProviderName(string? value)
    {
        string provider = (value ?? string.Empty).Trim();
        if (provider.Length is < 3 or > 128
            || provider.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new InvalidDataException("An exact Windows ML execution-provider name is required.");
        return provider;
    }

    private static string ToGenAiProviderName(string windowsMlProviderName)
    {
        if (!GenAiProviderNames.TryGetValue(windowsMlProviderName, out string? genAi))
            throw new NotSupportedException(
                "Windows ML provider " + windowsMlProviderName
                + " is not supported by this ONNX Runtime GenAI build. Supported Windows ML GenAI providers: "
                + string.Join(", ", GenAiProviderNames.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) + ".");
        return genAi;
    }

    private static void WriteError(string id, Exception ex) => Write(new WorkerEvent
    {
        Id = id,
        Type = "error",
        Error = ex.GetType().Name + ": " + ex.Message
    });

    private static void Write(WorkerEvent value)
    {
        string line = JsonSerializer.Serialize(value, JsonOptions);
        lock (OutputGate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private sealed class ServeCommand
    {
        public int ApiVersion { get; set; }
        public string? Id { get; set; }
        public string Op { get; set; } = string.Empty;
        public List<ServeMessage> Messages { get; set; } = new();
        public int MaxNewTokens { get; set; } = 4096;
        public double Temperature { get; set; } = 0.7;
        public string Workload { get; set; } = "chat";
        public bool EnableThinking { get; set; } = true;
    }

    private sealed class ServeMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = string.Empty;
    }

    private sealed class WorkerEvent
    {
        public int ApiVersion { get; set; } = WinMlGenAiServer.ApiVersion;
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? Content { get; set; }
        public string? Error { get; set; }
        public string? Provider { get; set; }
        public string? ProviderLibraryPath { get; set; }
        public string? ModelPath { get; set; }
        public string? ThinkingControl { get; set; }
        public string? CancellationControl { get; set; }
        public string? PromptCountingControl { get; set; }
        public string? DeviceEvidence { get; set; }
        public string? PinnedAdapter { get; set; }
        public string? AdapterEvidence { get; set; }
        public List<string> CompatibleDevices { get; set; } = new();
        public int PromptTokens { get; set; }
        public int GeneratedTokens { get; set; }
    }
}