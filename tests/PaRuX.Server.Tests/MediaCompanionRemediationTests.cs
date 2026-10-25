using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaCompanionRemediationTests
{
    [Fact]
    public void Formats_typed_dependency_fields_and_next_action()
    {
        var error = new MediaDependencyError
        {
            Reason = "component_missing",
            Role = "text encoder",
            ExpectedFileName = "encoder.safetensors",
            ComfyFolder = "text_encoders",
            Repository = "publisher/model",
            SizeBytes = 1_572_864,
            Message = "The workflow needs a text encoder.",
            NextAction = "Fetch the declared companion or copy it into ComfyUI."
        };

        string text = MediaCompanionRemediationText.FormatFailure(error);

        Assert.Contains(error.Message, text);
        Assert.Contains("Component: text encoder", text);
        Assert.Contains("Required file: encoder.safetensors", text);
        Assert.Contains("ComfyUI folder: models/text_encoders", text);
        Assert.Contains("Repository: publisher/model", text);
        Assert.Contains("Expected size: 1.5 MiB", text);
        Assert.Contains("Next step: " + error.NextAction, text);
    }

    [Fact]
    public void Offers_fetch_for_the_matching_fetchable_plan_and_maps_img2img_to_image()
    {
        MediaDependencyError error = Missing("encoder.safetensors", "text_encoders");
        MediaCompanionPlan plan = FetchablePlan();

        Assert.Equal("image", MediaCompanionRemediationText.CompanionTypeForJob("img2img"));
        Assert.Equal("edit", MediaCompanionRemediationText.CompanionTypeForJob("edit"));
        Assert.True(MediaCompanionRemediationText.CanOfferFetch(error, plan, "img2img"));
        Assert.False(MediaCompanionRemediationText.CanOfferFetch(error, plan, "video"));
    }

    [Fact]
    public void Does_not_offer_fetch_for_updates_unrelated_files_or_unfetchable_plans()
    {
        MediaDependencyError error = Missing("encoder.safetensors", "text_encoders");
        MediaCompanionPlan plan = FetchablePlan();

        Assert.False(MediaCompanionRemediationText.CanOfferFetch(
            new MediaDependencyError { Reason = "value_not_offered", UpdateRequired = true, ExpectedFileName = error.ExpectedFileName },
            plan, "image"));
        Assert.False(MediaCompanionRemediationText.CanOfferFetch(
            Missing("other.safetensors", "text_encoders"), plan, "image"));
        Assert.False(MediaCompanionRemediationText.CanOfferFetch(
            Missing("encoder.safetensors", "clip"), plan, "image"));

        plan.Missing.Add(new MediaCompanionItem
        {
            Role = "vae",
            Folder = "vae",
            FileName = "vae.safetensors",
            Fetchable = false
        });
        Assert.False(MediaCompanionRemediationText.CanOfferFetch(error, plan, "image"));

        plan.Missing.RemoveAt(plan.Missing.Count - 1);
        plan.PlanHash = string.Empty;
        Assert.False(MediaCompanionRemediationText.CanOfferFetch(error, plan, "image"));
    }

    [Fact]
    public void Formats_the_missing_plan_with_target_folder_size_repository_and_license()
    {
        string text = MediaCompanionRemediationText.FormatPlan(FetchablePlan());

        Assert.Contains("Plan for Example family", text);
        Assert.Contains("encoder.safetensors -> ComfyUI/models/text_encoders", text);
        Assert.Contains("Size: 1.5 MiB", text);
        Assert.Contains("Repository: publisher/model", text);
        Assert.Contains("License: MIT", text);
        Assert.Contains("Only the missing files listed above", text);
    }

    [Fact]
    public void Fetch_request_carries_the_reviewed_hash_and_explicit_consent()
    {
        MediaCompanionFetchRequest request = MediaCompanionRemediationText.CreateFetchRequest(FetchablePlan());

        Assert.Equal("image", request.Type);
        Assert.Equal(new string('a', 64), request.PlanHash);
        Assert.True(request.Consent);
        Assert.Throws<InvalidOperationException>(() => MediaCompanionRemediationText.CreateFetchRequest(
            new MediaCompanionPlan { Type = "image" }));
    }

    [Fact]
    public void Retry_is_allowed_only_after_a_successful_fetch_and_only_once()
    {
        Assert.True(MediaCompanionRemediationText.ShouldRetryAfterFetch(fetchSucceeded: true, retryAlreadyAttempted: false));
        Assert.False(MediaCompanionRemediationText.ShouldRetryAfterFetch(fetchSucceeded: false, retryAlreadyAttempted: false));
        Assert.False(MediaCompanionRemediationText.ShouldRetryAfterFetch(fetchSucceeded: true, retryAlreadyAttempted: true));
    }

    private static MediaDependencyError Missing(string fileName, string folder) => new()
    {
        Reason = "component_missing",
        Role = "text encoder",
        ExpectedFileName = fileName,
        ComfyFolder = folder
    };

    private static MediaCompanionPlan FetchablePlan() => new()
    {
        Type = "image",
        FamilyName = "Example family",
        Support = "supported",
        Installable = true,
        PlanHash = new string('a', 64),
        MissingApproxBytes = 1_572_864,
        Missing =
        {
            new MediaCompanionItem
            {
                Role = "text encoder",
                Folder = "text_encoders",
                FileName = "encoder.safetensors",
                PinnedSizeBytes = 1_572_864,
                ApproxSizeBytes = 1_600_000,
                Repository = "publisher/model",
                License = "MIT",
                Fetchable = true
            }
        }
    };
}
