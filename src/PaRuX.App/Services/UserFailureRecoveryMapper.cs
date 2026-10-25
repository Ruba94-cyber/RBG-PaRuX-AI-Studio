using System;
using System.IO;
using System.Net.Http;
using System.Security;

namespace PaRuX.Services
{
    /// <summary>
    /// User-facing failure guidance containing cause, impact on disk, concrete next recovery action,
    /// and raw technical details for logs or Details disclosure. Never exposes raw exception text in the status line.
    /// </summary>
    public sealed class UserFailureGuidance
    {
        public required string Cause { get; init; }
        public required string DiskState { get; init; }
        public required string NextAction { get; init; }
        public string? TechnicalDetails { get; init; }

        public string FormatStatusLine()
        {
            return Cause + " " + NextAction;
        }

        public string FormatFullReport()
        {
            return Cause + " Disk state: " + DiskState + " Next action: " + NextAction;
        }
    }

    /// <summary>
    /// Central failure recovery mapper that converts exceptions, HTTP status codes, and domain errors
    /// into structured guidance (cause, disk state, and next action) suitable for user display.
    /// </summary>
    public static class UserFailureRecoveryMapper
    {
        public static UserFailureGuidance Map(Exception? ex, string? context = null)
        {
            string contextPrefix = !string.IsNullOrWhiteSpace(context) ? context.Trim() + ": " : string.Empty;

            if (ex is OperationCanceledException or TimeoutException)
            {
                return new UserFailureGuidance
                {
                    Cause = contextPrefix + "The operation was cancelled or timed out.",
                    DiskState = "Nothing was changed on disk.",
                    NextAction = "Retry the operation when ready.",
                    TechnicalDetails = ex.ToString()
                };
            }

            if (ex is UnauthorizedAccessException or SecurityException)
            {
                return new UserFailureGuidance
                {
                    Cause = contextPrefix + "Access was denied due to missing file or folder permissions.",
                    DiskState = "No changes were written to disk.",
                    NextAction = "Verify folder permissions or choose a directory where you have write access.",
                    TechnicalDetails = ex.ToString()
                };
            }

            if (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new UserFailureGuidance
                {
                    Cause = contextPrefix + "A required file or directory could not be found.",
                    DiskState = "Nothing was changed on disk.",
                    NextAction = "Verify that the path exists or reselect the target location.",
                    TechnicalDetails = ex.ToString()
                };
            }

            if (ex is IOException ioEx)
            {
                bool diskFull = ioEx.Message.Contains("full", StringComparison.OrdinalIgnoreCase)
                    || ioEx.Message.Contains("space", StringComparison.OrdinalIgnoreCase);
                return new UserFailureGuidance
                {
                    Cause = contextPrefix + (diskFull
                        ? "Storage capacity error: the destination drive may be full."
                        : "A disk or file system error occurred."),
                    DiskState = "Incomplete changes were aborted.",
                    NextAction = diskFull
                        ? "Free disk space on the target drive and retry."
                        : "Ensure the file is not locked by another program and retry.",
                    TechnicalDetails = ex.ToString()
                };
            }

            if (ex is HttpRequestException httpEx)
            {
                return new UserFailureGuidance
                {
                    Cause = contextPrefix + "Communication with the local AI service failed.",
                    DiskState = "No files were modified.",
                    NextAction = "Ensure PaRuX service is running and retry.",
                    TechnicalDetails = httpEx.ToString()
                };
            }

            // Fallback for unexpected or generic errors
            return new UserFailureGuidance
            {
                Cause = contextPrefix + "An unexpected error occurred during the operation.",
                DiskState = "Your project files were left intact.",
                NextAction = "Check application logs in Settings or retry the action.",
                TechnicalDetails = ex?.ToString()
            };
        }

        public static UserFailureGuidance MapHttp(int statusCode, string? details = null, string? context = null)
        {
            string contextPrefix = !string.IsNullOrWhiteSpace(context) ? context.Trim() + ": " : string.Empty;

            return statusCode switch
            {
                404 => new UserFailureGuidance
                {
                    Cause = contextPrefix + "The requested resource or endpoint was not found.",
                    DiskState = "Nothing was changed on disk.",
                    NextAction = "Verify that the model or task ID exists.",
                    TechnicalDetails = details
                },
                401 or 403 => new UserFailureGuidance
                {
                    Cause = contextPrefix + "Access was rejected by the service.",
                    DiskState = "Nothing was changed on disk.",
                    NextAction = "Verify your authorization token or permission profile.",
                    TechnicalDetails = details
                },
                500 or 502 or 503 or 504 => new UserFailureGuidance
                {
                    Cause = contextPrefix + "The local service returned an internal error (" + statusCode + ").",
                    DiskState = "No files were modified.",
                    NextAction = "Verify that the local runtime is active and try again.",
                    TechnicalDetails = details
                },
                _ => new UserFailureGuidance
                {
                    Cause = contextPrefix + "The service returned HTTP status " + statusCode + ".",
                    DiskState = "No files were modified.",
                    NextAction = "Check application logs for details or retry.",
                    TechnicalDetails = details
                }
            };
        }

        public static UserFailureGuidance MapCodingTaskLaunch(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                return new UserFailureGuidance
                {
                    Cause = "Coding task launch was cancelled.",
                    DiskState = "No files were modified.",
                    NextAction = "You can start the task again when ready.",
                    TechnicalDetails = ex.ToString()
                };
            }

            return new UserFailureGuidance
            {
                Cause = "Unable to start coding task.",
                DiskState = "Workspace files were left unchanged.",
                NextAction = "Select a valid repository and check that the coding runtime is configured.",
                TechnicalDetails = ex.ToString()
            };
        }

        public static UserFailureGuidance MapCodingTaskCancel(Exception ex, string? taskId = null)
        {
            string taskRef = !string.IsNullOrWhiteSpace(taskId) ? " for task " + taskId : string.Empty;
            return new UserFailureGuidance
            {
                Cause = "Unable to cancel active coding task" + taskRef + ".",
                DiskState = "Task execution state is unchanged.",
                NextAction = "Verify whether the agent process is still responding or restart the service.",
                TechnicalDetails = ex.ToString()
            };
        }

        public static UserFailureGuidance MapCodingTaskApply(Exception ex)
        {
            return new UserFailureGuidance
            {
                Cause = "Could not apply reviewed changes.",
                DiskState = "Repository files were not modified; reviewed changes remain intact.",
                NextAction = "Verify workspace clean status and retry applying changes.",
                TechnicalDetails = ex.ToString()
            };
        }

        public static UserFailureGuidance MapCodingApplyResult(string message)
        {
            bool conflict = message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
            bool missing = message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("missing", StringComparison.OrdinalIgnoreCase);

            if (conflict)
            {
                return new UserFailureGuidance
                {
                    Cause = "Applying changes failed due to a file conflict with current repository state.",
                    DiskState = "Conflicting changes were not applied to your repository.",
                    NextAction = "Stash or commit local working changes before applying.",
                    TechnicalDetails = message
                };
            }

            if (missing)
            {
                return new UserFailureGuidance
                {
                    Cause = "Target file or repository was not found.",
                    DiskState = "No files were changed.",
                    NextAction = "Check that the repository path is still valid.",
                    TechnicalDetails = message
                };
            }

            return new UserFailureGuidance
            {
                Cause = "Could not apply changes to the project.",
                DiskState = "No unexpected file changes were written.",
                NextAction = "Review the staged diff and retry.",
                TechnicalDetails = message
            };
        }

        public static UserFailureGuidance MapFolderSelection(Exception ex)
        {
            return new UserFailureGuidance
            {
                Cause = "Couldn't select code folder.",
                DiskState = "Existing folder selection was kept.",
                NextAction = "Choose a readable folder location with write permissions.",
                TechnicalDetails = ex.ToString()
            };
        }

        public static UserFailureGuidance MapModelSelector(Exception ex)
        {
            return new UserFailureGuidance
            {
                Cause = "Couldn't refresh model options.",
                DiskState = "Current model selection remains active.",
                NextAction = "Ensure local server is running or select an available model manually.",
                TechnicalDetails = ex.ToString()
            };
        }
    }
}
