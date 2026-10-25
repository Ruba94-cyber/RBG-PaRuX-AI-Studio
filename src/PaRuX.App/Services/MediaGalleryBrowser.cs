using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PaRuX.Services
{
    public enum MediaGalleryFilter
    {
        All,
        ImagesOnly,
        VideosOnly,
        FinalOnly
    }

    public enum MediaGallerySort
    {
        NewestFirst,
        OldestFirst,
        TitleAscending,
        SizeDescending
    }

    /// <summary>
    /// Pure query engine for filtering, searching, and sorting media gallery items,
    /// and validating allowed user actions.
    /// </summary>
    public static class MediaGalleryBrowser
    {
        public static IReadOnlyList<MediaGalleryItem> FilterAndSort(
            IEnumerable<MediaGalleryItem> items,
            MediaGalleryFilter filter,
            MediaGallerySort sort,
            string? searchQuery = null)
        {
            if (items == null) return Array.Empty<MediaGalleryItem>();

            IEnumerable<MediaGalleryItem> query = items;

            // Apply media kind / status filter
            query = filter switch
            {
                MediaGalleryFilter.ImagesOnly => query.Where(i => i.IsImage),
                MediaGalleryFilter.VideosOnly => query.Where(i => i.IsVideo),
                MediaGalleryFilter.FinalOnly => query.Where(i => string.Equals(i.Status, "final", StringComparison.OrdinalIgnoreCase)),
                _ => query
            };

            // Apply search filter (prompt, title, model)
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string term = searchQuery.Trim();
                query = query.Where(i =>
                    i.Prompt.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.Model.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(i.FilePath).Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            // Apply sort
            query = sort switch
            {
                MediaGallerySort.OldestFirst => query.OrderBy(i => i.CreatedUtc),
                MediaGallerySort.TitleAscending => query.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
                MediaGallerySort.SizeDescending => query.OrderByDescending(i => i.SizeBytes ?? 0L),
                _ => query.OrderByDescending(i => i.CreatedUtc)
            };

            return query.ToList();
        }

        public static bool CanOpen(MediaGalleryItem? item) =>
            item != null && !string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath);

        public static bool CanReveal(MediaGalleryItem? item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return false;
            if (File.Exists(item.FilePath)) return true;
            string? dir = Path.GetDirectoryName(item.FilePath);
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
        }

        public static bool CanReuseAsEditSource(MediaGalleryItem? item) =>
            item != null
            && item.IsImage
            && !item.IsVideo
            && !string.IsNullOrWhiteSpace(item.FilePath)
            && File.Exists(item.FilePath);

        public static bool CanDelete(MediaGalleryItem? item) =>
            item != null && !string.IsNullOrWhiteSpace(item.FilePath);
    }
}
