using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views
{
    /// <summary>
    /// "Import ComfyUI workflow" and the LoRA search/install list. Every safety decision (validation, consent-bound plan hash,
    /// hash-verified download) is made by PaRuX Core; this file only shows the result and sends the user's explicit choices.
    /// Wording and AutomationIds come from <see cref="CreateWorkflowLoraPresenter"/>.
    /// </summary>
    public sealed partial class CreateWorkspaceView
    {
        private static readonly HttpClient WorkflowHttp = PaRuXServerController.CreateControlPlaneClient(TimeSpan.FromSeconds(60));
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

        private readonly TextBlock _importStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly ComboBox _importedTemplates = new ComboBox { MinWidth = 260, PlaceholderText = "Imported workflows" };
        private readonly TextBox _importedPrompt = new TextBox { PlaceholderText = "Prompt (used if the workflow has a prompt setting)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48 };
        private readonly Dictionary<string, (string Type, bool NeedsImage)> _importedInfo = new();
        private readonly TextBox _loraSearch = new TextBox { PlaceholderText = "Search LoRAs on CivitAI" };
        private readonly CheckBox _loraNsfw = new CheckBox { Content = "Include NSFW results", IsChecked = false };
        private readonly StackPanel _loraResults = new StackPanel { Spacing = 6 };
        private readonly StackPanel _loraInstalled = new StackPanel { Spacing = 2 };
        private readonly TextBlock _loraStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

        private UIElement BuildWorkflowLoraPanel()
        {
            var panel = new StackPanel { Spacing = 8 };

            var import = new Button { Content = "Import workflow…" };
            AutomationProperties.SetAutomationId(import, CreateWorkflowLoraPresenter.WorkflowImportButton);
            import.Click += async (_, _) => await ImportWorkflowAsync();
            AutomationProperties.SetAutomationId(_importStatus, CreateWorkflowLoraPresenter.WorkflowImportStatus);
            AutomationProperties.SetAutomationId(_importedTemplates, CreateWorkflowLoraPresenter.WorkflowImportParameters);
            AutomationProperties.SetName(_importedTemplates, "Imported workflow template");
            var run = new Button { Content = "Run imported workflow" };
            run.Click += async (_, _) => await RunImportedWorkflowAsync();
            _runButtons.Add(run);
            panel.Children.Add(import);
            panel.Children.Add(_importStatus);
            panel.Children.Add(_importedTemplates);
            panel.Children.Add(_importedPrompt);
            panel.Children.Add(run);

            panel.Children.Add(new TextBlock { Text = "LoRA", FontSize = 12, Opacity = 0.72, Margin = new Thickness(0, 8, 0, 0) });
            AutomationProperties.SetAutomationId(_loraSearch, CreateWorkflowLoraPresenter.LoraSearchBox);
            AutomationProperties.SetAutomationId(_loraResults, CreateWorkflowLoraPresenter.LoraResultList);
            AutomationProperties.SetAutomationId(_loraInstalled, CreateWorkflowLoraPresenter.LoraInstalledList);
            var search = new Button { Content = "Search" };
            search.Click += async (_, _) => await SearchLorasAsync();
            _loraSearch.KeyDown += async (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter) await SearchLorasAsync();
            };
            panel.Children.Add(_loraSearch);
            panel.Children.Add(_loraNsfw);
            panel.Children.Add(search);
            panel.Children.Add(_loraStatus);
            panel.Children.Add(_loraResults);
            panel.Children.Add(new TextBlock { Text = "Installed LoRAs", FontSize = 11, Opacity = 0.68 });
            panel.Children.Add(_loraInstalled);

            Loaded += async (_, _) => { await RefreshImportedAsync(); await RefreshInstalledLorasAsync(); };
            return panel;
        }

        // ---- transport -------------------------------------------------------------------------------------------

        private static async Task<(bool Ok, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            ServerBootstrap server = PaRuXServerController.Current.GetCurrentBootstrap();
            using var request = new HttpRequestMessage(method, server.Endpoint.TrimEnd('/') + path);
            request.Headers.Add("X-PaRuX-Token", server.Token);
            if (body != null) request.Content = JsonContent.Create(body, options: WebJson);
            using HttpResponseMessage response = await WorkflowHttp.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            JsonElement element = default;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { element = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
            }
            return (response.IsSuccessStatusCode, element);
        }

        private static string ErrorOf(JsonElement body, string fallback) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() ?? fallback : fallback;

        private static IReadOnlyList<string> Strings(JsonElement body, string name) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
                : new List<string>();

        // ---- workflow import -------------------------------------------------------------------------------------

        private async Task ImportWorkflowAsync()
        {
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".json");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null) return;
                if (new FileInfo(file.Path).Length > 2 * 1024 * 1024) { _importStatus.Text = "Cannot import: the file is larger than 2 MB."; return; }
                string json = await File.ReadAllTextAsync(file.Path);

                var (ok, preview) = await SendAsync(HttpMethod.Post, "/api/v1/media/comfy-workflows/preview", new { workflowJson = json }, CancellationToken.None);
                if (!ok) { _importStatus.Text = "Cannot import: " + ErrorOf(preview, "the server refused the file."); return; }
                bool valid = preview.GetProperty("ok").GetBoolean();
                string? type = preview.TryGetProperty("workflowType", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                int count = preview.TryGetProperty("parameters", out JsonElement ps) ? ps.GetArrayLength() : 0;
                bool verified = preview.TryGetProperty("verifiedAgainstComfyUi", out JsonElement v) && v.GetBoolean();
                string status = CreateWorkflowLoraPresenter.ImportStatus(valid, type, count, Strings(preview, "errors"), Strings(preview, "warnings"), verified);
                _importStatus.Text = status;
                if (!valid) return;

                var name = new TextBox { Text = Path.GetFileNameWithoutExtension(file.Name), MaxLength = 64 };
                AutomationProperties.SetAutomationId(name, CreateWorkflowLoraPresenter.WorkflowImportName);
                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap });
                foreach (string warning in Strings(preview, "warnings")) content.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
                content.Children.Add(new TextBlock { Text = "Template name" });
                content.Children.Add(name);
                var dialog = new ContentDialog
                {
                    Title = "Import ComfyUI workflow", Content = content, PrimaryButtonText = "Import", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
                };
                AutomationProperties.SetAutomationId(dialog, CreateWorkflowLoraPresenter.WorkflowImportConfirm);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                var (saved, result) = await SendAsync(HttpMethod.Post, "/api/v1/media/comfy-workflows",
                    new { name = name.Text, workflowJson = json, replaceExisting = false }, CancellationToken.None);
                _importStatus.Text = saved ? "Imported \"" + name.Text.Trim() + "\"." : "Cannot import: " + ErrorOf(result, "the server refused the workflow.");
                await RefreshImportedAsync();
            }
            catch (Exception ex) { _importStatus.Text = "Cannot import: " + ex.Message; }
        }

        private async Task RefreshImportedAsync()
        {
            try
            {
                var (ok, list) = await SendAsync(HttpMethod.Get, "/api/v1/media/comfy-workflows", null, CancellationToken.None);
                if (!ok || list.ValueKind != JsonValueKind.Array) return;
                _importedInfo.Clear();
                _importedTemplates.Items.Clear();
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string id = item.GetProperty("id").GetString() ?? "";
                    _importedInfo[id] = (item.GetProperty("workflowType").GetString() ?? "image", item.GetProperty("requiresSourceImage").GetBoolean());
                    _importedTemplates.Items.Add(new ComboBoxItem { Content = item.GetProperty("name").GetString(), Tag = id });
                }
                if (_importedTemplates.Items.Count > 0 && _importedTemplates.SelectedIndex < 0) _importedTemplates.SelectedIndex = 0;
            }
            catch (Exception) { /* the list is a convenience; the import status line reports real failures */ }
        }

        private async Task RunImportedWorkflowAsync()
        {
            CreateOperationTicket? ticket = null;
            try
            {
                if (_importedTemplates.SelectedItem is not ComboBoxItem { Tag: string id } || !_importedInfo.TryGetValue(id, out var info))
                    throw new InvalidOperationException("Import or choose a workflow first.");
                var request = new MediaJobRequest { Type = "imported", CustomWorkflowId = id, Prompt = _importedPrompt.Text.Trim(), Seed = -1 };
                if (info.NeedsImage)
                {
                    string source = _editSourcePath ?? throw new InvalidOperationException("This workflow needs a source image. Choose one in the Image edit section first.");
                    request.SourceImageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(source));
                }
                ticket = BeginOperation("Running imported workflow…");
                if (ticket == null) return;
                var progress = new Progress<MediaJobStatus>(job =>
                {
                    _progress.Value = Math.Clamp(job.ProgressPercent, 0, 100);
                    _status.Text = job.Stage + " · " + job.ProgressPercent + "%";
                });
                MediaAsset asset = await MediaJobClient.Current.RunAsync(request, progress, ticket.Token);
                string path = await SaveMediaAssetAsync(asset, info.Type == "video" ? "video" : "image", ticket.Token);
                _lastOutput.Text = "Output: " + path;
                ShowResult(path);
                FinishOperation(ticket, "Imported workflow completed locally and registered in Artifact Workspace.");
            }
            catch (OperationCanceledException) { FinishOperation(ticket, "Generation cancelled."); }
            catch (Exception ex) { FinishOperation(ticket, "Generation failed: " + ex.Message); }
        }

        // ---- LoRA ------------------------------------------------------------------------------------------------

        private async Task RefreshInstalledLorasAsync()
        {
            try
            {
                var (ok, list) = await SendAsync(HttpMethod.Get, "/api/v1/media/loras", null, CancellationToken.None);
                if (!ok || list.ValueKind != JsonValueKind.Array) return;
                _loraInstalled.Children.Clear();
                foreach (JsonElement l in list.EnumerateArray())
                {
                    string words = CreateWorkflowLoraPresenter.TriggerWords(Strings(l, "triggerWords"));
                    _loraInstalled.Children.Add(new TextBlock { Text = l.GetProperty("name").GetString() + " · " + words, TextWrapping = TextWrapping.Wrap });
                }
                if (_loraInstalled.Children.Count == 0) _loraInstalled.Children.Add(new TextBlock { Text = "None installed", Opacity = 0.6 });
            }
            catch (Exception) { /* advisory list */ }
        }

        private async Task SearchLorasAsync()
        {
            try
            {
                string q = _loraSearch.Text.Trim();
                if (q.Length == 0) { _loraStatus.Text = "Enter a search first."; return; }
                _loraStatus.Text = "Searching…";
                string nsfw = _loraNsfw.IsChecked == true ? "true" : "false";
                var (ok, page) = await SendAsync(HttpMethod.Get,
                    "/api/v1/media/loras/search?q=" + Uri.EscapeDataString(q) + "&includeNsfw=" + nsfw, null, CancellationToken.None);
                if (!ok) { _loraStatus.Text = "Search failed: " + ErrorOf(page, "the catalogue is not reachable."); return; }
                _loraResults.Children.Clear();
                foreach (JsonElement item in page.GetProperty("items").EnumerateArray())
                {
                    string modelId = item.GetProperty("modelId").GetString() ?? "";
                    bool isNsfw = item.GetProperty("nsfw").GetBoolean();
                    foreach (JsonElement version in item.GetProperty("versions").EnumerateArray())
                    foreach (JsonElement file in version.GetProperty("files").EnumerateArray())
                    {
                        string baseModel = version.TryGetProperty("baseModel", out JsonElement bm) && bm.ValueKind == JsonValueKind.String ? bm.GetString()! : "unknown base model";
                        var line = new StackPanel { Spacing = 2 };
                        string title = item.GetProperty("name").GetString() + " · " + version.GetProperty("name").GetString() + " · " + baseModel
                            + (isNsfw ? "  [" + CreateWorkflowLoraPresenter.NsfwLabel(true) + "]" : "");
                        line.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
                        var words = new TextBlock { Text = CreateWorkflowLoraPresenter.TriggerWords(Strings(version, "trainedWords")), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                        AutomationProperties.SetAutomationId(words, CreateWorkflowLoraPresenter.LoraTriggerWords);
                        line.Children.Add(words);
                        var install = new Button { Content = "Install…" };
                        string versionId = version.GetProperty("versionId").GetString() ?? "";
                        string fileId = file.GetProperty("fileId").GetString() ?? "";
                        install.Click += async (_, _) => await InstallLoraAsync(q, modelId, versionId, fileId);
                        line.Children.Add(install);
                        _loraResults.Children.Add(line);
                    }
                }
                _loraStatus.Text = _loraResults.Children.Count == 0 ? "No LoRAs found." : _loraResults.Children.Count + " file(s) found. Nothing is downloaded until you confirm.";
            }
            catch (Exception ex) { _loraStatus.Text = "Search failed: " + ex.Message; }
        }

        private async Task InstallLoraAsync(string query, string modelId, string versionId, string fileId)
        {
            try
            {
                bool nsfw = _loraNsfw.IsChecked == true;
                var key = new { query, page = 1, includeNsfw = nsfw, modelId, versionId, fileId };
                var (ok, plan) = await SendAsync(HttpMethod.Post, "/api/v1/media/loras/plan", key, CancellationToken.None);
                if (!ok) { _loraStatus.Text = "Cannot plan the install: " + ErrorOf(plan, "the file was not found."); return; }
                if (!plan.GetProperty("installable").GetBoolean())
                {
                    _loraStatus.Text = "This file cannot be installed here: " + (plan.TryGetProperty("reason", out JsonElement r) ? r.GetString() : "unknown reason");
                    return;
                }
                string fileName = plan.GetProperty("fileName").GetString() ?? "lora.safetensors";
                bool planNsfw = plan.GetProperty("nsfw").GetBoolean();
                string prompt = CreateWorkflowLoraPresenter.InstallPrompt(fileName, plan.GetProperty("sizeBytes").GetInt64(), "civitai.com", planNsfw);
                var content = new StackPanel { Spacing = 6 };
                content.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
                if (plan.TryGetProperty("license", out JsonElement lic) && lic.ValueKind == JsonValueKind.Object)
                    content.Children.Add(new TextBlock
                    {
                        Text = "Commercial use: " + (Strings(lic, "commercialUse") is { Count: > 0 } c ? string.Join(", ", c) : "not stated") + ". Check the model page for the full licence.",
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.75
                    });
                var dialog = new ContentDialog
                {
                    Title = "Install LoRA", Content = content, PrimaryButtonText = "Download and install", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
                };
                AutomationProperties.SetAutomationId(dialog, CreateWorkflowLoraPresenter.LoraInstallConfirm);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { _loraStatus.Text = "Nothing was downloaded."; return; }

                var (accepted, job) = await SendAsync(HttpMethod.Post, "/api/v1/media/loras/install",
                    new { query, page = 1, includeNsfw = nsfw, modelId, versionId, fileId, consent = true, planHash = plan.GetProperty("planHash").GetString() }, CancellationToken.None);
                if (!accepted) { _loraStatus.Text = "Install refused: " + ErrorOf(job, "the server refused the request."); return; }
                string jobId = job.GetProperty("id").GetString() ?? "";
                for (int i = 0; i < 7200; i++)
                {
                    _loraStatus.Text = "Downloading " + fileName + "…";
                    await Task.Delay(1000);
                    var (_, state) = await SendAsync(HttpMethod.Get, "/api/v1/media/loras/install/" + jobId, null, CancellationToken.None);
                    string s = state.ValueKind == JsonValueKind.Object ? state.GetProperty("state").GetString() ?? "" : "";
                    if (s is "completed" or "failed")
                    {
                        string msg = state.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : s;
                        _loraStatus.Text = s == "completed" ? msg : "Install failed: " + msg;
                        break;
                    }
                }
                await RefreshInstalledLorasAsync();
            }
            catch (Exception ex) { _loraStatus.Text = "Install failed: " + ex.Message; }
        }
    }
}
