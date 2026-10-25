using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PaRuX.Contracts;

namespace PaRuX.Server;

/// <summary>
/// Scans models currently available on local disk at execution time and automatically maps
/// each role (standard, coding, reasoning, planner, reviewer, vision, uncensored, fast)
/// to the optimal local model based on capability detection and hardware fit.
/// </summary>
public static class ModelRoleAutoAssigner
{
    public static RoleAutoAssignResponse AutoAssignRoles(
        IReadOnlyList<ManagedModelInfo> availableModels,
        RuntimeManagerStatus? status,
        GpuInventoryStatus? inventory)
    {
        var response = new RoleAutoAssignResponse();
        if (availableModels == null || availableModels.Count == 0)
        {
            var fallbackModels = new List<ManagedModelInfo>();
            if (File.Exists(LocalAiPaths.StandardModel))
            {
                var fi = new FileInfo(LocalAiPaths.StandardModel);
                fallbackModels.Add(new ManagedModelInfo { FileName = fi.Name, FilePath = fi.FullName, SizeBytes = fi.Length });
            }
            if (File.Exists(LocalAiPaths.SmallModel))
            {
                var fi = new FileInfo(LocalAiPaths.SmallModel);
                fallbackModels.Add(new ManagedModelInfo { FileName = fi.Name, FilePath = fi.FullName, SizeBytes = fi.Length });
            }
            if (fallbackModels.Count == 0)
            {
                response.Success = false;
                response.Summary = "No local models available on disk to assign to roles.";
                return response;
            }
            availableModels = fallbackModels;
        }

        RuntimeHardwareProfile? profile = status != null ? RuntimeHardwareProfileResolver.Resolve(status, inventory) : null;
        long vram = profile?.AcceleratorEligible == true ? profile.Value.DedicatedVideoMemoryBytes : 0;
        long ram = status?.RamTotalBytes ?? 0;

        var scoredModels = availableModels
            .Where(m => File.Exists(m.FilePath))
            .Select(m =>
            {
                var caps = FirstModelAdvisor.InferCapabilities(m.FileName, m.Alias, m.RepoId);
                var contentProfile = FirstModelAdvisor.InferContentProfile(m.FileName, m.Alias);
                var suitability = FirstModelAdvisor.Classify(m.SizeBytes, vram, ram);
                return new
                {
                    Model = m,
                    Capabilities = caps,
                    ContentProfile = contentProfile,
                    Suitability = suitability,
                    IsGpuFit = suitability == ModelSuitability.FitsOnGpu,
                    IsRamFit = suitability is ModelSuitability.FitsOnGpu or ModelSuitability.SplitWithRam or ModelSuitability.CpuOnly
                };
            })
            .Where(m => m.IsRamFit)
            .ToList();

        if (scoredModels.Count == 0)
        {
            response.Success = false;
            response.Summary = "Available local models exceed available system memory.";
            return response;
        }

        var standardPick = scoredModels
            .Where(m => m.ContentProfile == ModelContentProfileIds.Standard)
            .OrderByDescending(m => m.IsGpuFit)
            .ThenByDescending(m => m.Model.SizeBytes)
            .FirstOrDefault() ?? scoredModels.First();

        Assign(response, "standard", standardPick.Model, "Best general-chat fit for available hardware (" + standardPick.Suitability + ").");

        var codingPick = scoredModels
            .Where(m => m.Capabilities.Contains(ModelCapabilityIds.Coding) && m.ContentProfile == ModelContentProfileIds.Standard)
            .OrderByDescending(m => m.IsGpuFit)
            .ThenByDescending(m => m.Model.SizeBytes)
            .FirstOrDefault() ?? standardPick;
        Assign(response, "coding", codingPick.Model, codingPick == standardPick
            ? "Assigned standard model as general coding fallback."
            : "Specialized coding candidate matching available hardware.");

        var reasoningPick = scoredModels
            .Where(m => m.Capabilities.Contains(ModelCapabilityIds.Reasoning) && m.ContentProfile == ModelContentProfileIds.Standard)
            .OrderByDescending(m => m.IsGpuFit)
            .ThenByDescending(m => m.Model.SizeBytes)
            .FirstOrDefault() ?? standardPick;
        Assign(response, "reasoning", reasoningPick.Model, reasoningPick == standardPick
            ? "Assigned standard model as general reasoning fallback."
            : "Specialized multi-step reasoning candidate matching available hardware.");

        var visionPick = scoredModels
            .Where(m => m.Capabilities.Contains(ModelCapabilityIds.Vision))
            .OrderByDescending(m => m.IsGpuFit)
            .ThenByDescending(m => m.Model.SizeBytes)
            .FirstOrDefault();
        if (visionPick != null)
        {
            Assign(response, "vision", visionPick.Model, "Vision-language model candidate matching available hardware.");
        }

        var uncensoredPick = scoredModels
            .Where(m => m.ContentProfile == ModelContentProfileIds.Uncensored)
            .OrderByDescending(m => m.IsGpuFit)
            .ThenByDescending(m => m.Model.SizeBytes)
            .FirstOrDefault();
        if (uncensoredPick != null)
        {
            Assign(response, "uncensored", uncensoredPick.Model, "Explicit uncensored candidate matching available hardware.");
        }

        var fastPick = scoredModels
            .OrderByDescending(m => m.IsGpuFit)
            .ThenBy(m => m.Model.SizeBytes)
            .FirstOrDefault() ?? standardPick;
        Assign(response, "fast", fastPick.Model, "Smallest/fastest response candidate matching available hardware.");

        Assign(response, "planner", reasoningPick.Model, "Assigned reasoning specialist for planning and task decomposition.");
        Assign(response, "reviewer", codingPick.Model, "Assigned coding specialist for code reviews and audits.");

        response.Success = true;
        response.Summary = $"Automatically assigned {response.Assignments.Count} roles from {scoredModels.Count} available local models."
            + (response.Skipped.Count > 0 ? $" {response.Skipped.Count} role(s) were left unchanged (your own assignments are never replaced)." : string.Empty);
        return response;
    }

    private static void Assign(RoleAutoAssignResponse response, string role, ManagedModelInfo model, string reason)
    {
        // Never replaces an override the user owns, and reports instead of swallowing failures.
        if (!LocalAiPaths.TrySetAutoAssignedRoleOverride(role, model.FilePath, out string? skipped))
        {
            response.Skipped.Add(role + ": " + skipped);
            return;
        }
        response.Assignments.Add(new RoleAutoAssignItem
        {
            Role = role,
            ModelPath = model.FilePath,
            ModelName = model.Alias ?? model.FileName,
            Reason = reason
        });
    }
}
