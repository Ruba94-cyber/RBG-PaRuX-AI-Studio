using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests
{
    [Collection("MediaEditSourceBridge singleton")]
    public sealed class MediaGalleryBrowserTests
    {
        [Fact]
        public void FromArtifact_with_create_workspace_metadata_extracts_prompt_model_and_dimensions()
        {
            var artifact = new ArtifactDto
            {
                Id = "art-image-1",
                ArtifactType = "image",
                Title = "Cyberpunk cityscape",
                Status = "final",
                Source = "create-workspace",
                CurrentVersionNumber = 1,
                CreatedUtc = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc),
                UpdatedUtc = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc)
            };

            var meta = new
            {
                operation = "image",
                prompt = "A rainy cyberpunk neon street in Tokyo at midnight",
                model = "Flux.1-schnell",
                width = 1024,
                height = 768,
                mediaType = "image/png",
                steps = 4,
                seed = 42
            };

            var version = new ArtifactVersionDto
            {
                Id = "ver-1",
                ArtifactId = artifact.Id,
                VersionNumber = 1,
                ContentRef = "C:\\test\\generated\\images\\city.png",
                MetadataJson = JsonSerializer.Serialize(meta),
                CreatedUtc = artifact.CreatedUtc
            };

            MediaGalleryItem item = MediaGalleryItemMapper.FromArtifact(artifact, version);

            Assert.Equal("art-image-1", item.Id);
            Assert.Equal("A rainy cyberpunk neon street in Tokyo at midnight", item.Prompt);
            Assert.Equal("Flux.1-schnell", item.Model);
            Assert.Equal(1024, item.Width);
            Assert.Equal(768, item.Height);
            Assert.Equal("1024x768", item.FormattedDimensions);
            Assert.Equal("final", item.Status);
            Assert.True(item.IsImage);
            Assert.False(item.IsVideo);
            Assert.Equal("C:\\test\\generated\\images\\city.png", item.FilePath);
            Assert.Contains("Flux.1-schnell", item.FormattedDetails, StringComparison.Ordinal);
            Assert.Contains("1024x768", item.FormattedDetails, StringComparison.Ordinal);
            Assert.Equal("[FINAL] Image · Flux.1-schnell", item.BadgeText);
        }

        [Fact]
        public void FromArtifact_with_chat_metadata_extracts_details_and_handles_missing_fields()
        {
            var artifact = new ArtifactDto
            {
                Id = "art-video-2",
                ArtifactType = "video",
                Title = "sunset-beach.mp4",
                Status = "draft",
                Source = "chat-generation",
                CurrentVersionNumber = 2,
                CreatedUtc = new DateTime(2026, 10, 19, 15, 30, 0, DateTimeKind.Utc),
                UpdatedUtc = new DateTime(2026, 10, 19, 15, 35, 0, DateTimeKind.Utc)
            };

            var meta = new
            {
                conversationId = "conv-999",
                mediaType = "video/mp4",
                durationSeconds = 4.5,
                sizeBytes = 10485760L
            };

            var version = new ArtifactVersionDto
            {
                Id = "ver-2",
                ArtifactId = artifact.Id,
                VersionNumber = 2,
                ContentRef = "C:\\test\\generated\\videos\\sunset.mp4",
                MetadataJson = JsonSerializer.Serialize(meta),
                Comment = "High frame rate sunset video",
                CreatedUtc = artifact.UpdatedUtc
            };

            MediaGalleryItem item = MediaGalleryItemMapper.FromArtifact(artifact, version);

            Assert.Equal("art-video-2", item.Id);
            Assert.Equal("High frame rate sunset video", item.Prompt);
            Assert.Equal("Chat Generation", item.Model);
            Assert.Equal(4.5, item.DurationSeconds);
            Assert.Equal("4.5s", item.FormattedDimensions);
            Assert.Equal(10485760L, item.SizeBytes);
            Assert.True(item.IsVideo);
            Assert.False(item.IsImage);
            Assert.Equal("draft", item.Status);
            Assert.Equal("[DRAFT] Video · Chat Generation", item.BadgeText);
        }

        [Fact]
        public void FromArtifact_with_operation_property_extracts_capitalized_prompt()
        {
            var artifact = new ArtifactDto
            {
                Id = "art-op",
                ArtifactType = "image",
                Title = "",
                Status = "final",
                CurrentVersionNumber = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            var version = new ArtifactVersionDto
            {
                Id = "ver-op",
                ArtifactId = artifact.Id,
                VersionNumber = 1,
                ContentRef = "C:\\test\\render.png",
                MetadataJson = "{\"operation\":\"inpaint\"}",
                CreatedUtc = DateTime.UtcNow
            };

            MediaGalleryItem item = MediaGalleryItemMapper.FromArtifact(artifact, version);
            Assert.Equal("Inpaint generation", item.Prompt);
        }

        [Fact]
        public void FromArtifact_with_malformed_metadata_falls_back_gracefully()
        {
            var artifact = new ArtifactDto
            {
                Id = "art-fallback",
                ArtifactType = "image",
                Title = "Abstract Art",
                Status = "final",
                CurrentVersionNumber = 1,
                CreatedUtc = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc),
                UpdatedUtc = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc)
            };

            var version = new ArtifactVersionDto
            {
                Id = "ver-corrupt",
                ArtifactId = artifact.Id,
                VersionNumber = 1,
                ContentRef = "C:\\test\\abstract.png",
                MetadataJson = "{ invalid json content ...",
                CreatedUtc = artifact.CreatedUtc
            };

            MediaGalleryItem item = MediaGalleryItemMapper.FromArtifact(artifact, version);

            Assert.Equal("art-fallback", item.Id);
            Assert.Equal("Abstract Art", item.Prompt);
            Assert.Equal("Local Media Engine", item.Model);
            Assert.Equal("image/png", item.MediaType);
            Assert.True(item.IsImage);
        }

        [Fact]
        public void FromFile_infers_media_type_and_sets_properties()
        {
            string tempImage = Path.Combine(Path.GetTempPath(), $"parux_test_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempImage, new byte[] { 1, 2, 3, 4 });
            try
            {
                MediaGalleryItem item = MediaGalleryItemMapper.FromFile(
                    tempImage,
                    prompt: "Generated avatar portrait",
                    model: "SDXL-Turbo",
                    status: "final");

                Assert.Equal("Generated avatar portrait", item.Prompt);
                Assert.Equal("SDXL-Turbo", item.Model);
                Assert.Equal("image/png", item.MediaType);
                Assert.True(item.IsImage);
                Assert.False(item.IsVideo);
                Assert.True(item.FileExists);
                Assert.Equal(4, item.SizeBytes);
            }
            finally
            {
                if (File.Exists(tempImage)) File.Delete(tempImage);
            }
        }

        [Fact]
        public void FilterAndSort_filters_by_kind_and_status()
        {
            var items = new List<MediaGalleryItem>
            {
                new()
                {
                    Id = "1", Title = "Image 1", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "img1.png", Prompt = "Prompt A", Model = "Model 1",
                    CreatedUtc = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc), Status = "final"
                },
                new()
                {
                    Id = "2", Title = "Video 1", ArtifactType = "video", MediaType = "video/mp4",
                    FilePath = "vid1.mp4", Prompt = "Prompt B", Model = "Model 2",
                    CreatedUtc = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc), Status = "draft"
                },
                new()
                {
                    Id = "3", Title = "Image 2", ArtifactType = "image", MediaType = "image/jpeg",
                    FilePath = "img2.jpg", Prompt = "Prompt C", Model = "Model 1",
                    CreatedUtc = new DateTime(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc), Status = "draft"
                }
            };

            IReadOnlyList<MediaGalleryItem> imagesOnly = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.ImagesOnly, MediaGallerySort.NewestFirst);
            Assert.Equal(2, imagesOnly.Count);
            Assert.All(imagesOnly, i => Assert.True(i.IsImage));

            IReadOnlyList<MediaGalleryItem> videosOnly = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.VideosOnly, MediaGallerySort.NewestFirst);
            Assert.Single(videosOnly);
            Assert.True(videosOnly[0].IsVideo);

            IReadOnlyList<MediaGalleryItem> finalOnly = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.FinalOnly, MediaGallerySort.NewestFirst);
            Assert.Single(finalOnly);
            Assert.Equal("1", finalOnly[0].Id);
        }

        [Fact]
        public void FilterAndSort_searches_by_prompt_and_model_case_insensitively()
        {
            var items = new List<MediaGalleryItem>
            {
                new()
                {
                    Id = "1", Title = "Neon dragon", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "dragon.png", Prompt = "Glowing neon dragon flying over mountains", Model = "Flux-Dev",
                    CreatedUtc = DateTime.UtcNow, Status = "final"
                },
                new()
                {
                    Id = "2", Title = "Forest path", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "forest.png", Prompt = "Serene misty forest with tall redwood trees", Model = "SDXL",
                    CreatedUtc = DateTime.UtcNow, Status = "final"
                }
            };

            IReadOnlyList<MediaGalleryItem> promptMatch = MediaGalleryBrowser.FilterAndSort(
                items, MediaGalleryFilter.All, MediaGallerySort.NewestFirst, searchQuery: "dragon");
            Assert.Single(promptMatch);
            Assert.Equal("1", promptMatch[0].Id);

            IReadOnlyList<MediaGalleryItem> modelMatch = MediaGalleryBrowser.FilterAndSort(
                items, MediaGalleryFilter.All, MediaGallerySort.NewestFirst, searchQuery: "sdxl");
            Assert.Single(modelMatch);
            Assert.Equal("2", modelMatch[0].Id);

            IReadOnlyList<MediaGalleryItem> noMatch = MediaGalleryBrowser.FilterAndSort(
                items, MediaGalleryFilter.All, MediaGallerySort.NewestFirst, searchQuery: "spaceship");
            Assert.Empty(noMatch);
        }

        [Fact]
        public void FilterAndSort_sorts_correctly()
        {
            var t1 = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc);
            var t3 = new DateTime(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc);

            var items = new List<MediaGalleryItem>
            {
                new()
                {
                    Id = "B", Title = "Bravo", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "b.png", Prompt = "p", Model = "m", CreatedUtc = t2, Status = "final", SizeBytes = 200
                },
                new()
                {
                    Id = "A", Title = "Alpha", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "a.png", Prompt = "p", Model = "m", CreatedUtc = t1, Status = "final", SizeBytes = 500
                },
                new()
                {
                    Id = "C", Title = "Charlie", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "c.png", Prompt = "p", Model = "m", CreatedUtc = t3, Status = "final", SizeBytes = 100
                }
            };

            IReadOnlyList<MediaGalleryItem> newest = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.All, MediaGallerySort.NewestFirst);
            Assert.Equal(new[] { "C", "B", "A" }, newest.Select(i => i.Id));

            IReadOnlyList<MediaGalleryItem> oldest = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.All, MediaGallerySort.OldestFirst);
            Assert.Equal(new[] { "A", "B", "C" }, oldest.Select(i => i.Id));

            IReadOnlyList<MediaGalleryItem> byTitle = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.All, MediaGallerySort.TitleAscending);
            Assert.Equal(new[] { "A", "B", "C" }, byTitle.Select(i => i.Id));

            IReadOnlyList<MediaGalleryItem> bySize = MediaGalleryBrowser.FilterAndSort(items, MediaGalleryFilter.All, MediaGallerySort.SizeDescending);
            Assert.Equal(new[] { "A", "B", "C" }, bySize.Select(i => i.Id));
        }

        [Fact]
        public void CanReuseAsEditSource_requires_valid_image_and_existing_file()
        {
            string tempImage = Path.Combine(Path.GetTempPath(), $"parux_reuse_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempImage, new byte[] { 1, 2, 3 });
            try
            {
                var validImage = new MediaGalleryItem
                {
                    Id = "valid", Title = "Valid", ArtifactType = "image", MediaType = "image/png",
                    FilePath = tempImage, Prompt = "p", Model = "m", CreatedUtc = DateTime.UtcNow, Status = "final"
                };
                Assert.True(MediaGalleryBrowser.CanReuseAsEditSource(validImage));

                var nonExistentImage = new MediaGalleryItem
                {
                    Id = "ghost", Title = "Ghost", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "C:\\does\\not\\exist.png", Prompt = "p", Model = "m", CreatedUtc = DateTime.UtcNow, Status = "final"
                };
                Assert.False(MediaGalleryBrowser.CanReuseAsEditSource(nonExistentImage));

                var videoItem = new MediaGalleryItem
                {
                    Id = "vid", Title = "Vid", ArtifactType = "video", MediaType = "video/mp4",
                    FilePath = tempImage, Prompt = "p", Model = "m", CreatedUtc = DateTime.UtcNow, Status = "final"
                };
                Assert.False(MediaGalleryBrowser.CanReuseAsEditSource(videoItem));
            }
            finally
            {
                if (File.Exists(tempImage)) File.Delete(tempImage);
            }
        }

        [Fact]
        public void CanOpen_and_CanReveal_validate_file_and_directory_existence()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"parux_open_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempFile, new byte[] { 42 });
            try
            {
                var existingItem = new MediaGalleryItem
                {
                    Id = "ex", Title = "Existing", ArtifactType = "image", MediaType = "image/png",
                    FilePath = tempFile, Prompt = "p", Model = "m", CreatedUtc = DateTime.UtcNow, Status = "final"
                };
                Assert.True(MediaGalleryBrowser.CanOpen(existingItem));
                Assert.True(MediaGalleryBrowser.CanReveal(existingItem));

                string nonExistentPathInExistingDir = Path.Combine(Path.GetTempPath(), $"parux_missing_{Guid.NewGuid():N}.png");
                var missingItem = new MediaGalleryItem
                {
                    Id = "missing", Title = "Missing", ArtifactType = "image", MediaType = "image/png",
                    FilePath = nonExistentPathInExistingDir, Prompt = "p", Model = "m", CreatedUtc = DateTime.UtcNow, Status = "final"
                };
                Assert.False(MediaGalleryBrowser.CanOpen(missingItem));
                Assert.True(MediaGalleryBrowser.CanReveal(missingItem)); // directory exists

                var emptyItem = new MediaGalleryItem
                {
                    Id = "empty", Title = "Empty", ArtifactType = "image", MediaType = "image/png",
                    FilePath = "", Prompt = "p", Model = "m", CreatedUtc = DateTime.UtcNow, Status = "final"
                };
                Assert.False(MediaGalleryBrowser.CanOpen(emptyItem));
                Assert.False(MediaGalleryBrowser.CanReveal(emptyItem));
                Assert.False(MediaGalleryBrowser.CanDelete(emptyItem));
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void DeleteCoordinator_enforces_prior_confirmation_before_deletion()
        {
            var coordinator = new MediaGalleryDeleteCoordinator();
            string testFile = Path.Combine(Path.GetTempPath(), $"parux_del_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(testFile, new byte[] { 10, 20 });

            try
            {
                // Attempting delete without RequestDelete must fail
                DeleteConfirmationResult unconfirmed = coordinator.ConfirmDelete("item-1", path =>
                {
                    File.Delete(path);
                    return true;
                });
                Assert.False(unconfirmed.Success);
                Assert.Contains("requires prior user confirmation", unconfirmed.Error, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(testFile));

                // Request delete sets pending state
                DeleteConfirmationRequest req = coordinator.RequestDelete("item-1", "Test Output", testFile);
                Assert.True(coordinator.HasPendingRequest);
                Assert.Equal("item-1", coordinator.PendingItemId);
                Assert.Equal("Test Output", req.Title);

                // Cancel delete clears pending state
                Assert.True(coordinator.CancelDelete("item-1"));
                Assert.False(coordinator.HasPendingRequest);

                // Re-request and confirm delete
                coordinator.RequestDelete("item-1", "Test Output", testFile);
                DeleteConfirmationResult confirmed = coordinator.ConfirmDelete("item-1", path =>
                {
                    File.Delete(path);
                    return true;
                });

                Assert.True(confirmed.Success);
                Assert.False(File.Exists(testFile));
                Assert.False(coordinator.HasPendingRequest);
            }
            finally
            {
                if (File.Exists(testFile)) File.Delete(testFile);
            }
        }

        [Fact]
        public void MediaEditSourceBridge_validates_extensions_and_dispatches_events()
        {
            var bridge = MediaEditSourceBridge.Current;
            bridge.Clear();
            Assert.False(bridge.HasPendingSource);

            // Invalid non-image extension rejected
            Assert.False(bridge.SetEditSource("video.mp4", "prompt"));
            Assert.False(bridge.SetEditSource("data.json", "prompt"));
            Assert.False(bridge.HasPendingSource);

            // Valid image paths accepted and event fired
            bool eventFired = false;
            string? capturedPath = null;
            string? capturedPrompt = null;

            EventHandler<MediaEditSourceEventArgs> handler = (_, args) =>
            {
                eventFired = true;
                capturedPath = args.FilePath;
                capturedPrompt = args.Prompt;
            };

            // The bridge verifies that the source exists, so the accepted path must be a real image file.
            string samplePath = Path.Combine(Path.GetTempPath(), $"parux_bridge_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(samplePath, new byte[] { 137, 80, 78, 71 });

            bridge.EditSourceRequested += handler;
            try
            {
                bool set = bridge.SetEditSource(samplePath, "A scenic mountain vista");
                Assert.True(set);
                Assert.True(bridge.HasPendingSource);
                Assert.Equal(samplePath, bridge.PendingEditSourcePath);
                Assert.Equal("A scenic mountain vista", bridge.PendingPrompt);
                Assert.True(eventFired);
                Assert.Equal(samplePath, capturedPath);
                Assert.Equal("A scenic mountain vista", capturedPrompt);

                bridge.Clear();
                Assert.False(bridge.HasPendingSource);
                Assert.Null(bridge.PendingEditSourcePath);
                Assert.Null(bridge.PendingPrompt);
            }
            finally
            {
                bridge.EditSourceRequested -= handler;
                if (File.Exists(samplePath)) File.Delete(samplePath);
            }
        }

        [Fact]
        public void MediaEditSourceBridge_TryApplyToCreateView_handles_null_safely()
        {
            Assert.False(MediaEditSourceBridge.TryApplyToCreateView(null));
            Assert.False(MediaEditSourceBridge.TryApplyToCreateView(new object()));
        }

        [Fact]
        public void FromFile_creates_stable_id_for_same_file_across_multiple_mappings()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"parux_stable_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempFile, new byte[] { 10, 20, 30, 40, 50 });
            try
            {
                MediaGalleryItem item1 = MediaGalleryItemMapper.FromFile(tempFile);
                MediaGalleryItem item2 = MediaGalleryItemMapper.FromFile(tempFile);

                Assert.NotNull(item1.Id);
                Assert.StartsWith("file-", item1.Id, StringComparison.Ordinal);
                Assert.Equal(item1.Id, item2.Id);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void FromFile_creates_different_id_when_file_is_replaced()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"parux_replace_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempFile, new byte[] { 1, 2, 3 });
            try
            {
                MediaGalleryItem original = MediaGalleryItemMapper.FromFile(tempFile);

                // Replace file with different length and modified timestamp
                File.WriteAllBytes(tempFile, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
                File.SetLastWriteTimeUtc(tempFile, DateTime.UtcNow.AddMinutes(5));

                MediaGalleryItem replaced = MediaGalleryItemMapper.FromFile(tempFile);

                Assert.StartsWith("file-", original.Id, StringComparison.Ordinal);
                Assert.StartsWith("file-", replaced.Id, StringComparison.Ordinal);
                Assert.NotEqual(original.Id, replaced.Id);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void FromFile_with_explicit_artifactId_preserves_artifactId()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"parux_artid_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(tempFile, new byte[] { 42 });
            try
            {
                MediaGalleryItem item = MediaGalleryItemMapper.FromFile(tempFile, artifactId: "custom-artifact-777");
                Assert.Equal("custom-artifact-777", item.Id);
                Assert.Equal("custom-artifact-777", item.ArtifactId);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void ComputeFileBackedId_derives_deterministic_identifier()
        {
            string id1 = MediaGalleryItemMapper.ComputeFileBackedId("D:\\fixtures\\sample.png", 1024, 638000000000000000);
            string id2 = MediaGalleryItemMapper.ComputeFileBackedId("D:\\fixtures\\sample.png", 1024, 638000000000000000);
            string idDiffLength = MediaGalleryItemMapper.ComputeFileBackedId("D:\\fixtures\\sample.png", 2048, 638000000000000000);
            string idDiffTicks = MediaGalleryItemMapper.ComputeFileBackedId("D:\\fixtures\\sample.png", 1024, 638000000000000100);

            Assert.StartsWith("file-", id1, StringComparison.Ordinal);
            Assert.Equal(id1, id2);
            Assert.NotEqual(id1, idDiffLength);
            Assert.NotEqual(id1, idDiffTicks);
        }

        [Fact]
        public void DeleteCoordinator_confirmation_remains_valid_across_gallery_refresh_because_id_is_stable()
        {
            var coordinator = new MediaGalleryDeleteCoordinator();
            string testFile = Path.Combine(Path.GetTempPath(), $"parux_refresh_del_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(testFile, new byte[] { 99, 88, 77 });

            try
            {
                // User views gallery: item mapped from file
                MediaGalleryItem initialMapping = MediaGalleryItemMapper.FromFile(testFile);

                // User initiates delete request
                coordinator.RequestDelete(initialMapping.Id, initialMapping.Title, initialMapping.FilePath);
                Assert.True(coordinator.HasPendingRequest);
                Assert.Equal(initialMapping.Id, coordinator.PendingItemId);

                // Gallery refreshes in background or re-maps before user clicks Confirm
                MediaGalleryItem refreshedMapping = MediaGalleryItemMapper.FromFile(testFile);

                // Stable id ensures the refreshed mapping has the exact same ID as the pending request
                Assert.Equal(initialMapping.Id, refreshedMapping.Id);

                // Confirming delete using the refreshed mapping succeeds without going stale
                DeleteConfirmationResult result = coordinator.ConfirmDelete(refreshedMapping.Id, path =>
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        return true;
                    }
                    return false;
                });

                Assert.True(result.Success);
                Assert.False(File.Exists(testFile));
            }
            finally
            {
                if (File.Exists(testFile)) File.Delete(testFile);
            }
        }
    }
}
