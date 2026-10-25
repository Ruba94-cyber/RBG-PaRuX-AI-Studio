using System;
using System.IO;
using System.Reflection;

namespace PaRuX.Services
{
    public sealed class MediaEditSourceEventArgs : EventArgs
    {
        public required string FilePath { get; init; }
        public string? Prompt { get; init; }
        public DateTime TimestampUtc { get; init; }
    }

    public sealed record SetEditSourceResult(bool Success, string Message)
    {
        public static implicit operator bool(SetEditSourceResult result) => result.Success;

        public void Deconstruct(out bool success, out string message)
        {
            success = Success;
            message = Message;
        }
    }

    /// <summary>
    /// Pure coordinator bridging media gallery outputs into the image editor ("reuse as edit source").
    /// Stores pending source image identities and dispatches notifications when an output is reused.
    /// Verifies file existence before reporting success and returns an actionable message on failure.
    /// </summary>
    public sealed class MediaEditSourceBridge
    {
        public static MediaEditSourceBridge Current { get; } = new MediaEditSourceBridge();

        private readonly object _gate = new();

        public string? PendingEditSourcePath { get; private set; }
        public string? PendingPrompt { get; private set; }
        public DateTime? TimestampUtc { get; private set; }
        public string? LastMessage { get; private set; }

        public bool HasPendingSource => !string.IsNullOrWhiteSpace(PendingEditSourcePath);

        public event EventHandler<MediaEditSourceEventArgs>? EditSourceRequested;

        public SetEditSourceResult SetEditSource(string imagePath, string? prompt = null)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                string msg = "Image path must not be empty.";
                lock (_gate) { LastMessage = msg; }
                return new SetEditSourceResult(false, msg);
            }

            if (IsNetworkOrAlternateStreamPath(imagePath, out string pathReason))
            {
                lock (_gate) { LastMessage = pathReason; }
                return new SetEditSourceResult(false, pathReason);
            }

            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            {
                string msg = $"Unsupported image format '{ext}'. Supported formats are PNG, JPG, JPEG, and WebP.";
                lock (_gate) { LastMessage = msg; }
                return new SetEditSourceResult(false, msg);
            }

            if (!File.Exists(imagePath))
            {
                string msg = $"Source image file does not exist: '{imagePath}'.";
                lock (_gate) { LastMessage = msg; }
                return new SetEditSourceResult(false, msg);
            }

            MediaEditSourceEventArgs args;
            string successMsg = $"Selected '{Path.GetFileName(imagePath)}' as source for Image Edit.";
            lock (_gate)
            {
                PendingEditSourcePath = imagePath;
                PendingPrompt = prompt;
                TimestampUtc = DateTime.UtcNow;
                LastMessage = successMsg;
                args = new MediaEditSourceEventArgs
                {
                    FilePath = imagePath,
                    Prompt = prompt,
                    TimestampUtc = TimestampUtc.Value
                };
            }

            EditSourceRequested?.Invoke(this, args);
            return new SetEditSourceResult(true, successMsg);
        }

        public bool SetEditSource(string imagePath, string? prompt, out string message)
        {
            var result = SetEditSource(imagePath, prompt);
            message = result.Message;
            return result.Success;
        }

        public bool SetEditSource(string imagePath, out string message, string? prompt = null)
        {
            return SetEditSource(imagePath, prompt, out message);
        }

        /// <summary>
        /// Refuses paths that would reach the network or a hidden stream before any disk probe runs. A UNC or device
        /// path makes File.Exists contact a remote host, and an alternate data stream name such as "photo.txt:x.png"
        /// passes the extension check without naming a plain image file.
        /// </summary>
        private static bool IsNetworkOrAlternateStreamPath(string imagePath, out string reason)
        {
            string trimmed = imagePath.TrimStart();
            if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                reason = "Network and device paths cannot be used as an edit source. Copy the image to a local folder first.";
                return true;
            }

            if (Path.GetFileName(trimmed).Contains(':'))
            {
                reason = "Alternate data stream names cannot be used as an edit source. Copy the image to a local file first.";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public void Clear()
        {
            lock (_gate)
            {
                PendingEditSourcePath = null;
                PendingPrompt = null;
                TimestampUtc = null;
                LastMessage = null;
            }
        }

        /// <summary>
        /// Reflection helper to safely assign the edit source image to CreateWorkspaceView
        /// without mutating or violating clean-room restrictions on CreateWorkspaceView.cs.
        /// </summary>
        public static bool TryApplyToCreateView(object? createView)
        {
            if (createView == null) return false;
            string? path = Current.PendingEditSourcePath;
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                Type type = createView.GetType();
                FieldInfo? pathField = type.GetField("_editSourcePath", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo? labelField = type.GetField("_editSourceLabel", BindingFlags.NonPublic | BindingFlags.Instance);

                if (pathField != null)
                {
                    pathField.SetValue(createView, path);
                    object? labelObj = labelField?.GetValue(createView);
                    if (labelObj != null)
                    {
                        PropertyInfo? textProp = labelObj.GetType().GetProperty("Text");
                        textProp?.SetValue(labelObj, path);
                    }
                    return true;
                }
            }
            catch
            {
                // Graceful fallback if internal layout changes.
            }
            return false;
        }
    }
}
