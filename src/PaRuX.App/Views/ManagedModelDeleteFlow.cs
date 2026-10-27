using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>Shared "Delete" flow for a PaRuX-managed model (Model Hub and Settings): confirm with the file name and
    /// size, let PaRuX Core refuse when the model is assigned / loaded / linked / user-managed, and offer to switch the
    /// blocking role(s) to Automatic and retry.</summary>
    public static class ManagedModelDeleteFlow
    {
        /// <summary>Returns a status line, and sets deleted when the files were removed.</summary>
        public static async Task<(bool Deleted, string Message)> RunAsync(XamlRoot xamlRoot, string fileName, long sizeBytes)
        {
            var confirm = new ContentDialog
            {
                Title = "Delete this model?",
                Content = fileName + " (" + StorageQuotaService.FormatBytes(sizeBytes)
                    + ") will be permanently deleted from the PaRuX model library. This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return (false, "Delete cancelled.");

            try
            {
                ManagedModelDeleteOutcome outcome = await ModelDownloadClient.Current.DeleteManagedModelCheckedAsync(fileName, CancellationToken.None);
                if (!outcome.OtherBlockers && outcome.TextRolesToReset.Count > 0)
                {
                    var switchDialog = new ContentDialog
                    {
                        Title = "This model is in use",
                        Content = outcome.Message + "\n\nSwitch " + string.Join(", ", outcome.TextRolesToReset)
                            + " to Automatic and delete it?",
                        PrimaryButtonText = "Switch to Automatic and delete",
                        CloseButtonText = "Keep model",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = xamlRoot
                    };
                    if (await switchDialog.ShowAsync() != ContentDialogResult.Primary) return (false, outcome.Message);
                    foreach (string role in outcome.TextRolesToReset)
                        await ModelRolesClient.Current.ResetRoleAsync(role, CancellationToken.None);
                    outcome = await ModelDownloadClient.Current.DeleteManagedModelCheckedAsync(fileName, CancellationToken.None);
                }
                if (outcome.Deleted)
                {
                    Log.Write("Deleted managed model " + fileName);
                    return (true, "Deleted " + fileName + ".");
                }
                Log.Write("Delete refused for " + fileName + ": " + outcome.Message);
                return (false, outcome.Message);
            }
            catch (Exception ex)
            {
                Log.Write("Delete managed model failed: " + ex.Message);
                return (false, "Delete failed: " + ex.Message);
            }
        }
    }
}
