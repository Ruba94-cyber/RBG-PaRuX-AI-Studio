using System;
using System.Collections.Generic;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PaRuX.UITests.Infrastructure;

namespace PaRuX.UITests.PageObjects;

internal sealed class WorkspaceDescriptor
{
    internal string Id { get; }
    internal string Name { get; }
    internal bool IsAdvanced { get; }
    internal string LandmarkDescription { get; }
    internal Func<Window, bool> IsLandmarkVisible { get; }

    internal WorkspaceDescriptor(
        string id,
        string name,
        bool isAdvanced,
        string landmarkDescription,
        Func<Window, bool> isLandmarkVisible)
    {
        Id = id;
        Name = name;
        IsAdvanced = isAdvanced;
        LandmarkDescription = landmarkDescription;
        IsLandmarkVisible = isLandmarkVisible;
    }
}

internal sealed class WorkspaceNavigationPage
{
    private readonly Window _window;
    private readonly UiTestApplication _app;

    internal WorkspaceNavigationPage(Window window, UiTestApplication app)
    {
        _window = window;
        _app = app;
    }

    internal static readonly WorkspaceDescriptor ChatDescriptor = new(
        "Navigation_Chat_Item",
        "Chat",
        isAdvanced: false,
        landmarkDescription: "Chat prompt edit box (Chat_Prompt_TextBox)",
        isLandmarkVisible: window =>
        {
            var prompt = window.FindFirstDescendant(cf => cf.ByAutomationId("Chat_Prompt_TextBox").And(cf.ByControlType(ControlType.Edit)));
            return prompt != null && !prompt.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor CreateDescriptor = new(
        "Navigation_Create_Item",
        "Create",
        isAdvanced: false,
        landmarkDescription: "Create status text or cancel button (Create_Operation_Status_Text)",
        isLandmarkVisible: window =>
        {
            var status = window.FindFirstDescendant(cf => cf.ByAutomationId("Create_Operation_Status_Text").And(cf.ByControlType(ControlType.Text)));
            if (status != null && !status.Properties.IsOffscreen.ValueOrDefault) return true;
            var cancel = window.FindFirstDescendant(cf => cf.ByAutomationId("Create_Cancel_Active_Job_Button").And(cf.ByControlType(ControlType.Button)));
            return cancel != null && !cancel.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor ProjectsDescriptor = new(
        "Navigation_Projects_Item",
        "Projects",
        isAdvanced: false,
        landmarkDescription: "Projects & Artifacts heading or Create Project button",
        isLandmarkVisible: window =>
        {
            var btn = window.FindFirstDescendant(cf => cf.ByName("Create Project").And(cf.ByControlType(ControlType.Button)));
            if (btn != null && !btn.Properties.IsOffscreen.ValueOrDefault) return true;
            var heading = window.FindFirstDescendant(cf => cf.ByName("Projects & Artifacts").And(cf.ByControlType(ControlType.Text)));
            return heading != null && !heading.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor ModelHubDescriptor = new(
        "Navigation_ModelHub_Item",
        "Model Hub",
        isAdvanced: false,
        landmarkDescription: "Model Hub search box (ModelHub_Search_TextBox)",
        isLandmarkVisible: window =>
        {
            var search = window.FindFirstDescendant(cf => cf.ByAutomationId("ModelHub_Search_TextBox").And(cf.ByControlType(ControlType.Edit)));
            if (search != null && !search.Properties.IsOffscreen.ValueOrDefault) return true;
            var btn = window.FindFirstDescendant(cf => cf.ByAutomationId("ModelHub_Search_Button").And(cf.ByControlType(ControlType.Button)));
            return btn != null && !btn.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor ModelRoutingDescriptor = new(
        "Navigation_ModelRouting_Item",
        "Model Intelligence",
        isAdvanced: true,
        landmarkDescription: "Model routing role combo box (ModelRouting_Role_ComboBox)",
        isLandmarkVisible: window =>
        {
            var combo = window.FindFirstDescendant(cf => cf.ByAutomationId("ModelRouting_Role_ComboBox").And(cf.ByControlType(ControlType.ComboBox)));
            return combo != null && !combo.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor LifecycleDescriptor = new(
        "Navigation_Lifecycle_Item",
        "Lifecycle Lab",
        isAdvanced: true,
        landmarkDescription: "Lifecycle Lab title text or Refresh all button",
        isLandmarkVisible: window =>
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("Lifecycle Lab").And(cf.ByControlType(ControlType.Text)));
            if (title != null && !title.Properties.IsOffscreen.ValueOrDefault) return true;
            var refresh = window.FindFirstDescendant(cf => cf.ByName("Refresh all").And(cf.ByControlType(ControlType.Button)));
            return refresh != null && !refresh.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor WorkflowsDescriptor = new(
        "Navigation_Workflows_Item",
        "Workflows",
        isAdvanced: true,
        landmarkDescription: "Workflows title text or New workflow button",
        isLandmarkVisible: window =>
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("Workflows").And(cf.ByControlType(ControlType.Text)));
            if (title != null && !title.Properties.IsOffscreen.ValueOrDefault) return true;
            var btn = window.FindFirstDescendant(cf => cf.ByName("New workflow…").And(cf.ByControlType(ControlType.Button)));
            return btn != null && !btn.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor ToolsDescriptor = new(
        "Navigation_Tools_Item",
        "Tools & Automations",
        isAdvanced: true,
        landmarkDescription: "Tools & Automations title or Skills & Permissions heading",
        isLandmarkVisible: window =>
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("Tools & Automations").And(cf.ByControlType(ControlType.Text)));
            if (title != null && !title.Properties.IsOffscreen.ValueOrDefault) return true;
            var sec = window.FindFirstDescendant(cf => cf.ByName("SKILLS & PERMISSIONS").And(cf.ByControlType(ControlType.Text)));
            return sec != null && !sec.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor ObservabilityDescriptor = new(
        "Navigation_Observability_Item",
        "Observability",
        isAdvanced: true,
        landmarkDescription: "Observability & Benchmarks title or Current Core Snapshot heading",
        isLandmarkVisible: window =>
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("Observability & Benchmarks").And(cf.ByControlType(ControlType.Text)));
            if (title != null && !title.Properties.IsOffscreen.ValueOrDefault) return true;
            var sec = window.FindFirstDescendant(cf => cf.ByName("CURRENT CORE SNAPSHOT").And(cf.ByControlType(ControlType.Text)));
            return sec != null && !sec.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor UpdatesDescriptor = new(
        "Navigation_Updates_Item",
        "Updates",
        isAdvanced: true,
        landmarkDescription: "Update Manager title or Check again button",
        isLandmarkVisible: window =>
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("Update Manager").And(cf.ByControlType(ControlType.Text)));
            if (title != null && !title.Properties.IsOffscreen.ValueOrDefault) return true;
            var btn = window.FindFirstDescendant(cf => cf.ByName("Check again").And(cf.ByControlType(ControlType.Button)));
            return btn != null && !btn.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor RuntimeDescriptor = new(
        "Navigation_Runtime_Item",
        "Runtime",
        isAdvanced: true,
        landmarkDescription: "Runtime Manager title or Cancel active install button",
        isLandmarkVisible: window =>
        {
            var title = window.FindFirstDescendant(cf => cf.ByName("Runtime Manager").And(cf.ByControlType(ControlType.Text)));
            if (title != null && !title.Properties.IsOffscreen.ValueOrDefault) return true;
            var btn = window.FindFirstDescendant(cf => cf.ByName("Cancel active install").And(cf.ByControlType(ControlType.Button)));
            return btn != null && !btn.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly WorkspaceDescriptor SettingsDescriptor = new(
        "Navigation_Settings_Item",
        "Settings",
        isAdvanced: false,
        landmarkDescription: "Theme combo box (Settings_Theme_ComboBox)",
        isLandmarkVisible: window =>
        {
            var combo = window.FindFirstDescendant(cf => cf.ByAutomationId("Settings_Theme_ComboBox").And(cf.ByControlType(ControlType.ComboBox)));
            return combo != null && !combo.Properties.IsOffscreen.ValueOrDefault;
        });

    internal static readonly IReadOnlyList<WorkspaceDescriptor> SimpleWorkspaces = new[]
    {
        CreateDescriptor,
        ProjectsDescriptor,
        ModelHubDescriptor,
        SettingsDescriptor
    };

    internal static readonly IReadOnlyList<WorkspaceDescriptor> AdvancedWorkspaces = new[]
    {
        ModelRoutingDescriptor,
        LifecycleDescriptor,
        WorkflowsDescriptor,
        ToolsDescriptor,
        ObservabilityDescriptor,
        UpdatesDescriptor,
        RuntimeDescriptor
    };

    internal static readonly IReadOnlyList<WorkspaceDescriptor> AllNavigationWorkspaces = new[]
    {
        CreateDescriptor,
        ProjectsDescriptor,
        ModelHubDescriptor,
        ModelRoutingDescriptor,
        LifecycleDescriptor,
        WorkflowsDescriptor,
        ToolsDescriptor,
        ObservabilityDescriptor,
        UpdatesDescriptor,
        RuntimeDescriptor,
        SettingsDescriptor
    };

    internal void EnableAdvanced()
    {
        var toggle = UiWait.ById(_window, "MainWindow_StudioMode_Toggle", ControlType.Button);
        if (toggle.Patterns.Toggle.PatternOrDefault?.ToggleState == ToggleState.On
            || toggle.Name?.Contains("Advanced", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        _window.SetForeground();
        if (toggle.Patterns.Toggle.PatternOrDefault != null)
        {
            toggle.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            toggle.Click(moveMouse: false);
        }

        UiWait.For(() =>
        {
            var current = _window.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_StudioMode_Toggle"));
            return current != null && (current.Patterns.Toggle.PatternOrDefault?.ToggleState == ToggleState.On
                || current.Name?.Contains("Advanced", StringComparison.OrdinalIgnoreCase) == true);
        }, "Studio mode toggle switched to Advanced");
    }

    internal void EnableSimple()
    {
        var toggle = UiWait.ById(_window, "MainWindow_StudioMode_Toggle", ControlType.Button);
        if (toggle.Patterns.Toggle.PatternOrDefault?.ToggleState == ToggleState.Off
            || toggle.Name?.Contains("Simple", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        _window.SetForeground();
        if (toggle.Patterns.Toggle.PatternOrDefault != null)
        {
            toggle.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            toggle.Click(moveMouse: false);
        }

        UiWait.For(() =>
        {
            var current = _window.FindFirstDescendant(cf => cf.ByAutomationId("MainWindow_StudioMode_Toggle"));
            return current != null && (current.Patterns.Toggle.PatternOrDefault?.ToggleState == ToggleState.Off
                || current.Name?.Contains("Simple", StringComparison.OrdinalIgnoreCase) == true);
        }, "Studio mode toggle switched to Simple");
    }

    internal AutomationElement? FindChatNavItem()
    {
        var el = _window.FindFirstDescendant(cf => cf.ByAutomationId("Navigation_Chat_Item"));
        if (el != null) return el;

        el = _window.FindFirstDescendant(cf => cf.ByName("Chat, home workspace").And(cf.ByControlType(ControlType.ListItem)));
        if (el != null) return el;

        el = _app.FindRawDescendant(_window, candidate =>
        {
            string? name = candidate.Properties.Name.ValueOrDefault;
            return name != null && name.StartsWith("Chat, home workspace", StringComparison.Ordinal);
        });
        return el;
    }

    internal AutomationElement? FindNavItem(string id)
    {
        if (id == ChatDescriptor.Id) return FindChatNavItem();
        return _window.FindFirstDescendant(cf => cf.ByAutomationId(id).And(cf.ByControlType(ControlType.ListItem)));
    }

    internal bool IsItemRealized(string id) => FindNavItem(id) != null;

    internal bool IsItemSelected(string id)
    {
        var item = FindNavItem(id);
        if (item == null) return false;
        return item.Patterns.SelectionItem.PatternOrDefault?.IsSelected == true;
    }

    internal string? GetItemName(string id)
    {
        var item = FindNavItem(id);
        return item?.Properties.Name.ValueOrDefault;
    }

    internal void ActivateNavItem(AutomationElement item)
    {
        _window.SetForeground();
        try { item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); }
        catch { /* ScrollItem not exposed in compact view */ }

        if (item.Patterns.SelectionItem.PatternOrDefault != null)
        {
            try
            {
                item.Patterns.SelectionItem.Pattern.Select();
                return;
            }
            catch { /* Fall back to keyboard activation */ }
        }

        try
        {
            item.Focus();
            Keyboard.Press(VirtualKeyShort.SPACE);
            return;
        }
        catch { /* Fall back to mouse click */ }

        item.Click(moveMouse: false);
    }

    internal void NavigateTo(string automationId)
    {
        if (automationId == ChatDescriptor.Id)
        {
            NavigateToChat();
            return;
        }

        _window.SetForeground();
        AutomationElement item = UiWait.ById(_window, automationId, ControlType.ListItem);
        ActivateNavItem(item);
    }

    internal void NavigateToChat()
    {
        var chatItem = FindChatNavItem();
        if (chatItem != null)
        {
            ActivateNavItem(chatItem);
        }
        else
        {
            var newChat = UiWait.ById(_window, "MainWindow_NewChat_Button", ControlType.Button);
            if (newChat.Patterns.Invoke.PatternOrDefault != null)
                newChat.Patterns.Invoke.Pattern.Invoke();
            else
                newChat.Click(moveMouse: false);
        }
        UiWait.For(() => ChatDescriptor.IsLandmarkVisible(_window), "Chat workspace prompt visible");
    }

    internal void WaitForWorkspace(WorkspaceDescriptor descriptor, TimeSpan? timeout = null)
    {
        UiWait.For(
            () => descriptor.IsLandmarkVisible(_window),
            descriptor.Name + " landmark (" + descriptor.LandmarkDescription + ") visible",
            timeout ?? TimeSpan.FromSeconds(15));
    }

    internal bool IsWorkspaceLandmarkVisible(WorkspaceDescriptor descriptor) =>
        descriptor.IsLandmarkVisible(_window);
}
