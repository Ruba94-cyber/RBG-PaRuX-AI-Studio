namespace PaRuX.Contracts;

/// <summary>The authentication header names PaRuX uses. Every client in this repository sends
/// these, and Core accepts only these.</summary>
public static class PaRuXHeaders
{
    public const string Token = "X-PaRuX-Token";
    public const string DeviceToken = "X-PaRuX-Device-Token";
}
