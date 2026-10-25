using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// First-class operational surface for Skills/permissions, outbound MCP servers, Automations
    /// and Workflow Builder. It deliberately calls the existing server-owned registries/engines;
    /// no second permission, trigger or task execution path exists in the desktop client.
    /// </summary>
    public sealed class ToolsAutomationView : UserControl
    {
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };

        /// <summary>Switches being set back to the server's position; their Toggled handlers ignore that change.</summary>
        private readonly HashSet<ToggleSwitch> _switchesBeingReverted = new();
        private readonly ProgressBar _busy = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        private readonly StackPanel _skills = new StackPanel { Spacing = 8 };
        private readonly StackPanel _mcp = new StackPanel { Spacing = 8 };
        private readonly StackPanel _automations = new StackPanel { Spacing = 8 };
        private readonly StackPanel _workflows = new StackPanel { Spacing = 8 };

        private readonly ComboBox _mcpPreset = new ComboBox { PlaceholderText = "Preset (optional)", MinWidth = 260 };
        private readonly TextBox _mcpName = new TextBox { PlaceholderText = "MCP name", MinWidth = 180 };
        private readonly TextBox _mcpCommand = new TextBox { PlaceholderText = "Command", MinWidth = 220 };
        private readonly TextBox _mcpArgs = new TextBox { PlaceholderText = "Arguments", MinWidth = 320 };

        private readonly TextBox _automationName = new TextBox { PlaceholderText = "Automation name", MinWidth = 220 };
        private readonly ComboBox _triggerType = new ComboBox { MinWidth = 230 };
        private readonly TextBox _triggerJson = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        private readonly TextBox _automationGoal = new TextBox { PlaceholderText = "Agent goal template; trigger placeholders allowed" };
        private readonly TextBox _automationRole = new TextBox { PlaceholderText = "Optional agent role" };

        private readonly TextBox _workflowName = new TextBox { PlaceholderText = "Workflow name", MinWidth = 220 };
        private readonly TextBox _workflowDescription = new TextBox { PlaceholderText = "Optional description" };
        private readonly TextBox _workflowStepsJson = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150,
            PlaceholderText = "JSON array of workflow steps"
        };

        public ToolsAutomationView()
        {
            foreach (string trigger in AutomationTriggerType.All.OrderBy(x => x, StringComparer.Ordinal))
                _triggerType.Items.Add(new ComboBoxItem { Content = TriggerLabel(trigger), Tag = trigger });
            _triggerType.SelectedIndex = 0;
            _triggerType.SelectionChanged += (_, _) => PopulateTriggerExample();
            PopulateTriggerExample();

            _workflowStepsJson.Text = "[\n  {\"name\":\"Plan\",\"goal\":\"Plan {{input}}\",\"role\":\"reasoning\",\"dependsOnStepIndexes\":[]},\n  {\"name\":\"Execute\",\"goal\":\"Execute the approved plan for {{input}}\",\"role\":\"coding\",\"dependsOnStepIndexes\":[0]}\n]";

            foreach (McpServerPreset preset in McpServerPresetCatalog.All)
                _mcpPreset.Items.Add(new ComboBoxItem { Content = preset.DisplayName, Tag = preset });
            _mcpPreset.SelectionChanged += (_, _) =>
            {
                if (_mcpPreset.SelectedItem is ComboBoxItem { Tag: McpServerPreset preset })
                {
                    _mcpName.Text = preset.Name;
                    _mcpCommand.Text = preset.Command;
                    _mcpArgs.Text = preset.Args;
                }
            };
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAllAsync(); };
        }

        public async void OnShown() => await RefreshAllAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(24),
                MaxWidth = 1100,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            var titleRow = new Grid { ColumnSpacing = 8 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = "Tools & Automations", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            titleRow.Children.Add(title);
            var refresh = new Button { Content = "Refresh all" };
            refresh.Click += async (_, _) => await RefreshAllAsync();
            Grid.SetColumn(refresh, 1); titleRow.Children.Add(refresh);
            body.Children.Add(titleRow);
            body.Children.Add(new TextBlock
            {
                Text = "Inspect skill permissions, connect MCP tools, create bounded triggers and compose reusable task-graph workflows.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            body.Children.Add(_busy);
            body.Children.Add(_status);
            body.Children.Add(Section("SKILLS & PERMISSIONS", BuildSkillsSection()));
            body.Children.Add(Section("MCP SERVERS", BuildMcpSection()));
            body.Children.Add(Section("AUTOMATIONS", BuildAutomationSection()));
            body.Children.Add(Section("WORKFLOW BUILDER", BuildWorkflowSection()));
            return scroll;
        }

        private UIElement BuildSkillsSection()
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "Permissions are enforced by the Core. This view shows the exact declared matrix and lets you explicitly edit it; server validation remains authoritative.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
            panel.Children.Add(_skills);
            return panel;
        }

        private UIElement BuildMcpSection()
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "Pick a known server to fill in the launch command, or type your own below. A server that needs a folder or file still needs that one path edited in Arguments.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72
            });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_mcpPreset, "MCP server preset");
            panel.Children.Add(_mcpPreset);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(_mcpName); row.Children.Add(_mcpCommand); row.Children.Add(_mcpArgs);
            var add = new Button { Content = "Add MCP server" };
            add.Click += async (_, _) => await AddMcpAsync();
            row.Children.Add(add);
            panel.Children.Add(row);
            panel.Children.Add(_mcp);
            return panel;
        }

        private UIElement BuildAutomationSection()
        {
            var panel = new StackPanel { Spacing = 10 };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_triggerType, "Trigger type");
            top.Children.Add(_automationName); top.Children.Add(_triggerType);
            panel.Children.Add(top);
            panel.Children.Add(new TextBlock { Text = "Trigger configuration JSON", FontSize = 12, Opacity = 0.7 });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_triggerJson, "Trigger JSON");
            panel.Children.Add(_triggerJson);
            panel.Children.Add(_automationGoal);
            panel.Children.Add(_automationRole);
            var create = new Button { Content = "Create automation" };
            create.Click += async (_, _) => await CreateAutomationAsync();
            panel.Children.Add(create);
            panel.Children.Add(new TextBlock
            {
                Text = "Available trigger types are sourced from the shared contract. Malformed/unsafe definitions are rejected by the server before persistence.",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7
            });
            panel.Children.Add(_automations);
            return panel;
        }

        private UIElement BuildWorkflowSection()
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(_workflowName);
            panel.Children.Add(_workflowDescription);
            panel.Children.Add(new TextBlock
            {
                Text = "Steps are an explicit JSON array of {name, goal, role, dependsOnStepIndexes}. Dependencies may only point backward; the Core validates the DAG before saving.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
            panel.Children.Add(_workflowStepsJson);
            var create = new Button { Content = "Create workflow template" };
            create.Click += async (_, _) => await CreateWorkflowAsync();
            panel.Children.Add(create);
            panel.Children.Add(_workflows);
            return panel;
        }

        private async Task RefreshAllAsync()
        {
            SetBusy(true, "Refreshing tools and automation state…");
            try
            {
                await RefreshSkillsAsync();
                await RefreshMcpAsync();
                await RefreshAutomationsAsync();
                await RefreshWorkflowsAsync();
                _status.Text = "Tools and automation state refreshed.";
            }
            catch (Exception ex) { _status.Text = "Refresh failed: " + ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task RefreshSkillsAsync()
        {
            List<SkillManifest> skills = await SkillsClient.Current.ListAsync(CancellationToken.None);
            _skills.Children.Clear();
            foreach (SkillManifest skill in skills.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var row = ThreeColumnRow();
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock
                {
                    Text = (string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.Name : skill.DisplayName)
                        + " · " + skill.Source + " · v" + skill.Version,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                text.Children.Add(new TextBlock
                {
                    Text = PermissionSummary(skill.Permissions) + " · tools: " + (skill.AllowedTools.Count == 0 ? "none" : string.Join(", ", skill.AllowedTools)),
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72
                });
                Grid.SetColumn(text, 0); row.Children.Add(text);

                var toggle = new ToggleSwitch { IsOn = skill.Enabled, OnContent = "Enabled", OffContent = "Disabled" };
                toggle.Toggled += async (_, _) =>
                {
                    if (_switchesBeingReverted.Contains(toggle)) return;
                    bool requested = toggle.IsOn;
                    try { await SkillsClient.Current.SetEnabledAsync(skill.Id, requested, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        ToggleRecovery recovered = await SwitchRevert.ApplyAsync(toggle, requested, _switchesBeingReverted,
                            async ct => (await SkillsClient.Current.ListAsync(ct)).FirstOrDefault(item => item.Id == skill.Id)?.Enabled);
                        _status.Text = SwitchRevert.Explain("this skill", requested, ex.Message, recovered);
                    }
                };
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle);

                var permissions = new Button { Content = "Permissions…" };
                permissions.Click += async (_, _) => await EditSkillPermissionsAsync(skill);
                Grid.SetColumn(permissions, 2); row.Children.Add(permissions);
                _skills.Children.Add(row);
            }
            if (skills.Count == 0) _skills.Children.Add(new TextBlock { Text = "No skills registered." });
        }

        private async Task EditSkillPermissionsAsync(SkillManifest skill)
        {
            var panel = new StackPanel { Spacing = 6 };
            var read = PermissionToggle("Read files", skill.Permissions.ReadFiles, panel);
            var write = PermissionToggle("Write files", skill.Permissions.WriteFiles, panel);
            var delete = PermissionToggle("Delete files", skill.Permissions.DeleteFiles, panel);
            var shell = PermissionToggle("Shell", skill.Permissions.Shell, panel);
            var network = PermissionToggle("Network", skill.Permissions.Network, panel);
            var browser = PermissionToggle("Browser", skill.Permissions.Browser, panel);
            var computer = PermissionToggle("Computer Use", skill.Permissions.ComputerUse, panel);
            var processes = PermissionToggle("External processes", skill.Permissions.ExternalProcesses, panel);
            panel.Children.Add(new TextBlock
            {
                Text = "Increasing privilege is explicit and still subject to server-side policy validation.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72
            });
            var dialog = new ContentDialog
            {
                Title = "Skill permissions · " + (string.IsNullOrWhiteSpace(skill.DisplayName) ? skill.Name : skill.DisplayName),
                Content = panel,
                PrimaryButtonText = "Save permissions",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var requested = new SkillPermissions
            {
                ReadFiles = read.IsOn,
                WriteFiles = write.IsOn,
                DeleteFiles = delete.IsOn,
                Shell = shell.IsOn,
                Network = network.IsOn,
                Browser = browser.IsOn,
                ComputerUse = computer.IsOn,
                ExternalProcesses = processes.IsOn
            };
            try
            {
                // Built-in skills (the vast majority shown here) can never go through
                // CreateOrUpdateAsync - that path is for authoring whole user skills and
                // deliberately refuses any id that collides with a built-in one. SetPermissionsAsync
                // stores a permission override keyed by id instead, so it works uniformly for
                // built-in and user/imported skills alike without that collision.
                await SkillsClient.Current.SetPermissionsAsync(skill.Id, requested, CancellationToken.None);
                _status.Text = "Skill permissions saved after Core validation.";
                await RefreshSkillsAsync();
            }
            catch (Exception ex) { _status.Text = "Permission update rejected: " + ex.Message; }
        }

        private async Task RefreshMcpAsync()
        {
            List<McpServerStatus> servers = await McpServersClient.Current.GetAllAsync(CancellationToken.None);
            _mcp.Children.Clear();
            foreach (McpServerStatus server in servers)
            {
                var row = ThreeColumnRow();
                var text = new TextBlock
                {
                    Text = server.Name + " · " + (server.Connected ? "connected" : "not connected")
                        + " · " + server.ToolCount + " tool(s)"
                        + (string.IsNullOrWhiteSpace(server.LastError) ? string.Empty : " · " + server.LastError),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(text, 0); row.Children.Add(text);
                var toggle = new ToggleSwitch { IsOn = server.Enabled };
                toggle.Toggled += async (_, _) =>
                {
                    if (_switchesBeingReverted.Contains(toggle)) return;
                    bool requested = toggle.IsOn;
                    try { await McpServersClient.Current.SetEnabledAsync(server.Id, requested, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        ToggleRecovery recovered = await SwitchRevert.ApplyAsync(toggle, requested, _switchesBeingReverted,
                            async ct => (await McpServersClient.Current.GetAllAsync(ct)).FirstOrDefault(item => item.Id == server.Id)?.Enabled);
                        _status.Text = SwitchRevert.Explain("this MCP server", requested, ex.Message, recovered);
                        return;
                    }
                    // The switch is saved by now, so a failed list refresh is reported on its own, not as a failed switch.
                    try { await RefreshMcpAsync(); }
                    catch (Exception ex) { _status.Text = "MCP switch saved, but the list could not be refreshed: " + ex.Message; }
                };
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
                var remove = new Button { Content = "Remove" };
                remove.Click += async (_, _) =>
                {
                    try
                    {
                        // Removing a registration stops its tools for every agent, so it is confirmed first.
                        await ConfirmedAction.RunIfConfirmedAsync(
                            _ => ConfirmDestructiveAsync("Remove this MCP server?",
                                "Its tools stop being available to agents. This cannot be undone.", "Remove server"),
                            async ct =>
                            {
                                await McpServersClient.Current.RemoveAsync(server.Id, ct);
                                await RefreshMcpAsync();
                            },
                            CancellationToken.None);
                    }
                    catch (Exception ex) { _status.Text = "MCP remove failed: " + ex.Message; }
                };
                Grid.SetColumn(remove, 2); row.Children.Add(remove);
                _mcp.Children.Add(row);
            }
            if (servers.Count == 0) _mcp.Children.Add(new TextBlock { Text = "No outbound MCP servers configured." });
        }

        private async Task AddMcpAsync()
        {
            string name = (_mcpName.Text ?? string.Empty).Trim();
            string command = (_mcpCommand.Text ?? string.Empty).Trim();
            if (name.Length == 0 || command.Length == 0) { _status.Text = "MCP name and command are required."; return; }
            try
            {
                await McpServersClient.Current.AddAsync(name, command, (_mcpArgs.Text ?? string.Empty).Trim(), CancellationToken.None);
                _mcpName.Text = _mcpCommand.Text = _mcpArgs.Text = string.Empty;
                _mcpPreset.SelectedItem = null;
                await RefreshMcpAsync();
                _status.Text = "MCP server added; connection status refreshed.";
            }
            catch (Exception ex) { _status.Text = "MCP add failed: " + ex.Message; }
        }

        private async Task RefreshAutomationsAsync()
        {
            AutomationListResponse response = await AutomationClient.Current.ListAsync(CancellationToken.None);
            _automations.Children.Clear();
            foreach (AutomationDto automation in response.Automations.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = ThreeColumnRow();
                var text = new TextBlock
                {
                    Text = automation.Name + " · " + automation.TriggerType
                        + (automation.LastTriggeredUtc.HasValue ? " · last " + automation.LastTriggeredUtc.Value.ToLocalTime().ToString("g") : " · never triggered")
                        + (string.IsNullOrWhiteSpace(automation.LastResult) ? string.Empty : " · " + automation.LastResult),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(text, 0); row.Children.Add(text);
                var toggle = new ToggleSwitch { IsOn = automation.Enabled };
                toggle.Toggled += async (_, _) =>
                {
                    if (_switchesBeingReverted.Contains(toggle)) return;
                    bool requested = toggle.IsOn;
                    try { await AutomationClient.Current.SetEnabledAsync(automation.Id, requested, CancellationToken.None); }
                    catch (Exception ex)
                    {
                        ToggleRecovery recovered = await SwitchRevert.ApplyAsync(toggle, requested, _switchesBeingReverted,
                            async ct => (await AutomationClient.Current.ListAsync(ct)).Automations.FirstOrDefault(item => item.Id == automation.Id)?.Enabled);
                        _status.Text = SwitchRevert.Explain("this automation", requested, ex.Message, recovered);
                    }
                };
                Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
                var delete = new Button { Content = "Delete" };
                delete.Click += async (_, _) =>
                {
                    try
                    {
                        await ConfirmedAction.RunIfConfirmedAsync(
                            _ => ConfirmDestructiveAsync("Delete this automation?",
                                "Its trigger stops firing. This cannot be undone.", "Delete automation"),
                            async ct =>
                            {
                                await AutomationClient.Current.DeleteAsync(automation.Id, ct);
                                await RefreshAutomationsAsync();
                            },
                            CancellationToken.None);
                    }
                    catch (Exception ex) { _status.Text = "Automation delete failed: " + ex.Message; }
                };
                Grid.SetColumn(delete, 2); row.Children.Add(delete);
                _automations.Children.Add(row);
            }
            if (response.Automations.Count == 0) _automations.Children.Add(new TextBlock { Text = "No automations yet." });
        }

        private async Task CreateAutomationAsync()
        {
            string name = (_automationName.Text ?? string.Empty).Trim();
            string trigger = (_triggerType.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            string triggerJson = (_triggerJson.Text ?? string.Empty).Trim();
            string goal = (_automationGoal.Text ?? string.Empty).Trim();
            if (name.Length == 0 || trigger.Length == 0 || triggerJson.Length == 0 || goal.Length == 0)
            { _status.Text = "Automation name, trigger config and goal are required."; return; }
            try
            {
                // Parse locally only to catch obvious syntax errors; semantic validation is Core-owned.
                using (JsonDocument.Parse(triggerJson)) { }
                await AutomationClient.Current.CreateAsync(new CreateAutomationRequest
                {
                    Name = name,
                    TriggerType = trigger,
                    TriggerConfigJson = triggerJson,
                    ActionType = AutomationActionType.CreateAgentTask,
                    ActionConfigJson = JsonSerializer.Serialize(new CreateAgentTaskActionConfig
                    {
                        GoalTemplate = goal,
                        Role = string.IsNullOrWhiteSpace(_automationRole.Text) ? null : _automationRole.Text.Trim()
                    })
                }, CancellationToken.None);
                _automationName.Text = _automationGoal.Text = _automationRole.Text = string.Empty;
                await RefreshAutomationsAsync();
                _status.Text = "Automation created after Core validation.";
            }
            catch (Exception ex) { _status.Text = "Automation create failed: " + ex.Message; }
        }

        private async Task RefreshWorkflowsAsync()
        {
            List<WorkflowDto> workflows = await WorkflowClient.Current.ListAsync(CancellationToken.None);
            _workflows.Children.Clear();
            foreach (WorkflowDto workflow in workflows.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = ThreeColumnRow();
                var text = new TextBlock
                {
                    Text = workflow.Name + " · " + workflow.Steps.Count + " step(s)"
                        + (string.IsNullOrWhiteSpace(workflow.Description) ? string.Empty : " · " + workflow.Description),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(text, 0); row.Children.Add(text);
                var run = new Button { Content = "Run…" };
                run.Click += async (_, _) => await RunWorkflowAsync(workflow);
                Grid.SetColumn(run, 1); row.Children.Add(run);
                var delete = new Button { Content = "Delete" };
                delete.Click += async (_, _) =>
                {
                    try
                    {
                        // Same rule as the Workflows workspace: an earlier immutable version is kept, a versioned successor blocks it.
                        await ConfirmedAction.RunIfConfirmedAsync(
                            _ => ConfirmDestructiveAsync("Delete current workflow version?",
                                "Delete the current version of “" + workflow.Name + "”? Earlier immutable versions remain. A version with a successor cannot be deleted.",
                                "Delete current leaf"),
                            async ct =>
                            {
                                await WorkflowClient.Current.DeleteAsync(workflow.Id, ct);
                                await RefreshWorkflowsAsync();
                            },
                            CancellationToken.None);
                    }
                    catch (Exception ex) { _status.Text = "Workflow delete failed: " + ex.Message; }
                };
                Grid.SetColumn(delete, 2); row.Children.Add(delete);
                _workflows.Children.Add(row);
            }
            if (workflows.Count == 0) _workflows.Children.Add(new TextBlock { Text = "No workflow templates yet." });
        }

        private async Task CreateWorkflowAsync()
        {
            string name = (_workflowName.Text ?? string.Empty).Trim();
            if (name.Length == 0) { _status.Text = "Workflow name is required."; return; }
            try
            {
                List<WorkflowStepTemplate>? steps = JsonSerializer.Deserialize<List<WorkflowStepTemplate>>(
                    _workflowStepsJson.Text ?? string.Empty,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (steps == null || steps.Count == 0) throw new InvalidOperationException("At least one workflow step is required.");
                await WorkflowClient.Current.CreateAsync(new CreateWorkflowRequest
                {
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(_workflowDescription.Text) ? null : _workflowDescription.Text.Trim(),
                    Steps = steps
                }, CancellationToken.None);
                _workflowName.Text = _workflowDescription.Text = string.Empty;
                await RefreshWorkflowsAsync();
                _status.Text = "Workflow template created after dependency validation.";
            }
            catch (Exception ex) { _status.Text = "Workflow create failed: " + ex.Message; }
        }

        /// <summary>The confirmation every delete or removal in this view goes through. Declining, or a dialog that cannot open, deletes nothing.</summary>
        private async Task<bool> ConfirmDestructiveAsync(string title, string content, string primaryText)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            try
            {
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception ex)
            {
                // Another dialog may already be open, or the view may not be attached yet. Nothing is deleted, and the view says so.
                Log.Write("Confirmation could not be shown: " + ex.Message);
                _status.Text = "Couldn't open the confirmation; nothing was deleted.";
                return false;
            }
        }

        private async Task RunWorkflowAsync(WorkflowDto workflow)
        {
            var input = new TextBox { PlaceholderText = "Optional {{input}} value", MinWidth = 420 };
            var dialog = new ContentDialog
            {
                Title = "Run workflow · " + workflow.Name,
                Content = input,
                PrimaryButtonText = "Create task graph",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                WorkflowRunResult result = await WorkflowClient.Current.RunAsync(workflow.Id, input.Text, CancellationToken.None);
                _status.Text = "Workflow created " + result.CreatedTaskIds.Count + " real AgentTask(s): " + string.Join(", ", result.CreatedTaskIds);
            }
            catch (Exception ex) { _status.Text = "Workflow run failed: " + ex.Message; }
        }

        private void PopulateTriggerExample()
        {
            string trigger = (_triggerType.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            _triggerJson.Text = trigger switch
            {
                AutomationTriggerType.Schedule => "{\"intervalSeconds\":3600}",
                AutomationTriggerType.FileCreated => "{\"folderPath\":\"C:\\\\Data\",\"filter\":\"*.json\"}",
                AutomationTriggerType.GitCommit => "{\"repositoryPath\":\"C:\\\\repo\",\"branch\":\"\"}",
                AutomationTriggerType.ModelDownloadCompleted => "{\"fileNameContains\":\"\"}",
                AutomationTriggerType.ComponentHealthChanged => "{\"componentId\":\"comfyui\"}",
                AutomationTriggerType.TaskCompleted => "{\"role\":\"\",\"status\":\"completed\"}",
                _ => "{}"
            };
        }

        private static string TriggerLabel(string trigger) => trigger.Replace('_', ' ');

        private static ToggleSwitch PermissionToggle(string label, bool value, Panel panel)
        {
            var toggle = new ToggleSwitch { Header = label, IsOn = value };
            panel.Children.Add(toggle);
            return toggle;
        }

        private static string PermissionSummary(SkillPermissions p)
        {
            var granted = new List<string>();
            if (p.ReadFiles) granted.Add("read-files");
            if (p.WriteFiles) granted.Add("write-files");
            if (p.DeleteFiles) granted.Add("delete-files");
            if (p.Shell) granted.Add("shell");
            if (p.Network) granted.Add("network");
            if (p.Browser) granted.Add("browser");
            if (p.ComputerUse) granted.Add("computer-use");
            if (p.ExternalProcesses) granted.Add("external-processes");
            return granted.Count == 0 ? "permissions: none" : "permissions: " + string.Join(", ", granted);
        }

        private static Grid ThreeColumnRow()
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            return row;
        }

        private static Border Section(string title, UIElement child)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            panel.Children.Add(child);
            return new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel };
        }

        private void SetBusy(bool busy, string? text = null)
        {
            _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(text)) _status.Text = text;
        }
    }
}
