using System;
using System.IO;

namespace PaRuX.Services
{
    public sealed record DeleteConfirmationRequest(
        string ItemId,
        string Title,
        string FilePath,
        DateTime RequestedUtc,
        string? FileIdentity = null);

    public sealed record DeleteConfirmationResult(
        bool Success,
        string ItemId,
        string? Error = null);

    /// <summary>
    /// Pure coordinator enforcing that media outputs cannot be deleted accidentally without
    /// explicit user confirmation. A confirmation is bound to the item id and to the file identity
    /// captured when deletion was requested, so a different file that now occupies the same path is not deleted.
    /// </summary>
    public sealed class MediaGalleryDeleteCoordinator
    {
        private readonly object _gate = new();
        private DeleteConfirmationRequest? _pending;

        public bool HasPendingRequest
        {
            get { lock (_gate) return _pending != null; }
        }

        public string? PendingItemId
        {
            get { lock (_gate) return _pending?.ItemId; }
        }

        public DeleteConfirmationRequest RequestDelete(string itemId, string title, string filePath)
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item ID is required.", nameof(itemId));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required.", nameof(filePath));

            string trimmedPath = filePath.Trim();
            string? identity = CaptureFileIdentity(trimmedPath);

            lock (_gate)
            {
                var req = new DeleteConfirmationRequest(
                    itemId.Trim(),
                    string.IsNullOrWhiteSpace(title) ? "Media Output" : title.Trim(),
                    trimmedPath,
                    DateTime.UtcNow,
                    identity);
                _pending = req;
                return req;
            }
        }

        public DeleteConfirmationResult ConfirmDelete(string itemId, Func<string, bool> fileDeleter)
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("Item ID is required.", nameof(itemId));
            if (fileDeleter == null) throw new ArgumentNullException(nameof(fileDeleter));

            DeleteConfirmationRequest? target;
            lock (_gate)
            {
                if (_pending == null || !string.Equals(_pending.ItemId, itemId.Trim(), StringComparison.Ordinal))
                {
                    return new DeleteConfirmationResult(
                        Success: false,
                        ItemId: itemId,
                        Error: "Deletion requires prior user confirmation.");
                }
                target = _pending;
                _pending = null;
            }

            try
            {
                // The pending request is consumed above; a file that changed since the request is never deleted unseen.
                if (!string.Equals(CaptureFileIdentity(target.FilePath), target.FileIdentity, StringComparison.Ordinal))
                {
                    return new DeleteConfirmationResult(
                        Success: false,
                        ItemId: target.ItemId,
                        Error: "The file changed after deletion was requested. Review it in the gallery and confirm again.");
                }

                bool deleted = fileDeleter(target.FilePath);
                return new DeleteConfirmationResult(
                    Success: deleted,
                    ItemId: target.ItemId,
                    Error: deleted ? null : "File could not be removed from disk.");
            }
            catch (Exception ex)
            {
                return new DeleteConfirmationResult(
                    Success: false,
                    ItemId: target.ItemId,
                    Error: ex.Message);
            }
        }

        public bool CancelDelete(string itemId)
        {
            lock (_gate)
            {
                if (_pending != null && string.Equals(_pending.ItemId, itemId.Trim(), StringComparison.Ordinal))
                {
                    _pending = null;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Identity of the file at the path (full path, length and last write time), or null when no file exists there.
        /// </summary>
        private static string? CaptureFileIdentity(string filePath) =>
            File.Exists(filePath) ? MediaGalleryItemMapper.ComputeFileBackedId(filePath) : null;
    }
}
