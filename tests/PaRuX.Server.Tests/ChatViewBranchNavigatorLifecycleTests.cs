using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Physical-PC regression, 2026-09-09: the conversation branch Previous/Next/Versions navigator
/// (ChatView.ConversationBranches.cs) never appeared in the running app despite correct, persisted
/// branch data. Root cause, confirmed live with a temporary diagnostic log: ChatView is a plain
/// UserControl with no Style/ControlTemplate of its own, so WinUI never actually invokes
/// OnApplyTemplate() for it - and that override was the ONLY call site for
/// InitializeConversationBranchHistory() (and InitializeBoundedChatRendering()). SkillSelector.Parent
/// is also still null immediately after InitializeComponent() in the constructor, so calling the
/// initializers there is too early too - ChatView.OnLoaded is the first point where the element is
/// actually attached to the live visual tree. PaRuX.App itself (WinUI/net8.0-windows) can't be
/// referenced from this net8.0 xunit project, so this test guards the fix the same way the rest of
/// this file's siblings guard doc/contract text: by asserting on the real source, not a compiled
/// type. It intentionally does NOT assert that OnApplyTemplate calls them (that override is proven
/// dead and kept only as a harmless defensive no-op) - only that OnLoaded, the call site that is
/// actually reachable, still wires all three idempotent initializers.
/// </summary>
public sealed class ChatViewBranchNavigatorLifecycleTests
{
    [Fact]
    public void ChatView_OnLoaded_wires_the_three_idempotent_lifecycle_initializers()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "src", "PaRuX.App", "Views", "ChatView.xaml.cs");
        Assert.True(File.Exists(path), "ChatView.xaml.cs is missing: " + path);
        string text = File.ReadAllText(path);

        int onLoadedStart = text.IndexOf("private void OnLoaded(object sender, RoutedEventArgs e)", StringComparison.Ordinal);
        Assert.True(onLoadedStart >= 0, "ChatView.xaml.cs no longer declares OnLoaded(object, RoutedEventArgs) with the expected signature.");

        int bodyStart = text.IndexOf('{', onLoadedStart);
        Assert.True(bodyStart >= 0, "Could not find the opening brace of ChatView.OnLoaded.");

        // OnLoaded is a small, single-level-nesting method: find its matching closing brace by
        // depth-counting rather than assuming a fixed length, so this stays correct if the body grows.
        int depth = 0;
        int bodyEnd = -1;
        for (int i = bodyStart; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) { bodyEnd = i; break; }
            }
        }
        Assert.True(bodyEnd > bodyStart, "Could not find the closing brace of ChatView.OnLoaded.");

        string onLoadedBody = text[bodyStart..bodyEnd];

        // All three are idempotent (guarded by their own instance flags), so calling them from
        // OnLoaded is safe even though OnApplyTemplate also references them defensively.
        Assert.Contains("InitializeBoundedChatRendering();", onLoadedBody, StringComparison.Ordinal);
        Assert.Contains("InitializeNativeCodingHandoffUpgrade();", onLoadedBody, StringComparison.Ordinal);
        Assert.Contains("InitializeConversationBranchHistory();", onLoadedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversationBranches_has_accessibility_names_accelerators_and_live_setting()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "src", "PaRuX.App", "Views", "ChatView.ConversationBranches.cs");
        Assert.True(File.Exists(path), "ChatView.ConversationBranches.cs is missing: " + path);
        string text = File.ReadAllText(path);

        Assert.Contains("AutomationProperties.SetName(previous, \"Previous conversation version\");", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAcceleratorKey(previous, \"Ctrl+Alt+Left\");", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(next, \"Next conversation version\");", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAcceleratorKey(next, \"Ctrl+Alt+Right\");", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetLiveSetting(counter, AutomationLiveSetting.Polite);", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(versions, \"Conversation version history\");", text, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(_conversationBranchCounter,", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
    }
}
