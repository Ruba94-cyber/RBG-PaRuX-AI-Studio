using System.Diagnostics;
using System.Text.Json;
using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class MediaReviewPacketTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parux-review-" + Guid.NewGuid().ToString("N"));

    public MediaReviewPacketTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeFile(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private MediaReviewPacket.Item ItemFor(string id, string task = "text-render") => new(
        id, task, "a sign that says \"OPEN\", with commas", 42, 512, 512, 8, "diffusers", "model/x", "rev1",
        MakeFile(id + ".png", "pixels-" + id));

    private string ManifestPath => Path.Combine(Packet(), "manifest.json");
    private MediaReviewPacket.Manifest ReadManifest() => JsonSerializer.Deserialize<MediaReviewPacket.Manifest>(
        File.ReadAllText(ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    private void WriteManifest(MediaReviewPacket.Manifest manifest) => File.WriteAllText(ManifestPath,
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private void MarkReviewed()
    {
        string json = File.ReadAllText(ManifestPath)
            .Replace("\"PENDING_OWNER_REVIEW\"", "\"REVIEWED_BY_OWNER\"")
            .Replace("\"reviewer\": null", "\"reviewer\": \"owner\"")
            .Replace("\"reviewedUtc\": null", "\"reviewedUtc\": \"2026-10-05T00:00:00Z\"");
        File.WriteAllText(ManifestPath, json);
    }

    private static string ValidScoredSheet(string notes = "ok") =>
        "id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
        "a,text-render,outputs/a.png,4,3,5,yes,\"" + notes.Replace("\"", "\"\"") + "\"\r\n";

    private string Packet() => Path.Combine(_root, "packet");

    [Fact]
    public void Build_records_prompts_seeds_hashes_and_stays_pending()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a"), ItemFor("b", "identity") });

        MediaReviewPacket.Evaluation eval = MediaReviewPacket.Evaluate(Packet());
        Assert.Equal(MediaReviewPacket.Outcome.Pending, eval.Outcome);
        var manifest = JsonSerializer.Deserialize<MediaReviewPacket.Manifest>(
            File.ReadAllText(Path.Combine(Packet(), "manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("PENDING_OWNER_REVIEW", manifest.Status);
        Assert.Null(manifest.Reviewer);
        Assert.Equal(42, manifest.Items[0].Seed);
        Assert.Equal(64, manifest.Items[0].OutputSha256.Length);
        string sheet = File.ReadAllText(Path.Combine(Packet(), "review-sheet.csv"));
        Assert.All(sheet.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1),
            line => Assert.EndsWith(",,,,,", line));
    }

    [Fact]
    public void Tampered_output_invalidates_the_packet()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        File.WriteAllText(Path.Combine(Packet(), "outputs", "a.png"), "different");
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Input_and_output_hashes_and_byte_counts_are_audited()
    {
        MediaReviewPacket.Item item = ItemFor("a") with { InputPath = MakeFile("source.png", "source-pixels") };
        MediaReviewPacket.Build(Packet(), new[] { item });
        Assert.Equal(MediaReviewPacket.Outcome.Pending, MediaReviewPacket.Evaluate(Packet()).Outcome);

        File.WriteAllText(Path.Combine(Packet(), "outputs", "a.input.png"), "tampered-input");
        MediaReviewPacket.Evaluation inputTamper = MediaReviewPacket.Evaluate(Packet());
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, inputTamper.Outcome);
        Assert.Contains(inputTamper.Problems, p => p.Contains("input"));

        File.WriteAllText(Path.Combine(Packet(), "outputs", "a.input.png"), "source-pixels");
        MediaReviewPacket.Manifest manifest = ReadManifest();
        manifest.Items[0].OutputBytes++;
        WriteManifest(manifest);
        MediaReviewPacket.Evaluation wrongLength = MediaReviewPacket.Evaluate(Packet());
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, wrongLength.Outcome);
        Assert.Contains(wrongLength.Problems, p => p.Contains("byte count"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("C:/outside.png")]
    [InlineData("outputs/a\0.png")]
    public void Unsafe_manifest_paths_are_invalid(string path)
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        MediaReviewPacket.Manifest manifest = ReadManifest();
        manifest.Items[0].OutputFile = path;
        WriteManifest(manifest);
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Null_manifest_items_and_duplicate_content_paths_are_invalid()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a"), ItemFor("b") });
        MediaReviewPacket.Manifest manifest = ReadManifest();
        manifest.Items[1].OutputFile = manifest.Items[0].OutputFile;
        manifest.Items[1].OutputSha256 = manifest.Items[0].OutputSha256;
        manifest.Items[1].OutputBytes = manifest.Items[0].OutputBytes;
        WriteManifest(manifest);
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);

        File.WriteAllText(ManifestPath, "{\"schemaVersion\":1,\"items\":[null]}");
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Build_prevalidates_file_collisions_and_preserves_existing_review_evidence()
    {
        MediaReviewPacket.Item first = ItemFor("a") with { InputPath = MakeFile("collision-source.png", "source") };
        MediaReviewPacket.Item second = ItemFor("a.input");
        Assert.Throws<InvalidDataException>(() => MediaReviewPacket.Build(Packet(), new[] { first, second }));
        Assert.False(Directory.Exists(Packet()));

        Directory.CreateDirectory(Packet());
        string evidencePath = MakeFile(Path.Combine("packet", "review-sheet.csv"), "owner evidence");
        Assert.Throws<IOException>(() => MediaReviewPacket.Build(Packet(), new[] { ItemFor("another") }));
        Assert.Equal("owner evidence", File.ReadAllText(evidencePath));
    }

    [Fact]
    public void Missing_or_malformed_packet_files_are_invalid()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        File.Delete(Path.Combine(Packet(), "review-sheet.csv"));
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
        File.WriteAllText(Path.Combine(Packet(), "review-sheet.csv"), "id,task,output_file,p,v,t,pass,notes\r\n");
        File.Delete(Path.Combine(Packet(), "outputs", "a.png"));
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Reviewed_packet_rejects_unclosed_csv_quotes()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        MarkReviewed();
        File.WriteAllText(Path.Combine(Packet(), "review-sheet.csv"),
            "id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
            "a,text-render,outputs/a.png,4,3,5,yes,\"unclosed\r\n");

        MediaReviewPacket.Evaluation evaluation = MediaReviewPacket.Evaluate(Packet());
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, evaluation.Outcome);
        Assert.Contains(evaluation.Problems, problem => problem.Contains("malformed CSV"));
    }

    [Theory]
    [InlineData("wrong,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
                "a,text-render,outputs/a.png,4,3,5,yes,ok\r\n")]
    [InlineData("id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
                "a,text-render,outputs/other.png,4,3,5,yes,ok\r\n")]
    [InlineData("id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
                "a,text-render,outputs/a.png,4,3,5,yes\r\n")]
    [InlineData("id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n")]
    public void Reviewed_packet_requires_exact_header_and_manifest_row_identity(string sheet)
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        MarkReviewed();
        File.WriteAllText(Path.Combine(Packet(), "review-sheet.csv"), sheet);

        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Evaluate_rejects_outputs_junction_even_when_external_content_hash_matches()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        string outputs = Path.Combine(Packet(), "outputs");
        string outside = Path.Combine(_root, "outside-content");
        Directory.Move(outputs, outside);
        try
        {
            CreateJunction(outputs, outside);

            MediaReviewPacket.Evaluation evaluation = MediaReviewPacket.Evaluate(Packet());
            Assert.Equal(MediaReviewPacket.Outcome.Invalid, evaluation.Outcome);
            Assert.Contains(evaluation.Problems, problem => problem.Contains("reparse point"));
        }
        finally
        {
            if (Directory.Exists(outputs) && (File.GetAttributes(outputs) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(outputs);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Evaluate_rejects_reparse_point_at_content_path()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        string outputLink = Path.Combine(Packet(), "outputs", "a.png");
        string outside = Path.Combine(_root, "outside-file-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "asset"), "pixels-a");
        File.Delete(outputLink);
        try
        {
            CreateJunction(outputLink, outside);
            MediaReviewPacket.Evaluation evaluation = MediaReviewPacket.Evaluate(Packet());
            Assert.Equal(MediaReviewPacket.Outcome.Invalid, evaluation.Outcome);
            Assert.Contains(evaluation.Problems, problem => problem.Contains("reparse point"));
        }
        finally
        {
            if (Directory.Exists(outputLink) && (File.GetAttributes(outputLink) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(outputLink);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static void CreateJunction(string path, string target)
    {
        using Process junction = Process.Start(new ProcessStartInfo("cmd.exe",
            "/d /c mklink /J \"" + path + "\" \"" + target + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(junction.WaitForExit(10_000), "Junction helper did not exit.");
        Assert.Equal(0, junction.ExitCode);
    }

    [Fact]
    public void Scores_without_owner_sign_off_are_invalid_not_a_pass()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        File.WriteAllText(Path.Combine(Packet(), "review-sheet.csv"),
            "id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
            "a,text-render,outputs/a.png,5,5,5,yes,self-judged\r\n");
        MediaReviewPacket.Evaluation eval = MediaReviewPacket.Evaluate(Packet());
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, eval.Outcome);
        Assert.Contains(eval.Problems, p => p.Contains("scores present"));
    }

    [Fact]
    public void Owner_signed_complete_sheet_is_complete_and_incomplete_scores_are_not()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        MarkReviewed();
        string sheetPath = Path.Combine(Packet(), "review-sheet.csv");

        File.WriteAllText(sheetPath,
            "id,task,output_file,prompt_adherence_1to5,visual_quality_1to5,task_specific_1to5,pass_yes_no,notes\r\n" +
            "a,text-render,outputs/a.png,4,3,,yes,\r\n");
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);

        File.WriteAllText(sheetPath, ValidScoredSheet("ok, good"));
        Assert.Equal(MediaReviewPacket.Outcome.Complete, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Reviewed_status_without_reviewer_is_invalid()
    {
        MediaReviewPacket.Build(Packet(), new[] { ItemFor("a") });
        string manifestPath = Path.Combine(Packet(), "manifest.json");
        File.WriteAllText(manifestPath,
            File.ReadAllText(manifestPath).Replace("\"PENDING_OWNER_REVIEW\"", "\"REVIEWED_BY_OWNER\""));
        Assert.Equal(MediaReviewPacket.Outcome.Invalid, MediaReviewPacket.Evaluate(Packet()).Outcome);
    }

    [Fact]
    public void Build_rejects_duplicate_ids_unknown_tasks_and_missing_outputs()
    {
        Assert.Throws<InvalidDataException>(() => MediaReviewPacket.Build(Packet(), new[] { ItemFor("a"), ItemFor("a") }));
        Assert.Throws<InvalidDataException>(() => MediaReviewPacket.Build(Packet(), new[] { ItemFor("a", "vibes") }));
        MediaReviewPacket.Item missing = ItemFor("m") with { OutputPath = Path.Combine(_root, "nope.png") };
        Assert.Throws<FileNotFoundException>(() => MediaReviewPacket.Build(Packet(), new[] { missing }));
    }
}
