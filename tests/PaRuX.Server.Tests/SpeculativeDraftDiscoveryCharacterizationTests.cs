using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SpeculativeDraftDiscoveryCharacterizationTests : IDisposable
{
    private readonly string _root;
    private readonly string _targetDirectory;
    private readonly string _modelsPath;
    private readonly string _modelsDirectory;
    private readonly IDisposable _rootScope;
    private readonly IDisposable _modelsDirectoryScope;

    public SpeculativeDraftDiscoveryCharacterizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "parux-speculative-" + Guid.NewGuid().ToString("N"));
        _targetDirectory = Path.Combine(_root, "targets");
        _modelsPath = Path.Combine(_root, "models");
        _modelsDirectory = Path.Combine(_root, "managed-models");

        Directory.CreateDirectory(_targetDirectory);
        Directory.CreateDirectory(_modelsPath);
        Directory.CreateDirectory(_modelsDirectory);

        _rootScope = LocalAiPaths.UseRootForTests(_root);
        _modelsDirectoryScope = ModelDownloadService.UseModelsDirectoryForTests(_modelsDirectory);
    }

    public void Dispose()
    {
        _modelsDirectoryScope.Dispose();
        _rootScope.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string CreateTarget(string name)
    {
        string path = Path.Combine(_targetDirectory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string CreateCandidate(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void NullOrMissingTargetReturnsEmpty()
    {
        Assert.Empty(SpeculativeDraftDiscovery.Discover(null!));
        Assert.Empty(SpeculativeDraftDiscovery.Discover(string.Empty));
        Assert.Empty(SpeculativeDraftDiscovery.Discover(Path.Combine(_targetDirectory, "missing-target.gguf")));
    }

    [Fact]
    public void ShortTargetStemReturnsEmpty()
    {
        string target = CreateTarget("abc.gguf");
        Assert.Empty(SpeculativeDraftDiscovery.Discover(target));
    }

    [Fact]
    public void UnrelatedSidecarIsIgnored()
    {
        string target = CreateTarget("llama-3.1-8b-instruct-q4_k_m.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-draft.gguf");
        CreateCandidate(_targetDirectory, "mistral-7b-draft.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Single(found);
        Assert.Equal("llama-3.1-8b-instruct-draft.gguf", Path.GetFileName(found[0].Path));
    }

    [Fact]
    public void TargetFileIsNotReturnedAsCandidate()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Empty(found);
    }

    [Fact]
    public void CandidateInTargetDirectoryIsDiscovered()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-draft.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Single(found);
        Assert.Equal("draft-simple", found[0].Type);
        Assert.Equal(6, found[0].DraftMax);
    }

    [Fact]
    public void CandidateInLocalAiModelsPathIsDiscovered()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");
        CreateCandidate(_modelsPath, "llama-3.1-8b-instruct-eagle3.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Single(found);
        Assert.Equal("draft-eagle3", found[0].Type);
        Assert.Equal(8, found[0].DraftMax);
    }

    [Fact]
    public void CandidateInManagedModelsDirectoryIsDiscovered()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");
        CreateCandidate(_modelsDirectory, "llama-3.1-8b-instruct-dflash.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Single(found);
        Assert.Equal("draft-dflash", found[0].Type);
        Assert.Equal(15, found[0].DraftMax);
    }

    [Fact]
    public void CandidatesAreRankedByTypePriority()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-dflash.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-dspark.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-eagle3.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-draft.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Equal(3, found.Count);
        Assert.Equal("draft-eagle3", found[0].Type);
        Assert.Equal("draft-dspark", found[1].Type);
        Assert.Equal("draft-dflash", found[2].Type);
    }

    [Fact]
    public void QuantSuffixIsIgnoredWhenMatchingFamily()
    {
        string target = CreateTarget("llama-3.1-8b-instruct-q4_k_m.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-draft.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Single(found);
        Assert.Equal("llama-3.1-8b-instruct-draft.gguf", Path.GetFileName(found[0].Path));
    }

    [Fact]
    public void NonGgufFilesAreIgnored()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");
        Directory.CreateDirectory(_targetDirectory);
        File.WriteAllText(Path.Combine(_targetDirectory, "llama-3.1-8b-instruct-draft.bin"), string.Empty);

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Empty(found);
    }

    [Fact]
    public void MtpSidecarIsRecognized()
    {
        string target = CreateTarget("llama-3.1-8b-instruct.gguf");
        CreateCandidate(_targetDirectory, "llama-3.1-8b-instruct-mtp.gguf");

        IReadOnlyList<SpeculativeDraftDiscovery.Candidate> found = SpeculativeDraftDiscovery.Discover(target);

        Assert.Single(found);
        Assert.Equal("draft-mtp", found[0].Type);
        Assert.Equal(8, found[0].DraftMax);
    }
}
