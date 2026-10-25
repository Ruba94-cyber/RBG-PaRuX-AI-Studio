using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace PaRuX.UITests.Infrastructure;

internal static class UiWait
{
    /// <summary>
    /// A UIA call can transiently time out (COMException 0x80131505) while the app is busy laying out; that is a not-yet-observable
    /// state, not a verdict. The enclosing wait stays bounded and still fails with a TimeoutException.
    /// </summary>
    private static bool Probe(Func<bool> condition)
    {
        try { return condition(); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
        catch (FlaUI.Core.Exceptions.ElementNotAvailableException) { return false; }
    }

    internal static void For(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        DateTime until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < until)
        {
            if (Probe(condition)) return;
            Thread.Sleep(100); // bounded polling is required because UIA exposes no change event for every WinUI property.
        }
        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    /// <summary>
    /// Sends an input action (key press or click) and waits for its observable effect. SendInput can be lost while the XAML island is still
    /// taking keyboard focus after a UIA Focus(); when the input never produced the effect it is sent again
    /// (bounded), so a lost input is not reported as a product failure and a delivered one is never repeated.
    /// </summary>
    internal static void InputUntil(Action sendKey, Func<bool> effect, string description, int attempts = 3)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            sendKey();
            DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(attempt == attempts ? 20 : 2);
            while (DateTime.UtcNow < until)
            {
                if (Probe(effect)) return;
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException("Timed out waiting for " + description + ".");
    }

    internal static AutomationElement ById(AutomationElement root, string id, ControlType type)
    {
        AutomationElement? element = null;
        For(() =>
        {
            element = root.FindFirstDescendant(cf => cf.ByAutomationId(id).And(cf.ByControlType(type)));
            return element != null;
        }, "UI element " + id + " (" + type + ")");
        return element ?? throw new InvalidOperationException("Missing UI element: " + id + " (" + type + ").");
    }
}
