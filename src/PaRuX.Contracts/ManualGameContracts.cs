namespace PaRuX.Contracts;

public sealed record ManualGameDto(string Id, string Name, string ExecutablePath);
public sealed record AddManualGameRequest(string Name, string ExecutablePath);
