using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using PaRuX.UITests.Infrastructure;
using PaRuX.UITests.PageObjects;
using Xunit;

namespace PaRuX.UITests.Tests;

/// <summary>
/// BIC-09 S4 acceptance: a REAL Core-owned coding task, driven end to end through the UI with a real
/// local model. Submit /code in chat -> the coding-task card shows the plan and agent activity ->
/// the agent edits and runs the verifier in an isolated worktree -> the user reviews, clicks apply
/// and confirms -> the change lands in the disposable repository as an uncommitted edit.
///
/// Opt-in (HardwareE2E; excluded from the Regression filter) because it needs a provisioned
/// isolated data root (PARUX_REAL_CODING_ENV: data/, state/, localai/ with a runtime and a model
/// assigned to the standard role), a published layout (PARUX_REAL_CODING_APP) and an unlocked
/// desktop. It never touches an installed PaRuX, the user's data or the user's working tree: the
/// target repository is created fresh by the test under PARUX_REAL_CODING_ARTIFACTS.
/// Waits are bounded polls on AutomationIds; no coordinates, no fixed sleeps beyond the poll tick.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class RealCodingTaskTests
{
    private const string Goal =
        "In src/inventory.py the line_total function must give the 10 percent discount to orders of 10 or more units, "
        + "but an order of exactly 10 units currently gets no discount. Fix src/inventory.py without editing src/verify.py, "
        + "then run python src/verify.py to verify.";
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(15);

    [RealCodingFact, Trait("Category", "HardwareE2E"), Trait("Category", "RealCodingTask"), Trait("Category", "FlaUI")]
    public void Real_model_task_submitted_in_chat_is_planned_edited_verified_reviewed_and_applied_through_the_ui()
    {
        const string testName = nameof(Real_model_task_submitted_in_chat_is_planned_edited_verified_reviewed_and_applied_through_the_ui);
        string artifactsRoot = RealCodingProduct.RequireVariable(RealCodingProduct.ArtifactsVariable);
        string runDirectory = Path.Combine(artifactsRoot, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        string repository = Path.Combine(runDirectory, "repo");
        string baseline = CreateDisposableRepository(repository);
        string screenshots = Path.Combine(runDirectory, "ui");

        using var product = new RealCodingProduct(screenshots, repository);
        var timeline = new List<string>();
        void Mark(string text)
        {
            timeline.Add(DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + text);
            File.WriteAllLines(Path.Combine(runDirectory, "ui-timeline.txt"), timeline);
        }

        try
        {
            // 1. Submit through the composer exactly as a user would.
            // The app may restore another page; Chat is the home workspace, so go there explicitly.
            new MainWindowPage(product.MainWindow).Click("MainWindow_NewChat_Button", ControlType.Button);
            var chat = new ChatPage(product.MainWindow);
            Assert.Equal(Path.GetFileName(repository), chat.CodeFolderLabel.Name);
            chat.SendPrompt("/code " + Goal);
            Mark("submitted /code through Chat_Send_Button");

            // 2. The Core-owned task appears on the card (bounded wait; model/runtime may cold start).
            AutomationElement idText = UiWait.ById(product.MainWindow, "Chat_CodingTask_Id_Text", ControlType.Text);
            UiWait.For(() => (product.TryFindById("Chat_CodingTask_Id_Text")?.Properties.Name.ValueOrDefault ?? string.Empty).StartsWith("Task ", StringComparison.Ordinal),
                "Core task id on the card", TimeSpan.FromMinutes(3));
            idText = product.TryFindById("Chat_CodingTask_Id_Text")!;
            string taskPrefix = idText.Name.Replace("Task ", string.Empty, StringComparison.Ordinal).Trim();
            Assert.True(taskPrefix.Length >= 8, "task id text: " + idText.Name);
            Mark("task card visible: " + idText.Name);
            product.Capture("01-task-submitted");

            // 3. Observe plan / agent activity / edits / tests until the task is terminal.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool planShot = false, editShot = false;
            string status = string.Empty;
            DateTime deadline = DateTime.UtcNow + TaskTimeout;
            while (DateTime.UtcNow < deadline)
            {
                status = product.TryFindById("Chat_CodingTask_Status_Text")?.Properties.Name.ValueOrDefault ?? string.Empty;
                foreach (AutomationElement item in product.FindAllById("Chat_CodingTask_Activity_Item"))
                {
                    string text;
                    try { text = item.Properties.Name.ValueOrDefault ?? string.Empty; }
                    catch (FlaUI.Core.Exceptions.FlaUIException) { continue; } // card rebuilt between enumeration and read
                    if (seen.Add(text))
                    {
                        Mark("activity: " + (text.Length > 220 ? text[..220] : text));
                        if (!planShot && text.Contains("implementation plan", StringComparison.OrdinalIgnoreCase))
                        { product.Capture("02-plan-visible"); planShot = true; }
                        if (!editShot && text.Contains("replace_range", StringComparison.Ordinal))
                        { product.Capture("03-edit-visible"); editShot = true; }
                    }
                }
                if (status.StartsWith("Completed", StringComparison.Ordinal)
                    || status is "Verification failed" or "Failed" or "Cancelled") break;
                Thread.Sleep(1500); // bounded poll: the card is rebuilt from Core every few seconds
            }
            Mark("terminal card status: " + status);
            product.Capture("04-terminal-state");
            File.WriteAllLines(Path.Combine(runDirectory, "ui-activity-seen.txt"), seen.OrderBy(value => value, StringComparer.Ordinal));
            Assert.Equal("Completed and verified", status);

            // Core-side evidence for the same task: agents/plan/edit/test steps really happened.
            string fullTaskId = ResolveTaskId(product, taskPrefix);
            using JsonDocument logDocument = JsonDocument.Parse(product.CoreGet("/api/v1/orchestrator/tasks/" + fullTaskId + "/logs"));
            string[] logs = logDocument.RootElement.GetProperty("logs").EnumerateArray()
                .Select(entry => entry.GetProperty("message").GetString() ?? string.Empty).ToArray();
            File.WriteAllLines(Path.Combine(runDirectory, "core-task-log.txt"), logs);
            Assert.Contains(logs, line => line.StartsWith("Coding model:", StringComparison.Ordinal));
            // The plan is model-dependent (a small model may skip update_plan); it is recorded, the
            // agent activity itself (model attribution + numbered steps) is what must be visible.
            File.WriteAllText(Path.Combine(runDirectory, "plan-observed.txt"),
                logs.Any(line => line.Contains("implementation plan", StringComparison.OrdinalIgnoreCase)) ? "yes" : "no (model emitted no update_plan)");
            Assert.Contains(logs, line => line.StartsWith("Step 1:", StringComparison.Ordinal));
            Assert.Contains(seen, text => text.Contains("Step ", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("replace_range", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("run_command", StringComparison.Ordinal) && line.Contains("(exit 0)", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.StartsWith("Verifier pass", StringComparison.Ordinal) && line.Contains("PASS", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.StartsWith("[phase-timing-total]", StringComparison.Ordinal));
            using JsonDocument taskDocument = JsonDocument.Parse(product.CoreGet("/api/v1/orchestrator/tasks/" + fullTaskId));
            Assert.Equal("completed", taskDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("pass", taskDocument.RootElement.GetProperty("verificationStatus").GetString()!.ToLowerInvariant());

            // 4. Before apply the user's repository must be untouched (the agent worked in a worktree).
            Assert.Equal(baseline, Git(repository, "rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, Git(repository, "status", "--porcelain=v1").Trim());
            Assert.Contains("line_total(200, 10)", File.ReadAllText(Path.Combine(repository, "src", "verify.py")));
            Assert.Equal(1, RunVerifier(repository).ExitCode); // the bug is still there in the original

            // 5. Review -> apply -> confirm, all through the UI.
            AutomationElement apply = UiWait.ById(product.MainWindow, "Chat_CodingTask_Apply_Button", ControlType.Button);
            UiWait.For(() => product.TryFindById("Chat_CodingTask_DiffStat_Text") != null, "review diffstat on the card", TimeSpan.FromSeconds(30));
            string diffStat = product.TryFindById("Chat_CodingTask_DiffStat_Text")!.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert.Contains("src/inventory.py", diffStat, StringComparison.Ordinal);
            Assert.DoesNotContain("src/verify.py", diffStat, StringComparison.Ordinal);
            product.MainWindow.SetForeground();
            apply.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => product.TryFindById("Chat_CodingTask_ApplyConfirm_Dialog") != null, "apply confirmation dialog", TimeSpan.FromSeconds(30));
            AutomationElement dialog = product.TryFindById("Chat_CodingTask_ApplyConfirm_Dialog")!;
            Mark("apply confirmation shown");
            UiWait.For(() =>
            {
                try { return dialog.FindAllDescendants().Any(e => (e.Properties.Name.ValueOrDefault ?? string.Empty).Contains("inventory.py", StringComparison.Ordinal)); }
                catch (FlaUI.Core.Exceptions.FlaUIException) { return false; }
            },
                "changed file listed in the confirmation", TimeSpan.FromSeconds(15));
            product.Capture("05-apply-confirmation");
            UiWait.For(() => dialog.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")) != null, "confirmation primary button", TimeSpan.FromSeconds(15));
            dialog.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton"))!.Patterns.Invoke.Pattern.Invoke();

            UiWait.For(() => RunVerifier(repository).ExitCode == 0, "applied fix in the disposable repository", TimeSpan.FromSeconds(60));
            Mark("apply completed");
            product.Capture("06-applied");

            // 6. Verify the resulting git state: uncommitted edit only, nothing else.
            Assert.Equal(baseline, Git(repository, "rev-parse", "HEAD").Trim());
            string[] changed = Git(repository, "status", "--porcelain=v1").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).ToArray();
            File.WriteAllLines(Path.Combine(runDirectory, "git-status-after-apply.txt"), changed);
            Assert.Single(changed);
            Assert.Matches(@"^ M src/inventory\.py$", changed[0]);
            Assert.DoesNotContain("src/verify.py", string.Join("\n", changed), StringComparison.Ordinal);
            Assert.Equal(string.Empty, Git(repository, "diff", "--cached").Trim()); // nothing staged, no commit
            Assert.Equal(baseline, Git(repository, "rev-parse", "HEAD").Trim());
            string diff = Git(repository, "diff");
            File.WriteAllText(Path.Combine(runDirectory, "applied.diff"), diff);
            Assert.Contains(">= 10", diff, StringComparison.Ordinal);
            Assert.Equal(0, RunVerifier(repository).ExitCode);
            Assert.Equal("Changes applied to project", UiWait.ById(product.MainWindow, "Chat_CodingTask_Apply_Button", ControlType.Button).Name);
            File.WriteAllText(Path.Combine(runDirectory, "RESULT.txt"), "PASS " + DateTime.UtcNow.ToString("O") + "\ntask " + fullTaskId + "\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(runDirectory, "FAILURE.txt"), ex.ToString());
            product.Capture("failure");
            throw;
        }
    }

    [RealCodingFact("PARUX_REAL_CODING_WORKSPACE"), Trait("Category", "HardwareE2E"), Trait("Category", "PaRuXDogfood"), Trait("Category", "FlaUI")]
    public void Public_clone_artifact_search_recovers_regex_alternatives_and_applies_through_real_chat()
    {
        const string workspaceVariable = "PARUX_REAL_CODING_WORKSPACE";
        const string goal =
            "Fix literal searches that use | in CodingAgentArtifactStore.SearchFromDirectory. With regex=false, " +
            "first return any exact case-insensitive literal matches across the whole artifact. Only when there " +
            "are no exact matches, split a query containing | into at most 16 trimmed, nonempty terms and return " +
            "each line matching any term once, up to maxMatches. Do not run arbitrary regex as an implicit fallback. " +
            "Preserve explicit regex behavior and timeout. " +
            "First add two tests in CodingAgentArtifactStoreTests: one proving literal OR fallback returns each " +
            "matching line once when there is no exact match, and one proving an exact literal match anywhere in " +
            "the artifact suppresses OR fallback matches on other lines. Run the new fallback test with a focused " +
            "filter with -clp:ErrorsOnly and confirm a real assertion failure before changing production code. Then implement the smallest " +
            "fix in the store, run all CodingAgentArtifactStoreTests with: dotnet test " +
            "tests\\PaRuX.Server.Tests\\PaRuX.Server.Tests.csproj -c Release --filter " +
            "FullyQualifiedName~CodingAgentArtifactStoreTests -v:q -p:GitRepositoryConfigurationScope=local " +
            "-p:NuGetAudit=false -p:RunAnalyzers=false -clp:ErrorsOnly. " +
            "This machine localizes the VSTest summary in Italian " +
            "(Superato, Superati, Non superati, Totale). If output is truncated, call list_artifacts once, " +
            "then read the final 3000 characters of the relevant artifact once at offset max(0, Characters-3000). " +
            "Never repeat the same artifact read or rerun a completed command to recover output. " +
            "The filtered tests prove the behavior but are not an accepted completion receipt. After they pass, " +
            "run the unfiltered scoped verifier exactly once: dotnet build src\\PaRuX.App\\PaRuX.App.csproj -c Release " +
            "-m:1 -p:UseXamlCompilerExecutable=false -p:GitRepositoryConfigurationScope=local " +
            "-p:NuGetAudit=false -p:RunAnalyzers=false -clp:ErrorsOnly. Do not use --no-restore: " +
            "the fresh workspace has no project.assets.json, so allow the build's normal restore. " +
            "The in-process XAML compiler avoids the external executable blocked by AppContainer. " +
            "Do not run the full Server suite or root-level dotnet test/build. Use read_file_range only for " +
            "SearchFromDirectory and the artifact-search tests; do not use batch_read, whole-file reads, or explore " +
            "unrelated code. Modify only CodingAgentArtifactStore.cs and CodingAgentArtifactStoreTests.cs.";
        string workspace = Path.GetFullPath(RealCodingProduct.RequireVariable(workspaceVariable));
        string artifactsRoot = RealCodingProduct.RequireVariable(RealCodingProduct.ArtifactsVariable);
        string runDirectory = Path.Combine(artifactsRoot, "parux-run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        string screenshots = Path.Combine(runDirectory, "ui");
        Directory.CreateDirectory(runDirectory);

        Assert.True(File.Exists(Path.Combine(workspace, "AGENTS.md")), "Dogfood workspace must be the PaRuX repository.");
        string baselineHead = Git(workspace, "rev-parse", "HEAD").Trim();
        Assert.Equal(string.Empty, Git(workspace, "status", "--porcelain=v1").Trim());

        using var product = new RealCodingProduct(screenshots, workspace);
        var timeline = new List<string>();
        void Mark(string text)
        {
            timeline.Add(DateTime.UtcNow.ToString("HH:mm:ss.fff") + "  " + text);
            File.WriteAllLines(Path.Combine(runDirectory, "ui-timeline.txt"), timeline);
        }

        try
        {
            bool coreReady = false;
            DateTime coreDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (!coreReady && DateTime.UtcNow < coreDeadline)
            {
                try
                {
                    using JsonDocument taskList = JsonDocument.Parse(product.CoreGet("/api/v1/orchestrator/tasks"));
                    coreReady = taskList.RootElement.TryGetProperty("tasks", out JsonElement tasks)
                        && tasks.ValueKind == JsonValueKind.Array;
                }
                catch (Exception)
                {
                    // The desktop window appears before the isolated Core has finished starting.
                }
                if (!coreReady) Thread.Sleep(1000);
            }
            Assert.True(coreReady, "PaRuX Core was not ready before the coding request.");
            Mark("Core API ready");

            new MainWindowPage(product.MainWindow).Click("MainWindow_NewChat_Button", ControlType.Button);
            var chat = new ChatPage(product.MainWindow);
            Assert.Equal(Path.GetFileName(workspace), chat.CodeFolderLabel.Name);
            chat.SendPrompt("/code " + goal);
            Mark("submitted /code through Chat_Send_Button");

            UiWait.For(() => product.TryFindById("parux-coding-permission-mode-dialog") != null
                    || product.TryFindById("Chat_CodingTask_Id_Text") != null,
                "coding permission dialog or task card", TimeSpan.FromMinutes(2));
            AutomationElement? permissionDialog = product.TryFindById("parux-coding-permission-mode-dialog");
            if (permissionDialog != null)
            {
                AutomationElement modePicker = permissionDialog.FindFirstDescendant(cf => cf.ByControlType(ControlType.ComboBox))
                    ?? throw new InvalidOperationException("Coding permission mode picker was not exposed through UIA.");
                modePicker.Patterns.ExpandCollapse.Pattern.Expand();
                AutomationElement? bypassOption = null;
                UiWait.For(() =>
                {
                    bypassOption = modePicker.AsComboBox().Items.FirstOrDefault(item =>
                        item.Properties.ControlType.ValueOrDefault == ControlType.ListItem
                        && (item.Properties.Name.ValueOrDefault ?? string.Empty).StartsWith("Bypass", StringComparison.Ordinal));
                    return bypassOption != null;
                }, "Bypass coding-run permission option", TimeSpan.FromSeconds(15));
                bypassOption!.Patterns.SelectionItem.Pattern.Select();
                AutomationElement startCoding = permissionDialog.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton"))
                    ?? permissionDialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                        .FirstOrDefault(button => button.Properties.Name.ValueOrDefault == "Start coding")
                    ?? throw new InvalidOperationException("Coding permission Start coding button was not exposed through UIA.");
                startCoding.Patterns.Invoke.Pattern.Invoke();
                Mark("selected Bypass and started coding through UIA");
                UiWait.For(() => product.TryFindById("parux-coding-permission-mode-dialog") == null,
                    "coding permission dialog closed", TimeSpan.FromSeconds(30));
            }

            UiWait.For(() => (product.TryFindById("Chat_CodingTask_Id_Text")?.Properties.Name.ValueOrDefault ?? string.Empty)
                    .StartsWith("Task ", StringComparison.Ordinal),
                "Core task id on the card", TimeSpan.FromMinutes(3));
            string taskPrefix = product.TryFindById("Chat_CodingTask_Id_Text")!.Name
                .Replace("Task ", string.Empty, StringComparison.Ordinal).Trim();
            Assert.True(taskPrefix.Length >= 8, "task id text: " + taskPrefix);
            Mark("task card visible: Task " + taskPrefix);
            product.Capture("01-task-submitted");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string status = string.Empty;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(45);
            while (DateTime.UtcNow < deadline)
            {
                status = product.TryFindById("Chat_CodingTask_Status_Text")?.Properties.Name.ValueOrDefault ?? string.Empty;
                foreach (AutomationElement item in product.FindAllById("Chat_CodingTask_Activity_Item"))
                {
                    string activity;
                    try { activity = item.Properties.Name.ValueOrDefault ?? string.Empty; }
                    catch (FlaUI.Core.Exceptions.FlaUIException) { continue; }
                    if (seen.Add(activity))
                        Mark("activity: " + (activity.Length > 220 ? activity[..220] : activity));
                }

                if (status.StartsWith("Completed", StringComparison.Ordinal)
                    || status is "Verification failed" or "Failed" or "Cancelled") break;
                Thread.Sleep(1500);
            }

            Mark("terminal card status: " + status);
            product.Capture("02-terminal-state");
            File.WriteAllLines(Path.Combine(runDirectory, "ui-activity-seen.txt"),
                seen.OrderBy(value => value, StringComparer.Ordinal));
            Assert.Equal("Completed and verified", status);

            string fullTaskId = ResolveTaskId(product, taskPrefix);
            using JsonDocument logDocument = JsonDocument.Parse(
                product.CoreGet("/api/v1/orchestrator/tasks/" + fullTaskId + "/logs"));
            string[] logs = logDocument.RootElement.GetProperty("logs").EnumerateArray()
                .Select(entry => entry.GetProperty("message").GetString() ?? string.Empty).ToArray();
            File.WriteAllLines(Path.Combine(runDirectory, "core-task-log.txt"), logs);
            Assert.Contains(logs, line => line.StartsWith("Coding model:", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.StartsWith("Step 1:", StringComparison.Ordinal));
            Assert.Contains(seen, activity => activity.Contains("Step ", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("CodingAgentArtifactStoreTests", StringComparison.Ordinal)
                && line.Contains("run_command", StringComparison.Ordinal)
                && line.Contains("(exit 0)", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.Contains("PaRuX.App.csproj", StringComparison.Ordinal)
                && line.Contains("run_command", StringComparison.Ordinal)
                && line.Contains("(exit 0)", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.StartsWith("Verifier pass", StringComparison.Ordinal)
                && line.Contains("PASS", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.StartsWith("[phase-timing-total]", StringComparison.Ordinal));

            using JsonDocument taskDocument = JsonDocument.Parse(product.CoreGet("/api/v1/orchestrator/tasks/" + fullTaskId));
            Assert.Equal("completed", taskDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("pass", taskDocument.RootElement.GetProperty("verificationStatus").GetString()!.ToLowerInvariant());

            string runtimeStatusJson = product.CoreGet("/api/v1/runtime/status");
            File.WriteAllText(Path.Combine(runDirectory, "runtime-status.json"), runtimeStatusJson);
            using JsonDocument runtimeStatus = JsonDocument.Parse(runtimeStatusJson);
            Assert.Equal("Qwen3.8-27B-UD-IQ4_XS.gguf",
                Path.GetFileName(runtimeStatus.RootElement.GetProperty("activeModelPath").GetString()));
            Assert.Equal(baselineHead, Git(workspace, "rev-parse", "HEAD").Trim());
            Assert.Equal(string.Empty, Git(workspace, "status", "--porcelain=v1").Trim());

            AutomationElement apply = UiWait.ById(product.MainWindow, "Chat_CodingTask_Apply_Button", ControlType.Button);
            string diffStat = product.TryFindById("Chat_CodingTask_DiffStat_Text")?.Properties.Name.ValueOrDefault ?? string.Empty;
            Assert.Contains("CodingAgentArtifactStore.cs", diffStat, StringComparison.Ordinal);
            Assert.Contains("CodingAgentArtifactStoreTests.cs", diffStat, StringComparison.Ordinal);
            product.MainWindow.SetForeground();
            apply.Patterns.Invoke.Pattern.Invoke();
            UiWait.For(() => product.TryFindById("Chat_CodingTask_ApplyConfirm_Dialog") != null,
                "apply confirmation dialog", TimeSpan.FromSeconds(30));
            AutomationElement dialog = product.TryFindById("Chat_CodingTask_ApplyConfirm_Dialog")!;
            UiWait.For(() => dialog.FindAllDescendants().Any(element =>
                    (element.Properties.Name.ValueOrDefault ?? string.Empty).Contains("CodingAgentArtifactStore", StringComparison.Ordinal)),
                "artifact-preview files listed in confirmation", TimeSpan.FromSeconds(15));
            product.Capture("03-apply-confirmation");
            UiWait.For(() => dialog.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")) != null,
                "confirmation primary button", TimeSpan.FromSeconds(15));
            dialog.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton"))!.Patterns.Invoke.Pattern.Invoke();

            string[] expectedChangedPaths = new[]
                {
                    "src/PaRuX.App/Services/CodingAgentArtifactStore.cs",
                    "tests/PaRuX.Server.Tests/CodingAgentArtifactStoreTests.cs"
                }
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            UiWait.For(() => Git(workspace, "status", "--porcelain=v1")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r'))
                    .Select(line => line.Length > 3 ? line[3..] : line)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(expectedChangedPaths, StringComparer.Ordinal),
                "applied patch in the public repository", TimeSpan.FromSeconds(60));
            string[] changed = Git(workspace, "status", "--porcelain=v1").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r')).ToArray();
            string[] changedPaths = changed.Select(line => line.Length > 3 ? line[3..] : line)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedChangedPaths, changedPaths);
            Assert.Equal(baselineHead, Git(workspace, "rev-parse", "HEAD").Trim());
            string diff = Git(workspace, "diff");
            File.WriteAllText(Path.Combine(runDirectory, "applied.diff"), diff);
            Assert.Contains("Search", diff, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regex", diff, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Assert", diff, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(runDirectory, "RESULT.txt"),
                "PASS " + DateTime.UtcNow.ToString("O") + "\ntask " + fullTaskId + "\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(runDirectory, "FAILURE.txt"), ex.ToString());
            product.Capture("failure");
            throw;
        }
    }

    private static string ResolveTaskId(RealCodingProduct product, string prefix)
    {
        using JsonDocument list = JsonDocument.Parse(product.CoreGet("/api/v1/orchestrator/tasks"));
        JsonElement tasks = list.RootElement.TryGetProperty("tasks", out JsonElement nested) ? nested : list.RootElement;
        string[] matches = tasks.EnumerateArray()
            .Select(task => task.GetProperty("id").GetString() ?? string.Empty)
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Task id prefix " + prefix + " matched " + matches.Length + " task(s).");
    }

    private static string CreateDisposableRepository(string root)
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "inventory.py"),
            "def line_total(unit_price_cents, quantity):\n"
            + "    \"\"\"Total for one order line in cents. Orders of 10 or more units get 10% off.\"\"\"\n"
            + "    total = unit_price_cents * quantity\n"
            + "    if quantity > 10:\n"
            + "        total = total * 90 // 100\n"
            + "    return total\n\n\n"
            + "def order_total(lines):\n"
            + "    return sum(line_total(price, qty) for price, qty in lines)\n");
        File.WriteAllText(Path.Combine(source, "verify.py"),
            "import sys\n"
            + "from inventory import line_total, order_total\n\n"
            + "failures = []\n\n\n"
            + "def check(name, actual, expected):\n"
            + "    if actual != expected:\n"
            + "        failures.append(f'{name}: expected {expected}, got {actual}')\n\n\n"
            + "check('no discount below 10 units', line_total(200, 5), 1000)\n"
            + "check('discount at exactly 10 units', line_total(200, 10), 1800)\n"
            + "check('discount above 10 units', line_total(200, 12), 2160)\n"
            + "check('fractional cent discount floors to integer cents', line_total(1, 11), 9)\n"
            + "check('order total', order_total([(100, 10), (50, 1)]), 950)\n"
            + "for failure in failures:\n"
            + "    print('FAIL', failure)\n"
            + "print('verify: ' + ('FAILED' if failures else 'OK'))\n"
            + "sys.exit(1 if failures else 0)\n");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "__pycache__/\n.parux-agents/\n");
        Git(root, "init", "-q");
        Git(root, "config", "user.email", "bic09@example.invalid");
        Git(root, "config", "user.name", "bic09-disposable");
        Git(root, "config", "core.autocrlf", "false");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "baseline with a boundary bug");
        return Git(root, "rev-parse", "HEAD").Trim();
    }

    private static (int ExitCode, string Output) RunVerifier(string repository)
    {
        var start = new ProcessStartInfo("python")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-B"); // no .pyc written into the user's repository
        start.ArgumentList.Add(Path.Combine("src", "verify.py"));
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("python did not start.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("verify.py timed out."); }
        return (process.ExitCode, output);
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("git timed out."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(' ', arguments) + " failed: " + error);
        return output;
    }
}
