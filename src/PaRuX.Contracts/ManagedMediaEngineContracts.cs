namespace PaRuX.Contracts;

public sealed class ManagedMediaEngineStatus
{
    public string EngineId { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public bool RuntimeInstalled { get; set; }
    public bool ModelInstalled { get; set; }
    public bool Ready { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ModelRevision { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class ManagedMediaEngineInstallResult
{
    public ManagedMediaEngineStatus Status { get; set; } = new();
    public string Validation { get; set; } = string.Empty;
}

/// <summary>
/// Truthful discovery record for a shipping or evaluated media engine. Discovery is deliberately
/// broader than execution: an audited candidate may be visible before PaRuX has an executor for it.
/// Callable, benchmark eligibility and AUTO eligibility are independent claims and must never be
/// inferred merely from an engine being named in this catalog.
/// </summary>
public sealed class MediaEngineCatalogEntry
{
    public string EngineId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EngineVersion { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = Array.Empty<string>();
    public string Ownership { get; set; } = "PARUX_MANAGED";
    public string Lifecycle { get; set; } = string.Empty;
    public string? SourceRepository { get; set; }
    public string? SourceRevision { get; set; }
    public string? CodeLicense { get; set; }
    public string DependencyLicenseStatus { get; set; } = "not-applicable";
    public bool Audited { get; set; }
    public bool Callable { get; set; }
    public bool BenchmarkEligible { get; set; }
    public bool AutoEligible { get; set; }
    public bool Ready { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class ImageEngineRaceRequest
{
    public MediaJobRequest Job { get; set; } = new() { Type = "image" };
    /// <summary>
    /// When true, fail before rendering unless every production race engine is installed. When
    /// false, PaRuX still requires at least two ready candidates and never fabricates a race.
    /// </summary>
    public bool RequireAllManagedEngines { get; set; } = true;
}
