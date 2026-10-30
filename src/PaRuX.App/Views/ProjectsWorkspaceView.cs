using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// Durable Projects + Artifact Workspace surface. Project identity/tasks live in Project
    /// Manager/Context Graph; artifact identity/versioning lives in ArtifactStore. Filesystem-
    /// capable tasks receive an explicit desktop-only repository authority instead of borrowing a
    /// global coding path. Coding mutates only its isolated worktree; specialist roles receive the
    /// same Project repository only as a read-only evidence authority.
    /// </summary>
    public sealed class ProjectsWorkspaceView : UserControl
    {
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _active = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _repository = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.76 };
        private readonly Button _chooseRepository = new() { Content = "Choose Project repository…" };
        private readonly Button _clearRepository = new() { Content = "Clear session repository" };
        private readonly TextBox _goal = new() { PlaceholderText = "Goal for a new durable Project" };
        private readonly TextBlock _newProjectRepository = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.76 };
        private readonly Button _chooseNewProjectRepository = new() { Content = "Choose repository for new Project…" };
        private readonly Button _clearNewProjectRepository = new() { Content = "Clear", IsEnabled = false };
        private string? _newProjectRepositoryRoot;
        private readonly StackPanel _projects = new() { Spacing = 8 };
        private readonly StackPanel _projectArtifacts = new() { Spacing = 8 };
        private readonly StackPanel _allArtifacts = new() { Spacing = 8 };
        private readonly StackPanel _versions = new() { Spacing = 6 };
        private readonly MediaGalleryView _mediaGallery = new();
        private CancellationTokenSource? _cts;

        public ProjectsWorkspaceView()
        {
            Content = BuildUi();
            Loaded += async (_, _) => { if (Visibility == Visibility.Visible) await RefreshAllAsync(); };
            Unloaded += (_, _) => _cts?.Cancel();
            ProjectWorkspaceSession.Current.Changed += (_, _) => DispatcherQueue.TryEnqueue(async () => await RefreshAllAsync());
        }

        public async void OnShown() => await RefreshAllAsync();

        private UIElement BuildUi()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel
            {
                Spacing = 18,
                Margin = new Thickness(24),
                MaxWidth = 1050,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            scroll.Content = body;

            body.Children.Add(new TextBlock { Text = "Projects & Artifacts", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock
            {
                Text = "Select a durable Project as the active workspace. Coding/Binary tasks and read-only repository-aware specialists receive explicit task-scoped repository authority; selecting another Project never reuses the previous Project's repository implicitly.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.78
            });
            body.Children.Add(_active);

            var repoActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            _chooseRepository.Click += async (_, _) => await ChooseRepositoryAsync();
            _clearRepository.Click += (_, _) => ProjectWorkspaceSession.Current.SetRepositoryRoot(null);
            repoActions.Children.Add(_chooseRepository);
            repoActions.Children.Add(_clearRepository);
            var repoPanel = new StackPanel { Spacing = 6 };
            repoPanel.Children.Add(_repository);
            repoPanel.Children.Add(repoActions);
            body.Children.Add(Section("PROJECT REPOSITORY · DESKTOP ONLY", repoPanel));

            body.Children.Add(_status);

            var create = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            create.Children.Add(_goal);
            var createButton = new Button { Content = "Create Project" };
            createButton.Click += async (_, _) => await CreateProjectAsync();
            create.Children.Add(createButton);
            var createPanel = new StackPanel { Spacing = 8 };
            createPanel.Children.Add(create);
            _chooseNewProjectRepository.Click += async (_, _) => await ChooseNewProjectRepositoryAsync();
            _clearNewProjectRepository.Click += (_, _) => SetNewProjectRepository(null);
            var newProjectRepositoryActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            newProjectRepositoryActions.Children.Add(_chooseNewProjectRepository);
            newProjectRepositoryActions.Children.Add(_clearNewProjectRepository);
            createPanel.Children.Add(_newProjectRepository);
            createPanel.Children.Add(newProjectRepositoryActions);
            body.Children.Add(Section("PROJECTS", createPanel));
            body.Children.Add(_projects);

            _mediaGallery.ItemReusedAsEditSource += (_, item) =>
            {
                _status.Text = "Selected '" + item.Title + "' as source for Image Edit in Create Workspace.";
            };
            _mediaGallery.ItemDeleted += (_, _) =>
            {
                _ = RefreshArtifactsAsync(CancellationToken.None);
            };
            body.Children.Add(Section("MEDIA OUTPUT GALLERY · THUMBNAILS & ACTIONS", _mediaGallery));
            body.Children.Add(Section("ARTIFACTS IN ACTIVE PROJECT", _projectArtifacts));
            body.Children.Add(CollapsibleSection("All artifacts · link explicitly", _allArtifacts, "Projects_AllArtifacts_Expander"));
            body.Children.Add(CollapsibleSection("Selected artifact version history", _versions, "Projects_Versions_Expander"));
            return scroll;
        }

        private async Task RefreshAllAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            try
            {
                await RefreshProjectsAsync(ct);
                await _mediaGallery.RefreshAsync();
                await RefreshArtifactsAsync(ct);
                _status.Text = "Project workspace refreshed.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status.Text = "Project workspace refresh failed: " + ex.Message; }
        }

        private async Task RefreshProjectsAsync(CancellationToken ct)
        {
            List<ProjectDto> projects = await ProjectManagerClient.Current.ListProjectsAsync(ct);
            string? activeId = ProjectWorkspaceSession.Current.ProjectId;
            _active.Text = activeId == null
                ? "Active project: none — new outputs remain unscoped until you explicitly select/link them."
                : "Active project: " + ProjectWorkspaceSession.Current.Goal + " · " + activeId;

            string? repository = ProjectWorkspaceSession.Current.RepositoryRoot;
            _repository.Text = activeId == null
                ? "Select a Project before assigning a repository."
                : string.IsNullOrWhiteSpace(repository)
                    ? "Session repository: none. Existing per-task assignments remain durable; choose a repository to authorize unassigned coding/binary tasks and read-only repository specialists in this Project."
                    : "Session repository: " + repository;
            _chooseRepository.IsEnabled = activeId != null;
            _clearRepository.IsEnabled = activeId != null && !string.IsNullOrWhiteSpace(repository);

            _projects.Children.Clear();
            foreach (ProjectDto project in projects)
            {
                var row = ActionRow(project.Goal + " · " + project.TaskIds.Count + " task(s) · updated " + project.UpdatedUtc.ToLocalTime().ToString("g"));
                var select = new Button { Content = project.Id == activeId ? "Active" : "Select" };
                select.IsEnabled = project.Id != activeId;
                select.Click += (_, _) => ProjectWorkspaceSession.Current.Select(project.Id, project.Goal);
                Grid.SetColumn(select, 1); row.Children.Add(select);
                _projects.Children.Add(row);
            }
            if (projects.Count == 0) _projects.Children.Add(new TextBlock { Text = "No durable Projects yet." });
        }

        private async Task CreateProjectAsync()
        {
            string goal = (_goal.Text ?? string.Empty).Trim();
            if (goal.Length == 0)
            {
                _status.Text = "Write a goal for the new Project first.";
                return;
            }
            try
            {
                string? repositoryRoot = _newProjectRepositoryRoot;
                ProjectDto created = await ProjectManagerClient.Current.CreateAsync(goal, repositoryRoot, CancellationToken.None);
                _goal.Text = string.Empty;
                ProjectWorkspaceSession.Current.Select(created.Id, created.Goal);
                SetNewProjectRepository(null);
                if (repositoryRoot != null) ProjectWorkspaceSession.Current.SetRepositoryRoot(repositoryRoot);
                _status.Text = "Created and selected Project \"" + (created.Goal.Length > 60 ? created.Goal[..60] + "..." : created.Goal)
                    + "\". PaRuX planned its tasks; they are listed as queued in Settings > Agents"
                    + (repositoryRoot == null
                        ? ". Choose its repository if the generated DAG contains coding/binary work or repository-aware specialist tasks."
                        : ". Its repository authority was saved before the generated tasks were scheduled.");
            }
            catch (Exception ex) { _status.Text = "Project creation failed: " + ex.Message; }
        }

        private async Task ChooseNewProjectRepositoryAsync()
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                SetNewProjectRepository(Path.GetFullPath(folder.Path));
            }
            catch (Exception ex)
            {
                _status.Text = "Couldn't select the new Project repository: " + ex.Message;
            }
        }

        private void SetNewProjectRepository(string? repositoryRoot)
        {
            _newProjectRepositoryRoot = repositoryRoot;
            _newProjectRepository.Text = string.IsNullOrWhiteSpace(repositoryRoot)
                ? "New Project repository: none. Choose a Git working tree before creation to authorize repository tasks before they can run."
                : "New Project repository: " + repositoryRoot;
            _clearNewProjectRepository.IsEnabled = !string.IsNullOrWhiteSpace(repositoryRoot);
        }

        private async Task ChooseRepositoryAsync()
        {
            string? projectId = ProjectWorkspaceSession.Current.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                _status.Text = "Select a Project first.";
                return;
            }

            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;

                string root = Path.GetFullPath(folder.Path);
                ProjectWorkspaceSession.Current.SetRepositoryRoot(root);
                await ApplyRepositoryToActiveProjectTasksAsync(projectId, root).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Write("Project repository assignment failed: " + ex.Message);
                _status.Text = "Couldn't assign the Project repository: " + ex.Message;
            }
        }

        private async Task ApplyRepositoryToActiveProjectTasksAsync(string projectId, string repositoryRoot)
        {
            List<ProjectDto> projects = await ProjectManagerClient.Current.ListProjectsAsync(CancellationToken.None);
            ProjectDto? project = projects.FirstOrDefault(value => string.Equals(value.Id, projectId, StringComparison.Ordinal));
            if (project == null) throw new InvalidOperationException("The active Project no longer exists.");

            var repositoryTasks = new List<AgentTaskDto>();
            foreach (string taskId in project.TaskIds)
            {
                AgentTaskDto task = await AgentOrchestratorClient.Current.GetAsync(taskId, CancellationToken.None);
                if (task.Role is "coding" or "binary-analysis" or "planner" or "review" or "documentation" or "testing" or "security")
                    repositoryTasks.Add(task);
            }

            if (repositoryTasks.Any(task => task.Role == "coding"))
            {
                bool gitRepository = Directory.Exists(Path.Combine(repositoryRoot, ".git"))
                    || File.Exists(Path.Combine(repositoryRoot, ".git"));
                if (!gitRepository)
                    throw new InvalidOperationException("This Project contains coding tasks, so its repository must be a Git working tree. PaRuX will not manufacture a baseline over an occupied non-Git folder.");
            }

            int assigned = 0, already = 0, conflicts = 0;
            foreach (AgentTaskDto task in repositoryTasks)
            {
                AgentRepositorySetDto existing = await AgentOrchestratorClient.Current
                    .GetRepositorySetAsync(task.Id, CancellationToken.None);
                AgentRepositoryAuthorityDto? primary = existing.Repositories.FirstOrDefault(repository => repository.IsPrimary);
                if (primary != null)
                {
                    string current = Path.GetFullPath(primary.WorkspaceRoot);
                    if (string.Equals(current, repositoryRoot, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) already++;
                    else conflicts++;
                    continue;
                }

                List<AddAgentRepositoryAuthorityRequest> additional = existing.Repositories
                    .Where(repository => !repository.IsPrimary)
                    .Select(repository => new AddAgentRepositoryAuthorityRequest
                    {
                        Alias = repository.Alias,
                        WorkspaceRoot = repository.WorkspaceRoot
                    })
                    .ToList();
                await AgentOrchestratorClient.Current.ReplaceRepositorySetAsync(
                    task.Id, repositoryRoot, additional, CancellationToken.None);
                assigned++;
            }

            await AgentOrchestratorClient.Current.ScheduleAsync(CancellationToken.None);
            _status.Text = "Project repository applied: " + assigned + " task(s) newly authorized, "
                + already + " already matched"
                + (conflicts > 0 ? ", " + conflicts + " kept their existing immutable task authority" : string.Empty)
                + ". Coding receives isolated write authority; specialist repository tools remain read-only.";
        }

        private async Task RefreshArtifactsAsync(CancellationToken ct)
        {
            ArtifactListResponse all = await ArtifactWorkspaceClient.Current.ListAsync(null, ct);
            List<ArtifactDto> scoped = new();
            string? projectId = ProjectWorkspaceSession.Current.ProjectId;
            if (!string.IsNullOrWhiteSpace(projectId))
                scoped = await ProjectArtifactBridge.Current.ListForProjectAsync(projectId, ct);
            var scopedIds = new HashSet<string>(scoped.Select(a => a.Id), StringComparer.Ordinal);

            _projectArtifacts.Children.Clear();
            foreach (ArtifactDto artifact in scoped)
                _projectArtifacts.Children.Add(BuildArtifactRow(artifact, linked: true));
            if (scoped.Count == 0)
                _projectArtifacts.Children.Add(new TextBlock { Text = projectId == null ? "Select a Project to see scoped artifacts." : "No artifact is linked to this Project yet." });

            _allArtifacts.Children.Clear();
            foreach (ArtifactDto artifact in all.Artifacts.OrderByDescending(a => a.UpdatedUtc))
                _allArtifacts.Children.Add(BuildArtifactRow(artifact, scopedIds.Contains(artifact.Id)));
            if (all.Artifacts.Count == 0) _allArtifacts.Children.Add(new TextBlock { Text = "Artifact Workspace is empty." });
        }

        private UIElement BuildArtifactRow(ArtifactDto artifact, bool linked)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool isMedia = string.Equals(artifact.ArtifactType, "image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(artifact.ArtifactType, "video", StringComparison.OrdinalIgnoreCase);
            string iconPrefix = string.Equals(artifact.ArtifactType, "video", StringComparison.OrdinalIgnoreCase) ? "🎬 " : isMedia ? "🖼 " : string.Empty;

            var label = new Button
            {
                Content = iconPrefix + artifact.Title + " · " + artifact.ArtifactType + " · " + artifact.Status + " · v" + artifact.CurrentVersionNumber,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            label.Click += async (_, _) => await ShowVersionsAsync(artifact);
            Grid.SetColumn(label, 0); row.Children.Add(label);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (string.Equals(artifact.ArtifactType, "image", StringComparison.OrdinalIgnoreCase))
            {
                var reuse = new Button { Content = "Reuse as edit source" };
                reuse.Click += async (_, _) =>
                {
                    try
                    {
                        var versions = await ArtifactWorkspaceClient.Current.ListVersionsAsync(artifact.Id, CancellationToken.None);
                        var latest = versions.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                        if (latest != null && File.Exists(latest.ContentRef))
                        {
                            MediaEditSourceBridge.Current.SetEditSource(latest.ContentRef, artifact.Title);
                            _status.Text = "Selected '" + artifact.Title + "' as edit source for Create Workspace.";
                        }
                    }
                    catch (Exception ex) { _status.Text = "Reuse as edit source failed: " + ex.Message; }
                };
                actions.Children.Add(reuse);
            }
            if (!linked && !string.IsNullOrWhiteSpace(ProjectWorkspaceSession.Current.ProjectId))
            {
                var link = new Button { Content = "Link to active Project" };
                link.Click += async (_, _) => await LinkArtifactAsync(artifact);
                actions.Children.Add(link);
            }
            var status = new Button { Content = artifact.Status == "final" ? "Set draft" : "Set final" };
            status.Click += async (_, _) =>
            {
                try
                {
                    await ArtifactWorkspaceClient.Current.SetStatusAsync(artifact.Id, artifact.Status == "final" ? "draft" : "final", CancellationToken.None);
                    await RefreshArtifactsAsync(CancellationToken.None);
                }
                catch (Exception ex) { _status.Text = "Artifact status update failed: " + ex.Message; }
            };
            actions.Children.Add(status);
            Grid.SetColumn(actions, 1); row.Children.Add(actions);
            return row;
        }

        private async Task LinkArtifactAsync(ArtifactDto artifact)
        {
            string? projectId = ProjectWorkspaceSession.Current.ProjectId;
            if (string.IsNullOrWhiteSpace(projectId)) return;
            try
            {
                await ProjectArtifactBridge.Current.LinkAsync(projectId, artifact, CancellationToken.None);
                _status.Text = "Linked artifact " + artifact.Title + " to the active Project through Context Graph.";
                await RefreshArtifactsAsync(CancellationToken.None);
            }
            catch (Exception ex) { _status.Text = "Artifact link failed: " + ex.Message; }
        }

        private async Task ShowVersionsAsync(ArtifactDto artifact)
        {
            try
            {
                ArtifactVersionListResponse response = await ArtifactWorkspaceClient.Current.ListVersionsAsync(artifact.Id, CancellationToken.None);
                _versions.Children.Clear();
                _versions.Children.Add(new TextBlock { Text = artifact.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                foreach (ArtifactVersionDto version in response.Versions.OrderByDescending(v => v.VersionNumber))
                {
                    var row = ActionRow("v" + version.VersionNumber + " · " + version.CreatedUtc.ToLocalTime().ToString("g") + " · " + version.ContentRef + (string.IsNullOrWhiteSpace(version.Comment) ? string.Empty : " · " + version.Comment));
                    if (version.VersionNumber != artifact.CurrentVersionNumber)
                    {
                        var restore = new Button { Content = "Restore as new version" };
                        restore.Click += async (_, _) =>
                        {
                            try
                            {
                                await ArtifactWorkspaceClient.Current.RestoreAsync(artifact.Id, version.VersionNumber, CancellationToken.None);
                                _status.Text = "Restored v" + version.VersionNumber + " as a new head version; history was preserved.";
                                await RefreshArtifactsAsync(CancellationToken.None);
                                await ShowVersionsAsync(await ArtifactWorkspaceClient.Current.GetAsync(artifact.Id, CancellationToken.None));
                            }
                            catch (Exception ex) { _status.Text = "Artifact restore failed: " + ex.Message; }
                        };
                        Grid.SetColumn(restore, 1); row.Children.Add(restore);
                    }
                    _versions.Children.Add(row);
                }
            }
            catch (Exception ex) { _status.Text = "Version history failed: " + ex.Message; }
        }

        private static Grid ActionRow(string text)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(label, 0); row.Children.Add(label);
            return row;
        }

        private static Expander CollapsibleSection(string title, UIElement content, string automationId)
        {
            var expander = new Expander { Header = title, Content = content, IsExpanded = false };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(expander, automationId);
            return expander;
        }

        private static Border Section(string title, UIElement content)
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.72 });
            body.Children.Add(content);
            return new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = body };
        }
    }
}
