using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PaRuX.Views;

public sealed partial class SettingsView
{
    internal const string LegacyAndroidComputerUseStopCopy =
        "Does not yet reach Android's on-device Computer Use.";

    internal const string CurrentAndroidComputerUseStopCopy =
        "Android Computer Use requests are supervised Windows tasks, so the same Windows execution is covered by Universal STOP.";

    /// <summary>
    /// Reconciles a legacy Settings markup sentence from the retired Android on-device Computer
    /// Use design. Current Android is intentionally a paired Windows companion with no Accessibility
    /// or raw-input authority. The existing SettingsView template hook calls this narrow helper so
    /// the shipped UI cannot advertise the superseded execution model without adding another
    /// competing OnApplyTemplate override to the partial class.
    /// </summary>
    internal static int ReconcileLegacyStopAllCopy(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        int replacements = 0;

        if (root is TextBlock textBlock
            && !string.IsNullOrWhiteSpace(textBlock.Text)
            && textBlock.Text.Contains(LegacyAndroidComputerUseStopCopy, StringComparison.Ordinal))
        {
            textBlock.Text = textBlock.Text.Replace(
                LegacyAndroidComputerUseStopCopy,
                CurrentAndroidComputerUseStopCopy,
                StringComparison.Ordinal);
            replacements++;
        }

        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < children; index++)
            replacements += ReconcileLegacyStopAllCopy(VisualTreeHelper.GetChild(root, index));

        return replacements;
    }
}
