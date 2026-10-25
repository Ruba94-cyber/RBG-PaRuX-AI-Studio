using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    public sealed class WorkspaceNavigationOrderTests
    {
        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
                current = current.Parent;
            }
            current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "PaRuX.sln"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate PaRuX.sln from the test process.");
        }

        [Fact]
        public void CanonicalOrder_Equals_NavigationViewItem_Order_In_MainWindowXaml()
        {
            // Acceptance (1): the canonical order equals the NavigationViewItem order in MainWindow.xaml
            // (the test reads the XAML text and extracts AutomationId values in order)
            string root = FindRepositoryRoot();
            string xamlPath = Path.Combine(root, "src", "PaRuX.App", "MainWindow.xaml");
            Assert.True(File.Exists(xamlPath), $"MainWindow.xaml not found at {xamlPath}");

            string xamlContent = File.ReadAllText(xamlPath);

            // Extract AutomationIds of all NavigationViewItem elements from XAML in order
            var matches = Regex.Matches(xamlContent, @"<NavigationViewItem[^>]*AutomationProperties\.AutomationId=""([^""]+)""");
            var extractedAutomationIds = matches.Select(m => m.Groups[1].Value).ToList();

            Assert.NotEmpty(extractedAutomationIds);
            Assert.Equal(WorkspaceNavigationOrder.CanonicalOrder, extractedAutomationIds);
            Assert.Equal(WorkspaceNavigationOrder.CanonicalAutomationIds, extractedAutomationIds);
        }

        [Fact]
        public void Previous_From_Projects_Is_Create_In_Both_Simple_And_Advanced_Modes()
        {
            // Acceptance (2): Previous from Projects is Create
            string? previousInAdvanced = WorkspaceNavigationOrder.Previous(
                WorkspaceNavigationOrder.ProjectsAutomationId, isAdvancedMode: true);
            string? previousInSimple = WorkspaceNavigationOrder.Previous(
                WorkspaceNavigationOrder.ProjectsAutomationId, isAdvancedMode: false);

            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId, previousInAdvanced);
            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId, previousInSimple);

            // Verify GetPrevious method matches Previous
            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.GetPrevious(WorkspaceNavigationOrder.ProjectsAutomationId, isAdvancedMode: true));
            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.GetPrevious(WorkspaceNavigationOrder.ProjectsAutomationId, isAdvancedMode: false));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Previous_From_Selected_Projects_Uses_Create_When_Chat_Focus_Is_Stale(bool isAdvancedMode)
        {
            string? origin = WorkspaceNavigationOrder.ResolveNavigationOrigin(
                WorkspaceNavigationOrder.ChatAutomationId,
                WorkspaceNavigationOrder.ProjectsAutomationId,
                keyboardFocusIsCurrent: false,
                isAdvancedMode);

            Assert.Equal(WorkspaceNavigationOrder.ProjectsAutomationId, origin);
            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.GetPrevious(origin!, isAdvancedMode));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Arrow_Traversal_Continues_From_Keyboard_Focus_When_Selection_Is_Unchanged(bool isAdvancedMode)
        {
            string? origin = WorkspaceNavigationOrder.ResolveNavigationOrigin(
                WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.ProjectsAutomationId,
                keyboardFocusIsCurrent: true,
                isAdvancedMode);

            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId, origin);
            Assert.Equal(WorkspaceNavigationOrder.ChatAutomationId,
                WorkspaceNavigationOrder.GetPrevious(origin!, isAdvancedMode));
        }

        [Fact]
        public void Simple_And_Advanced_Filtering_Keep_Relative_Order()
        {
            // Acceptance (3): Simple and Advanced filtering keep the relative order
            IReadOnlyList<string> simple = WorkspaceNavigationOrder.GetOrderedAutomationIds(isAdvancedMode: false);
            IReadOnlyList<string> advanced = WorkspaceNavigationOrder.GetOrderedAutomationIds(isAdvancedMode: true);

            Assert.NotEmpty(simple);
            Assert.NotEmpty(advanced);
            Assert.True(advanced.Count > simple.Count);

            var advancedList = advanced.ToList();
            // Every item in Simple mode must be in Advanced mode and appear in the exact same relative order
            int lastAdvancedIndex = -1;
            foreach (string simpleId in simple)
            {
                int currentAdvancedIndex = advancedList.IndexOf(simpleId);
                Assert.True(currentAdvancedIndex >= 0, $"Expected {simpleId} to exist in Advanced mode");
                Assert.True(currentAdvancedIndex > lastAdvancedIndex,
                    $"Expected relative order preserved: {simpleId} index {currentAdvancedIndex} must be > previous index {lastAdvancedIndex}");
                lastAdvancedIndex = currentAdvancedIndex;
            }

            // Verify specific members of Simple mode
            Assert.Equal(new[]
            {
                WorkspaceNavigationOrder.ChatAutomationId,
                WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.ProjectsAutomationId,
                WorkspaceNavigationOrder.ModelHubAutomationId,
                WorkspaceNavigationOrder.SettingsAutomationId,
                WorkspaceNavigationOrder.HelpAutomationId
            }, simple);

            // Advanced-only items must not be in Simple
            string[] advancedOnlyIds =
            {
                WorkspaceNavigationOrder.ModelRoutingAutomationId,
                WorkspaceNavigationOrder.LifecycleAutomationId,
                WorkspaceNavigationOrder.WorkflowAutomationId,
                WorkspaceNavigationOrder.ToolsAutomationId,
                WorkspaceNavigationOrder.ObservabilityAutomationId,
                WorkspaceNavigationOrder.UpdatesAutomationId,
                WorkspaceNavigationOrder.RuntimeAutomationId
            };
            foreach (string advId in advancedOnlyIds)
            {
                Assert.DoesNotContain(advId, simple);
                Assert.Contains(advId, advanced);
            }
        }

        [Fact]
        public void Chat_Remains_First_In_Both_Simple_And_Advanced_Modes()
        {
            // Acceptance (4): Chat remains first
            IReadOnlyList<string> simple = WorkspaceNavigationOrder.GetOrderedAutomationIds(isAdvancedMode: false);
            IReadOnlyList<string> advanced = WorkspaceNavigationOrder.GetOrderedAutomationIds(isAdvancedMode: true);

            Assert.Equal(WorkspaceNavigationOrder.ChatAutomationId, simple[0]);
            Assert.Equal(WorkspaceNavigationOrder.ChatAutomationId, advanced[0]);
            Assert.Equal(WorkspaceNavigationOrder.ChatAutomationId, WorkspaceNavigationOrder.CanonicalOrder[0]);

            // Up/Previous from Chat has defined end (null, cannot navigate above Chat)
            Assert.Null(WorkspaceNavigationOrder.Previous(WorkspaceNavigationOrder.ChatAutomationId, isAdvancedMode: false));
            Assert.Null(WorkspaceNavigationOrder.Previous(WorkspaceNavigationOrder.ChatAutomationId, isAdvancedMode: true));
            Assert.Null(WorkspaceNavigationOrder.GetPrevious(WorkspaceNavigationOrder.ChatAutomationId, isAdvancedMode: false));
            Assert.Null(WorkspaceNavigationOrder.GetPrevious(WorkspaceNavigationOrder.ChatAutomationId, isAdvancedMode: true));

            // Down/Next from Chat is Create
            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.Next(WorkspaceNavigationOrder.ChatAutomationId, isAdvancedMode: false));
            Assert.Equal(WorkspaceNavigationOrder.CreateAutomationId,
                WorkspaceNavigationOrder.Next(WorkspaceNavigationOrder.ChatAutomationId, isAdvancedMode: true));
        }

        [Fact]
        public void Next_And_Previous_Defined_Ends_Boundary_Conditions()
        {
            // Top end boundary: Chat has no previous
            Assert.Null(WorkspaceNavigationOrder.GetPrevious(WorkspaceNavigationOrder.ChatAutomationId, false));
            Assert.Null(WorkspaceNavigationOrder.GetPrevious(WorkspaceNavigationOrder.ChatAutomationId, true));

            // Bottom end boundary: Help has no next
            Assert.Null(WorkspaceNavigationOrder.GetNext(WorkspaceNavigationOrder.HelpAutomationId, false));
            Assert.Null(WorkspaceNavigationOrder.GetNext(WorkspaceNavigationOrder.HelpAutomationId, true));

            // Unknown and invalid IDs return null
            Assert.Null(WorkspaceNavigationOrder.GetNext("NonExistentItem", false));
            Assert.Null(WorkspaceNavigationOrder.GetNext("NonExistentItem", true));
            Assert.Null(WorkspaceNavigationOrder.GetPrevious("NonExistentItem", false));
            Assert.Null(WorkspaceNavigationOrder.GetPrevious("NonExistentItem", true));
            Assert.Null(WorkspaceNavigationOrder.GetNext("", false));
            Assert.Null(WorkspaceNavigationOrder.GetPrevious(null!, false));
        }

        [Fact]
        public void Full_Sequential_Down_And_Up_Traversal_Visits_Every_Item()
        {
            // Traversal in Advanced mode
            IReadOnlyList<string> advanced = WorkspaceNavigationOrder.AdvancedOrder;
            string current = advanced[0];
            for (int i = 1; i < advanced.Count; i++)
            {
                string? next = WorkspaceNavigationOrder.Next(current, isAdvancedMode: true);
                Assert.Equal(advanced[i], next);
                current = next!;
            }
            Assert.Null(WorkspaceNavigationOrder.Next(current, isAdvancedMode: true));

            // Reverse traversal in Advanced mode
            for (int i = advanced.Count - 2; i >= 0; i--)
            {
                string? prev = WorkspaceNavigationOrder.Previous(current, isAdvancedMode: true);
                Assert.Equal(advanced[i], prev);
                current = prev!;
            }
            Assert.Null(WorkspaceNavigationOrder.Previous(current, isAdvancedMode: true));

            // Traversal in Simple mode
            IReadOnlyList<string> simple = WorkspaceNavigationOrder.SimpleOrder;
            current = simple[0];
            for (int i = 1; i < simple.Count; i++)
            {
                string? next = WorkspaceNavigationOrder.Next(current, isAdvancedMode: false);
                Assert.Equal(simple[i], next);
                current = next!;
            }
            Assert.Null(WorkspaceNavigationOrder.Next(current, isAdvancedMode: false));

            // Reverse traversal in Simple mode
            for (int i = simple.Count - 2; i >= 0; i--)
            {
                string? prev = WorkspaceNavigationOrder.Previous(current, isAdvancedMode: false);
                Assert.Equal(simple[i], prev);
                current = prev!;
            }
            Assert.Null(WorkspaceNavigationOrder.Previous(current, isAdvancedMode: false));
        }

        [Theory]
        [InlineData("Navigation_Chat_Item", true)]
        [InlineData("Navigation_Create_Item", true)]
        [InlineData("Navigation_Projects_Item", true)]
        [InlineData("Navigation_ModelHub_Item", true)]
        [InlineData("Navigation_ModelRouting_Item", true)]
        [InlineData("Navigation_Lifecycle_Item", true)]
        [InlineData("Navigation_Workflows_Item", true)]
        [InlineData("Navigation_Tools_Item", true)]
        [InlineData("Navigation_Observability_Item", true)]
        [InlineData("Navigation_Updates_Item", true)]
        [InlineData("Navigation_Runtime_Item", true)]
        [InlineData("Navigation_Settings_Item", true)]
        [InlineData("Navigation_Help_Item", true)]
        [InlineData("Navigation_Unknown_Item", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsWorkspaceAutomationId_Identifies_Known_Ids(string? automationId, bool expected)
        {
            Assert.Equal(expected, WorkspaceNavigationOrder.IsWorkspaceAutomationId(automationId));
        }
    }
}
