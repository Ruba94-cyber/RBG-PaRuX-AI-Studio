using System.Reflection;
using System.Text.Json.Nodes;
using PaRuX.Contracts;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaCompanionGraphTests
{
    [Theory]
    [InlineData("z-image", "image", "z_image_turbo_nvfp4.safetensors")]
    [InlineData("wan2.2-ti2v-5b", "video", "wan2.2_ti2v_5B_fp16.safetensors")]
    public void Submitted_graph_uses_resolver_selected_names_for_each_supported_family(
        string familyId,
        string type,
        string mainFileName)
    {
        MediaCompanionRegistry registry = RegistryWithSelectableNames();
        RegistryFamily family = Assert.IsType<RegistryFamily>(registry.FindFamily(familyId));
        MediaFamilyDetection detection = MediaFamilyDetector.Detect(SyntheticHeader(family), registry);
        Assert.Equal(familyId, detection.FamilyId);

        string template = Assert.IsType<string>(ConfigAssets.ReadEmbedded(family.TemplateFile!));
        JsonObject request = JsonNode.Parse(template)!.AsObject();
        Dictionary<string, IReadOnlyList<InstalledComfyFile>> installed = SelectedAlternatives(family);

        MediaCompanionPlan plan = MediaCompanionGraph.PrepareSubmissionGraph(
            request, type, detection, registry, installed);

        Assert.Equal(familyId, plan.FamilyId);
        Assert.Equal(mainFileName, plan.MainFileName);
        Assert.All(plan.Companions, companion => Assert.Equal("installed", companion.State));
        Assert.Equal(
            Assert.Single(plan.Companions, companion => companion.Role == "text_encoder").FileName,
            LoaderInput(request, "CLIPLoader", "clip_name"));
        Assert.Equal(
            Assert.Single(plan.Companions, companion => companion.Role == "vae").FileName,
            LoaderInput(request, "VAELoader", "vae_name"));
        Assert.NotEqual(
            Assert.Single(family.Companions, companion => companion.Role == "text_encoder").FileName,
            LoaderInput(request, "CLIPLoader", "clip_name"));
        Assert.NotEqual(
            Assert.Single(family.Companions, companion => companion.Role == "vae").FileName,
            LoaderInput(request, "VAELoader", "vae_name"));
    }

    private static MediaCompanionRegistry RegistryWithSelectableNames()
    {
        Assembly assembly = typeof(MediaCompanionRegistry).Assembly;
        string resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("media-companion-registry.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        JsonObject manifest = JsonNode.Parse(stream)!.AsObject();

        foreach (JsonObject family in manifest["families"]!.AsArray().OfType<JsonObject>()
            .Where(item => (string?)item["support"] == "supported"))
        {
            foreach (JsonObject companion in family["companions"]!.AsArray().OfType<JsonObject>())
            {
                companion["match"] = new JsonObject
                {
                    ["pattern"] = "*-selected.safetensors",
                    ["exclude"] = new JsonArray()
                };
            }
        }

        return MediaCompanionRegistry.Parse(manifest.ToJsonString());
    }

    private static SafetensorsHeader SyntheticHeader(RegistryFamily family)
    {
        var tensors = family.Signature.RequiredStems.Select(stem =>
        {
            string name = stem == "patch_embedding." ? "patch_embedding.weight" : stem + "synthetic.weight";
            long[] shape = name == "patch_embedding.weight" ? [1, 48] : [1];
            return new SafetensorsTensor(name, "F32", shape, 0, 1);
        }).ToList();
        return new SafetensorsHeader(1, 1, tensors, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static Dictionary<string, IReadOnlyList<InstalledComfyFile>> SelectedAlternatives(RegistryFamily family)
    {
        var folders = new Dictionary<string, List<InstalledComfyFile>>(StringComparer.Ordinal);
        foreach (RegistryCompanion companion in family.Companions)
        {
            if (!folders.TryGetValue(companion.Folder, out List<InstalledComfyFile>? files))
            {
                files = new List<InstalledComfyFile>();
                folders.Add(companion.Folder, files);
            }
            files.Add(new InstalledComfyFile(
                family.Id + "-" + companion.Role + "-selected.safetensors", 1024));
        }

        return folders.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<InstalledComfyFile>)pair.Value,
            StringComparer.Ordinal);
    }

    private static string LoaderInput(JsonObject request, string classType, string input)
    {
        JsonObject prompt = request["prompt"]!.AsObject();
        JsonObject loader = Assert.Single(prompt.Select(pair => pair.Value).OfType<JsonObject>(),
            node => (string?)node["class_type"] == classType);
        return (string?)loader["inputs"]?[input]
            ?? throw new InvalidDataException("The selected loader input is missing.");
    }
}
