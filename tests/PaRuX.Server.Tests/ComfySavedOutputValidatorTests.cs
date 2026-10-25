using System.IO;
using System.Text.Json;
using Xunit;

namespace PaRuX.Server.Tests;

// Saved-output selection and path containment shared by live generation and restart recovery.
public sealed class ComfySavedOutputValidatorTests
{
    private static readonly string OutputRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "parux-saved-output-validator"));

    [Fact]
    public void A_saved_file_under_the_output_root_resolves_to_its_path()
    {
        string path = ComfySavedOutputValidator.ResolvePath("image.png", "sub", OutputRoot);

        Assert.Equal(Path.Combine(OutputRoot, "sub", "image.png"), path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("sub/../..")]
    public void A_subfolder_that_leaves_the_output_root_is_refused(string subfolder)
    {
        Assert.Throws<InvalidDataException>(() => ComfySavedOutputValidator.ResolvePath("image.png", subfolder, OutputRoot));
    }

    [Fact]
    public void A_missing_save_node_is_not_replaced_by_another_nodes_output()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"11\":{\"images\":[{\"filename\":\"other.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}");

        string? path = ComfySavedOutputValidator.FindSavedMediaPath(document.RootElement, OutputRoot, "42");

        Assert.Null(path);
    }
}
