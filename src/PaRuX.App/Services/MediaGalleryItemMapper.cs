using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Pure mapper translating Artifact contracts and file-system artifacts into MediaGalleryItems.
    /// Safely extracts prompt, model, resolution, duration, and status from heterogeneous metadata schemas.
    /// Ensures file-backed gallery items keep a stable identity derived from full path, length, and last write time.
    /// </summary>
    public static class MediaGalleryItemMapper
    {
        public static MediaGalleryItem FromArtifact(ArtifactDto artifact, ArtifactVersionDto? version = null)
        {
            if (artifact == null) throw new ArgumentNullException(nameof(artifact));

            string contentRef = version?.ContentRef ?? string.Empty;
            string metadataJson = version?.MetadataJson ?? string.Empty;

            string prompt = string.Empty;
            string model = string.Empty;
            int? width = null;
            int? height = null;
            double? duration = null;
            long? sizeBytes = null;
            string mediaType = string.Empty;

            if (!string.IsNullOrWhiteSpace(metadataJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(metadataJson);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("prompt", out var pElem) && pElem.ValueKind == JsonValueKind.String)
                        prompt = pElem.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("userPrompt", out var upElem) && upElem.ValueKind == JsonValueKind.String)
                        prompt = upElem.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("description", out var descElem) && descElem.ValueKind == JsonValueKind.String)
                        prompt = descElem.GetString() ?? string.Empty;

                    if (root.TryGetProperty("model", out var mElem) && mElem.ValueKind == JsonValueKind.String)
                        model = mElem.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("checkpoint", out var cpElem) && cpElem.ValueKind == JsonValueKind.String)
                        model = cpElem.GetString() ?? string.Empty;
                    else if (root.TryGetProperty("engine", out var engElem) && engElem.ValueKind == JsonValueKind.String)
                        model = engElem.GetString() ?? string.Empty;

                    if (root.TryGetProperty("width", out var wElem) && wElem.TryGetInt32(out int wVal))
                        width = wVal;
                    if (root.TryGetProperty("height", out var hElem) && hElem.TryGetInt32(out int hVal))
                        height = hVal;
                    if (root.TryGetProperty("durationSeconds", out var durElem) && durElem.TryGetDouble(out double durVal))
                        duration = durVal;
                    if (root.TryGetProperty("sizeBytes", out var szElem) && szElem.TryGetInt64(out long szVal))
                        sizeBytes = szVal;

                    if (root.TryGetProperty("mediaType", out var mtElem) && mtElem.ValueKind == JsonValueKind.String)
                        mediaType = mtElem.GetString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(prompt) && root.TryGetProperty("operation", out var opElem) && opElem.ValueKind == JsonValueKind.String)
                    {
                        string op = opElem.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(op))
                            prompt = $"{char.ToUpperInvariant(op[0])}{op[1..]} generation";
                    }
                }
                catch
                {
                    // Degrade gracefully on malformed metadata JSON.
                }
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                if (!string.IsNullOrWhiteSpace(version?.Comment))
                    prompt = version.Comment;
                else if (!string.IsNullOrWhiteSpace(artifact.Title))
                    prompt = artifact.Title;
                else
                    prompt = "Generated media output";
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                if (!string.IsNullOrWhiteSpace(artifact.Source))
                    model = artifact.Source switch
                    {
                        "create-workspace" => "Create Studio",
                        "chat-generation" => "Chat Generation",
                        _ => artifact.Source
                    };
                else
                    model = "Local Media Engine";
            }

            if (string.IsNullOrWhiteSpace(mediaType))
                mediaType = InferMediaType(contentRef, artifact.ArtifactType);

            DateTime createdUtc = version?.CreatedUtc ?? artifact.CreatedUtc;

            return new MediaGalleryItem
            {
                Id = artifact.Id,
                ArtifactId = artifact.Id,
                Title = string.IsNullOrWhiteSpace(artifact.Title) ? Path.GetFileName(contentRef) : artifact.Title,
                ArtifactType = string.IsNullOrWhiteSpace(artifact.ArtifactType) ? "image" : artifact.ArtifactType,
                MediaType = mediaType,
                FilePath = contentRef,
                Prompt = prompt,
                Model = model,
                CreatedUtc = createdUtc,
                Status = string.IsNullOrWhiteSpace(artifact.Status) ? "final" : artifact.Status,
                Width = width,
                Height = height,
                DurationSeconds = duration,
                SizeBytes = sizeBytes
            };
        }

        public static MediaGalleryItem FromFile(
            string filePath,
            string? prompt = null,
            string? model = null,
            DateTime? createdUtc = null,
            string status = "final",
            string? artifactId = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path must not be empty.", nameof(filePath));

            string fileName = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isVideo = ext is ".mp4" or ".webm" or ".mov" or ".mkv";
            string artifactType = isVideo ? "video" : "image";
            string mediaType = InferMediaType(filePath, artifactType);

            long? sizeBytes = null;
            DateTime timestamp = createdUtc ?? DateTime.UtcNow;
            long length = 0;
            long lastWriteTicks = 0;

            if (File.Exists(filePath))
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    sizeBytes = fi.Length;
                    length = fi.Length;
                    lastWriteTicks = fi.LastWriteTimeUtc.Ticks;
                    if (!createdUtc.HasValue) timestamp = fi.CreationTimeUtc;
                }
                catch { }
            }

            string id = !string.IsNullOrWhiteSpace(artifactId)
                ? artifactId
                : ComputeFileBackedId(filePath, length, lastWriteTicks);

            return new MediaGalleryItem
            {
                Id = id,
                ArtifactId = artifactId,
                Title = fileName,
                ArtifactType = artifactType,
                MediaType = mediaType,
                FilePath = filePath,
                Prompt = string.IsNullOrWhiteSpace(prompt) ? fileName : prompt.Trim(),
                Model = string.IsNullOrWhiteSpace(model) ? "Local Media Engine" : model.Trim(),
                CreatedUtc = timestamp,
                Status = string.IsNullOrWhiteSpace(status) ? "final" : status.Trim(),
                SizeBytes = sizeBytes
            };
        }

        /// <summary>
        /// Derives a stable, deterministic identifier from the file's full path, length, and last write time.
        /// Stays stable across refreshes for the same file, and changes if the file is replaced or modified.
        /// </summary>
        public static string ComputeFileBackedId(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path must not be empty.", nameof(filePath));

            long length = 0;
            long lastWriteTicks = 0;
            if (File.Exists(filePath))
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    length = fi.Length;
                    lastWriteTicks = fi.LastWriteTimeUtc.Ticks;
                }
                catch { }
            }

            return ComputeFileBackedId(filePath, length, lastWriteTicks);
        }

        public static string ComputeFileBackedId(string filePath, long length, long lastWriteTicks)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path must not be empty.", nameof(filePath));

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch
            {
                fullPath = filePath;
            }

            string key = $"{fullPath.ToLowerInvariant()}|{length}|{lastWriteTicks}";
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            return $"file-{Convert.ToHexString(hash).ToLowerInvariant()}";
        }

        public static string InferMediaType(string filePath, string fallbackType = "image")
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".wav" => "audio/wav",
                ".mp3" => "audio/mpeg",
                _ => string.Equals(fallbackType, "video", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "image/png"
            };
        }
    }
}
