namespace PaRuX.UITests.Infrastructure;

internal static class Repository
{
    internal static string Root { get; } = FindRoot();
    internal static string AppExecutable => Environment.GetEnvironmentVariable("PARUX_UI_APP_PATH")
        ?? Path.Combine(Root, "src", "PaRuX.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "PaRuX.exe");

    private static string FindRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not find PaRuX.sln from the UI test output directory.");
    }
}
