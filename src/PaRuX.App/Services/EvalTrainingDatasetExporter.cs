using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// Deterministic client-side handoff from semantically mapped Eval cases to Training Lab's SFT
    /// chat JSONL. The semantic decision has already been made explicitly when the user mapped
    /// Dataset Hub columns into Eval; this exporter therefore does no additional guessing and does
    /// not need a second CSV/JSON/Parquet parser. DPO is intentionally not synthesized from Eval
    /// cases because Eval has no rejected-response concept; DPO continues to require a real
    /// prompt/chosen/rejected JSONL source.
    /// </summary>
    public static class EvalTrainingDatasetExporter
    {
        public static async Task<string> ExportSftAsync(
            EvalDatasetDto dataset,
            IReadOnlyList<EvalCaseDto> cases,
            CancellationToken ct)
        {
            if (cases.Count == 0)
                throw new InvalidDataException("The selected Eval dataset has no cases to export for SFT training.");

            string directory = Path.Combine(
                PaRuX.Contracts.PaRuXDataRoot.Path, "training-datasets");
            Directory.CreateDirectory(directory);

            string safeName = MakeSafeName(dataset.Name);
            string output = Path.Combine(directory, safeName + "-" + dataset.Id + ".sft.jsonl");
            string temp = output + ".tmp";

            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (EvalCaseDto c in cases.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Id, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    var messages = new List<object>();
                    if (!string.IsNullOrWhiteSpace(c.SystemPrompt))
                        messages.Add(new { role = "system", content = c.SystemPrompt.Trim() });
                    messages.Add(new { role = "user", content = c.Prompt.Trim() });
                    messages.Add(new { role = "assistant", content = c.ExpectedBehavior.Trim() });
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { messages }).AsMemory(), ct);
                }
            }

            File.Move(temp, output, overwrite: true);

            // Small provenance sidecar: adapter registration already records SourceDatasetPath,
            // while this preserves which durable Eval dataset/cases created that concrete file.
            string metadataPath = output + ".meta.json";
            string metadataTemp = metadataPath + ".tmp";
            string metadata = JsonSerializer.Serialize(new
            {
                sourceEvalDatasetId = dataset.Id,
                sourceEvalDatasetName = dataset.Name,
                caseIds = cases.Select(c => c.Id).ToArray(),
                exportedUtc = DateTime.UtcNow,
                method = TrainingMethod.Sft
            });
            await File.WriteAllTextAsync(metadataTemp, metadata, Encoding.UTF8, ct);
            File.Move(metadataTemp, metadataPath, overwrite: true);
            return output;
        }

        private static string MakeSafeName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "eval-dataset" : safe[..Math.Min(safe.Length, 80)];
        }
    }
}
