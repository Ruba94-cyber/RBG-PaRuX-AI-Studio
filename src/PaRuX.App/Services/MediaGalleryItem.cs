using System;
using System.Globalization;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure, self-contained model for a media gallery item representing an image or video output.
    /// Exposes normalized prompt, model, timestamp, status, and dimensions for visual browsing.
    /// </summary>
    public sealed class MediaGalleryItem
    {
        public required string Id { get; init; }
        public string? ArtifactId { get; init; }
        public required string Title { get; init; }
        public required string ArtifactType { get; init; }
        public required string MediaType { get; init; }
        public required string FilePath { get; init; }
        public required string Prompt { get; init; }
        public required string Model { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required string Status { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public double? DurationSeconds { get; init; }
        public long? SizeBytes { get; init; }

        public bool IsVideo =>
            string.Equals(ArtifactType, "video", StringComparison.OrdinalIgnoreCase)
            || MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || FilePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || FilePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

        public bool IsImage =>
            !IsVideo
            && (string.Equals(ArtifactType, "image", StringComparison.OrdinalIgnoreCase)
                || MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || FilePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || FilePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || FilePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

        public bool FileExists => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

        public string FormattedTime => CreatedUtc.ToLocalTime().ToString("g");

        public string FormattedDimensions
        {
            get
            {
                if (Width.HasValue && Height.HasValue && Width.Value > 0 && Height.Value > 0)
                    return $"{Width.Value}x{Height.Value}";
                if (DurationSeconds.HasValue && DurationSeconds.Value > 0)
                    return $"{DurationSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture)}s";
                return string.Empty;
            }
        }

        public string FormattedDetails
        {
            get
            {
                string dims = FormattedDimensions;
                string statusUpper = string.IsNullOrWhiteSpace(Status) ? "FINAL" : Status.ToUpperInvariant();
                string modelPart = string.IsNullOrWhiteSpace(Model) ? "Local Media Engine" : Model;
                return string.IsNullOrWhiteSpace(dims)
                    ? $"{modelPart} · {statusUpper} · {FormattedTime}"
                    : $"{modelPart} · {dims} · {statusUpper} · {FormattedTime}";
            }
        }

        public string BadgeText =>
            $"[{Status.ToUpperInvariant()}] {(IsVideo ? "Video" : IsImage ? "Image" : ArtifactType)} · {Model}";
    }
}
