using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PaRuX.Views
{
    /// <summary>Create shows one mode at a time (Image, Video, Edit, 3D, Speech, Workflows, Gallery) instead of one very long page.</summary>
    public sealed partial class CreateWorkspaceView
    {
        private readonly Dictionary<string, UIElement> _modePanels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SelectorBarItem> _modeItems = new(StringComparer.Ordinal);
        private SelectorBar? _modeBar;

        private UIElement BuildModeTabs(IReadOnlyList<(string Name, UIElement Panel)> modes)
        {
            var root = new StackPanel { Spacing = 12 };
            _modeBar = new SelectorBar();
            AutomationProperties.SetAutomationId(_modeBar, "Create_Mode_Tabs");
            var host = new Grid();
            foreach ((string name, UIElement panel) in modes)
            {
                var item = new SelectorBarItem { Text = name };
                AutomationProperties.SetAutomationId(item, "Create_Mode_" + name);
                _modeBar.Items.Add(item);
                _modeItems[name] = item;
                _modePanels[name] = panel;
                panel.Visibility = Visibility.Collapsed;
                host.Children.Add(panel);
            }
            _modeBar.SelectionChanged += (_, _) => ShowSelectedMode();
            root.Children.Add(_modeBar);
            root.Children.Add(host);
            SelectMode(modes[0].Name);
            return root;
        }

        private void SelectMode(string name)
        {
            if (_modeBar != null && _modeItems.TryGetValue(name, out SelectorBarItem? item)) _modeBar.SelectedItem = item;
            ShowSelectedMode();
        }

        private void ShowSelectedMode()
        {
            string? selected = (_modeBar?.SelectedItem as SelectorBarItem)?.Text;
            foreach (KeyValuePair<string, UIElement> pair in _modePanels)
                pair.Value.Visibility = pair.Key == selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
