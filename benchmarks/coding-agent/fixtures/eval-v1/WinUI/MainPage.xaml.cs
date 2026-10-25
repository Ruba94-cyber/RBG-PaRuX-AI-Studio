namespace PaRuX.Benchmarks.EvalV1;

public static class NavigationPolicy
{
    public static string HomeForMode(bool advanced) => advanced ? "Models" : "Chat";

    public static bool IsWorkspaceVisible(bool advanced, string workspace)
        => advanced;

    public static bool HandleKey(string key)
        => key == "Enter";
}
