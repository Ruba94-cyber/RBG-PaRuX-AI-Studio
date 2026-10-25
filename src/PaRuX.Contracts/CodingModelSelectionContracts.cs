namespace PaRuX.Contracts;

public static class CodingModelSelectionMode
{
    /// <summary>Core picks the coding model from measured local task outcomes when evidence suffices.</summary>
    public const string Auto = "auto";
    /// <summary>The model assigned to the coding role is always used.</summary>
    public const string Assigned = "assigned";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Assigned, StringComparison.OrdinalIgnoreCase) ? Assigned : Auto;
}

/// <summary>Measured coding-task evidence and utility for one candidate model on this hardware.</summary>
public sealed class CodingModelScoreDto
{
    public required string FileName { get; set; }
    public required string FilePath { get; set; }
    public bool HardwareCompatible { get; set; }
    public int Attempts { get; set; }
    public int Verified { get; set; }
    public int ProtocolTrouble { get; set; }
    public double? MedianVerifiedSeconds { get; set; }
    /// <summary>Wilson 95% lower bound of the verified-task rate (conservative with few samples).</summary>
    public double Quality { get; set; }
    public double Reliability { get; set; }
    /// <summary>Fastest median verified time among ranked candidates divided by this candidate's.</summary>
    public double Speed { get; set; }
    public double? Utility { get; set; }
    public required string Status { get; set; }
}

public sealed class CodingModelSelectionDto
{
    public required string Mode { get; set; }
    /// <summary>"auto" when the measured winner is used, otherwise "assigned".</summary>
    public required string EffectiveSource { get; set; }
    public string? SelectedFilePath { get; set; }
    public string? SelectedFileName { get; set; }
    public required string Reason { get; set; }
    public required string HardwareFingerprint { get; set; }
    public double QualityWeight { get; set; }
    public double ReliabilityWeight { get; set; }
    public double SpeedWeight { get; set; }
    public int MinimumAttempts { get; set; }
    public List<CodingModelScoreDto> Candidates { get; set; } = new();
    public DateTime DecidedUtc { get; set; }
}

public sealed class SetCodingModelSelectionModeRequest
{
    public required string Mode { get; set; }
}
