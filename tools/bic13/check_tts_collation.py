"""Check the shipped TTS collator against a local SpeechT5 processor on CPU.

Usage: python check_tts_collation.py trainer.py evaluator.py model_dir dataset_dir out.json
No model weights, inference, training, or network access are required.
"""
import ast
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

os.environ["CUDA_VISIBLE_DEVICES"] = ""
os.environ["HF_HUB_OFFLINE"] = "1"
import librosa
import torch
from transformers import SpeechT5Processor

trainer, evaluator, model_dir, dataset_dir, output = map(Path, sys.argv[1:6])


def load_definitions(path, names):
    nodes = [node for node in ast.walk(ast.parse(path.read_text(encoding="utf-8-sig")))
             if isinstance(node, (ast.FunctionDef, ast.ClassDef)) and node.name in names]
    if {node.name for node in nodes} != set(names) or len(nodes) != len(names):
        raise RuntimeError("Requested shipped definitions were not found exactly once")
    namespace = {"torch": torch}
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(path), "exec"), namespace)
    return namespace


production = load_definitions(trainer, ["normalize_tts_spectrogram", "TtsCollator"])
evaluation = load_definitions(evaluator, ["normalize_tts_spectrogram", "build_tts_evaluation_batch"])
processor = SpeechT5Processor.from_pretrained(str(model_dir), local_files_only=True)
row = json.loads((dataset_dir / "tts-train.jsonl").read_text().splitlines()[0])
audio, _ = librosa.load(dataset_dir / row["audio"], sr=16000, mono=True)
items = [processor(text=row["text"], audio_target=clip, sampling_rate=16000,
                   return_attention_mask=False) for clip in [audio, audio[:len(audio) // 2]]]
original_shapes = [list(torch.as_tensor(item["labels"]).shape) for item in items]
for item in items:
    original = torch.as_tensor(item["labels"])
    item["labels"] = production["normalize_tts_spectrogram"](original)
    expected = original[0] if original.ndim == 3 else original
    torch.testing.assert_close(torch.as_tensor(item["labels"]), expected)
    torch.testing.assert_close(torch.as_tensor(evaluation["normalize_tts_spectrogram"](original)), expected)
    item["speaker_embeddings"] = [0.0] * 512
    evaluation_batch = evaluation["build_tts_evaluation_batch"](processor, item, 2)
    assert "attention_mask" in evaluation_batch
    assert evaluation_batch["attention_mask"].shape == evaluation_batch["input_ids"].shape
    assert evaluation_batch["labels"].ndim == 3
    assert "decoder_attention_mask" not in evaluation_batch

collator = production["TtsCollator"](processor, SimpleNamespace(config=SimpleNamespace(reduction_factor=2)))
batch = collator(items)
assert batch["labels"].ndim == 3 and batch["labels"].shape[0] == 2
assert batch["labels"].shape[1] % 2 == 0
assert batch["labels"].shape[2] == torch.as_tensor(items[0]["labels"]).shape[1]
short_frames = len(items[1]["labels"])
assert short_frames < batch["labels"].shape[1]
assert torch.all(batch["labels"][1, short_frames:] == -100)
torch.testing.assert_close(batch["labels"][1, :short_frames], torch.as_tensor(items[1]["labels"]))
assert "decoder_attention_mask" not in batch
assert batch["speaker_embeddings"].shape == (2, 512)

rejections = 0
for shape in [(80,), (2, 10, 80), (1, 1, 10, 80), (0, 80), (10, 0)]:
    for scope in [production, evaluation]:
        try:
            scope["normalize_tts_spectrogram"](torch.zeros(shape))
        except ValueError:
            rejections += 1
        else:
            raise AssertionError("Malformed spectrogram was accepted: " + str(shape))


class MalformedMaskProcessor:
    def pad(self, **kwargs):
        result = processor.pad(**kwargs)
        result["decoder_attention_mask"] = torch.ones(2, 1)
        return result


try:
    production["TtsCollator"](MalformedMaskProcessor(), SimpleNamespace(config=SimpleNamespace(reduction_factor=2)))(items)
except ValueError:
    rejections += 1
else:
    raise AssertionError("Misaligned frame mask was accepted")

class MissingTokenMaskProcessor:
    def pad(self, **kwargs):
        result = processor.pad(**kwargs)
        result.pop("attention_mask", None)
        return result

try:
    evaluation["build_tts_evaluation_batch"](MissingTokenMaskProcessor(), items[0], 2)
except ValueError:
    rejections += 1
else:
    raise AssertionError("Missing token attention mask was accepted")

result = {"result": "PASS", "device": "cpu", "original_processor_shapes": original_shapes,
          "collated_shape": list(batch["labels"].shape), "malformed_inputs_rejected": rejections,
          "scope": "processor/collator tensor contracts and loss padding; no trained-model quality evidence"}
output.write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))
