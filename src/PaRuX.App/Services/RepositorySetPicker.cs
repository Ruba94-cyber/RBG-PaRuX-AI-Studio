using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PaRuX.Services
{
    public sealed record RepositorySetSelection(
        string PrimaryWorkspaceRoot,
        IReadOnlyList<AddAgentRepositoryAuthorityRequest> AdditionalRepositories);

    /// <summary>
    /// Collects a COMPLETE repository topology locally before any authority is written to Core.
    /// Folder picks and aliases are validated on the desktop; Core repeats all validation and owns
    /// the final atomic write. This prevents a coding worker from observing an intermediate set.
    /// </summary>
    public static class RepositorySetPicker
    {
        private static readonly Regex AliasPattern = new("^[A-Za-z][A-Za-z0-9._-]{0,31}$", RegexOptions.Compiled);
        private const int MaxRepositories = 8;

        public static async Task<RepositorySetSelection?> PickAsync(XamlRoot xamlRoot)
        {
            string? primary = await PickFolderAsync("Choose primary Project repository").ConfigureAwait(true);
            if (primary == null) return null;

            var additional = new List<AddAgentRepositoryAuthorityRequest>();
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "primary" };
            var paths = new List<string> { primary };

            while (additional.Count < MaxRepositories - 1)
            {
                var choice = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "Project repositories",
                    Content = additional.Count == 0
                        ? "Use only the primary repository, or add another independent Git repository? PaRuX writes nothing until you finish the whole set."
                        : "Add another repository, or finish this complete authority set?",
                    PrimaryButtonText = "Add repository",
                    CloseButtonText = "Finish",
                    DefaultButton = ContentDialogButton.Close
                };
                ContentDialogResult result = await choice.ShowAsync();
                if (result != ContentDialogResult.Primary) break;

                string? folder = await PickFolderAsync("Choose additional Project repository").ConfigureAwait(true);
                if (folder == null) return null;
                if (paths.Any(existing => PathEquals(existing, folder)))
                {
                    await ShowMessageAsync(xamlRoot, "Duplicate repository", "That repository path is already in this authority set.");
                    continue;
                }

                string? alias = await AskAliasAsync(xamlRoot, aliases).ConfigureAwait(true);
                if (alias == null) return null;
                aliases.Add(alias);
                paths.Add(folder);
                additional.Add(new AddAgentRepositoryAuthorityRequest { Alias = alias, WorkspaceRoot = folder });
            }

            return new RepositorySetSelection(primary, additional);
        }

        private static async Task<string?> PickFolderAsync(string title)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            return folder == null ? null : Path.GetFullPath(folder.Path);
        }

        private static async Task<string?> AskAliasAsync(XamlRoot xamlRoot, HashSet<string> existingAliases)
        {
            while (true)
            {
                var input = new TextBox { PlaceholderText = "backend, frontend, docs…", MinWidth = 320 };
                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "Repository alias",
                    Content = input,
                    PrimaryButtonText = "Use alias",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };
                ContentDialogResult result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return null;

                string alias = (input.Text ?? string.Empty).Trim();
                if (!AliasPattern.IsMatch(alias))
                {
                    await ShowMessageAsync(xamlRoot, "Invalid alias",
                        "Start with a letter and use only letters, numbers, '.', '_' or '-' (maximum 32 characters).");
                    continue;
                }
                if (existingAliases.Contains(alias))
                {
                    await ShowMessageAsync(xamlRoot, "Duplicate alias", "That alias is already used in this repository set.");
                    continue;
                }
                return alias;
            }
        }

        private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }

        private static bool PathEquals(string left, string right)
        {
            try
            {
                string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(a, b, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch { return false; }
        }
    }
}
