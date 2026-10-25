using PaRuX.Server;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// 2026-08-27: Model Hub discovery categories/taxonomy. ModelCategoryClassifier is a pure function
/// over already-fetched search-result data (repo id, gguf file names, the search endpoint's own
/// tags array) - no HTTP call, no HF dependency, fully hermetic. See the classifier's own doc
/// comment for why this exists instead of relying on HF's search-time-absent pipeline_tag.
/// </summary>
public sealed class ModelCategoryClassifierTests
{
    [Fact]
    public void Classifies_a_coding_model_by_repo_id()
    {
        string category = ModelCategoryClassifier.Classify(
            "unsloth/Qwen2.5-Coder-7B-Instruct-GGUF", new[] { "qwen2.5-coder-7b-instruct-q4_k_m.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Coding, category);
    }

    [Fact]
    public void Classifies_a_vision_language_model_by_repo_id()
    {
        string category = ModelCategoryClassifier.Classify(
            "bartowski/Qwen2-VL-7B-Instruct-GGUF", new[] { "qwen2-vl-7b-instruct-q4_k_m.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Vision, category);
    }

    [Fact]
    public void Classifies_an_embedding_model_by_repo_id()
    {
        string category = ModelCategoryClassifier.Classify(
            "nomic-ai/nomic-embed-text-v1.5-GGUF", new[] { "nomic-embed-text-v1.5.f16.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Embedding, category);
    }

    [Fact]
    public void Classifies_a_reranker_model_by_repo_id()
    {
        string category = ModelCategoryClassifier.Classify(
            "gpustack/bge-reranker-v2-m3-GGUF", new[] { "bge-reranker-v2-m3-f16.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Reranker, category);
    }

    [Fact]
    public void Classifies_an_uncensored_model_by_repo_id()
    {
        string category = ModelCategoryClassifier.Classify(
            "TheDrummer/Qwen3.5-4B-Uncensored-GGUF", new[] { "qwen3.5-4b-uncensored-q4_k_m.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.UncensoredRoleplay, category);
    }

    [Fact]
    public void Classifies_a_reasoning_model_by_repo_id()
    {
        string category = ModelCategoryClassifier.Classify(
            "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B-GGUF", new[] { "deepseek-r1-distill-qwen-7b-q4_k_m.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Reasoning, category);
    }

    [Fact]
    public void Falls_back_to_general_for_an_ordinary_instruct_model()
    {
        string category = ModelCategoryClassifier.Classify(
            "meta-llama/Llama-3.1-8B-Instruct-GGUF", new[] { "llama-3.1-8b-instruct-q4_k_m.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.General, category);
    }

    [Fact]
    public void A_matching_tag_alone_is_enough_even_when_the_repo_id_and_filenames_are_generic()
    {
        string category = ModelCategoryClassifier.Classify(
            "some-org/generic-name-GGUF", new[] { "generic-name-q4_k_m.gguf" }, new[] { "text-embeddings-inference", "embed" });
        Assert.Equal(ModelCategoryClassifier.Embedding, category);
    }

    [Fact]
    public void Reranker_takes_priority_over_a_looser_embedding_match_on_the_same_repo()
    {
        string category = ModelCategoryClassifier.Classify(
            "BAAI/bge-reranker-v2-m3-GGUF", new[] { "bge-reranker-v2-m3.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Reranker, category);
    }

    [Fact]
    public void Classification_is_deterministic_and_case_insensitive()
    {
        string lower = ModelCategoryClassifier.Classify("org/MY-CODER-MODEL-GGUF", new[] { "MY-CODER-MODEL.gguf" }, null);
        string upper = ModelCategoryClassifier.Classify("org/my-coder-model-gguf", new[] { "my-coder-model.gguf" }, null);
        Assert.Equal(ModelCategoryClassifier.Coding, lower);
        Assert.Equal(lower, upper);
    }

    [Theory]
    [InlineData("mlabonne/Llama-3.1-8B-Instruct-abliterated-GGUF", "llama-3.1-8b-instruct-abliterated.q4_k_m.gguf", ModelCategoryClassifier.Abliterated)]
    [InlineData("TheDrummer/Cydonia-24B-Uncensored-GGUF", "cydonia-24b-uncensored-q4_k_m.gguf", ModelCategoryClassifier.UncensoredRoleplay)]
    [InlineData("ggerganov/whisper.cpp-GGUF", "ggml-whisper-large-v3-q5_0.gguf", ModelCategoryClassifier.Audio)]
    [InlineData("lex-au/Orpheus-3b-FT-Q8_0.gguf", "orpheus-3b-0.1-ft-q8_0.gguf", ModelCategoryClassifier.Audio)]
    [InlineData("bartowski/QwQ-32B-GGUF", "qwq-32b-q4_k_m.gguf", ModelCategoryClassifier.Reasoning)]
    [InlineData("bartowski/Llama-3.2-3B-Instruct-GGUF", "llama-3.2-3b-instruct-q4_k_m.gguf", ModelCategoryClassifier.General)]
    public void Splits_abliterated_audio_and_reasoning_families_without_catching_ordinary_chat_models(
        string repo, string file, string expected) =>
        Assert.Equal(expected, ModelCategoryClassifier.Classify(repo, new[] { file }, null));
}
