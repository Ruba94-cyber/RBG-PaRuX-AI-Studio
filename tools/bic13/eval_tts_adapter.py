"""Reload a PaRuX TTS SFT (SpeechT5) adapter with plain transformers + peft and score held-out spectrogram loss.

Usage: python eval_tts_adapter.py <adapter_dir|base:<hf-id>> <tts-heldout.jsonl> <dataset_dir> <out.json>
Loss = the model's configured training criterion: pre/postnet spectrogram L1, stop-token BCE, and guided-attention
loss when enabled by the model configuration. Held-out utterances use the shipped trainer's zero speaker embedding.
Lower is better on this synthetic training-loss proxy; this is NOT a naturalness judgement.
"""
import json, math, os, platform, sys, time
import librosa, torch
from peft import PeftConfig, PeftModel
from transformers import SpeechT5ForTextToSpeech, SpeechT5Processor

def normalize_tts_spectrogram(labels):
    labels = torch.as_tensor(labels)
    if labels.ndim == 3 and labels.shape[0] == 1:
        labels = labels[0]
    if labels.ndim != 2 or labels.shape[0] == 0 or labels.shape[1] == 0:
        raise ValueError("TTS example must contain one nonempty rank-2 spectrogram")
    return labels.tolist()

def build_tts_evaluation_batch(processor, item, reduction_factor):
    batch = processor.pad(input_ids=[{"input_ids": item["input_ids"]}],
                          labels=[{"input_values": normalize_tts_spectrogram(item["labels"])}], return_tensors="pt")
    labels = batch["labels"]
    if labels.ndim != 3:
        raise ValueError("TTS batch spectrograms must have shape [batch, frames, mel bins]")
    token_mask = batch.get("attention_mask")
    if token_mask is None or tuple(token_mask.shape) != tuple(batch["input_ids"].shape):
        raise ValueError("TTS evaluation requires a token-aligned attention mask")
    if "decoder_attention_mask" in batch:
        mask = batch.pop("decoder_attention_mask")
        if tuple(mask.shape) != tuple(labels.shape[:2]):
            raise ValueError("TTS decoder attention mask must align with spectrogram frames")
        labels = labels.masked_fill(mask.unsqueeze(-1).ne(1), -100)
    if reduction_factor > 1:
        labels = labels[:, : labels.shape[1] - labels.shape[1] % reduction_factor]
    batch["labels"] = labels
    batch["speaker_embeddings"] = torch.zeros(labels.shape[0], 512)
    return batch

target, heldout_path, dataset_dir, out_path = sys.argv[1:5]
rows = [json.loads(l) for l in open(heldout_path, encoding="utf-8") if l.strip()]
if target.startswith("base:"):
    base_id, adapter_dir = target[5:], None
else:
    adapter_dir, base_id = target, PeftConfig.from_pretrained(target).base_model_name_or_path
processor = SpeechT5Processor.from_pretrained(base_id)
model = SpeechT5ForTextToSpeech.from_pretrained(base_id)
if adapter_dir:
    model = PeftModel.from_pretrained(model, adapter_dir)
device = "cuda" if torch.cuda.is_available() else "cpu"
model.to(device).eval()
started = time.time()
losses = []
for index, row in enumerate(rows):
    audio, _ = librosa.load(os.path.join(dataset_dir, row["audio"]), sr=16000, mono=True)
    item = processor(text=row["text"], audio_target=audio, sampling_rate=16000, return_attention_mask=False)
    batch = build_tts_evaluation_batch(processor, item, getattr(model.config, "reduction_factor", 1))
    # SpeechT5's prenet retains dropout during evaluation. Pair base/adapter rows
    # with a predeclared seed so their proxy losses can be reproduced.
    torch.manual_seed(index)
    with torch.no_grad():
        out = model(**{name: value.to(device) for name, value in batch.items()})
    loss = float(out.loss)
    if not math.isfinite(loss):
        raise RuntimeError("TTS held-out loss is not finite")
    losses.append(loss)
result = {"adapter": adapter_dir, "base_model": base_id, "heldout_rows": len(rows),
          "evaluation_seed_per_row": "zero-based row index",
          "heldout_mean_loss": sum(losses) / len(losses), "per_row_loss": losses, "seconds": round(time.time() - started, 1),
          "hardware": {"device": device, "gpu": torch.cuda.get_device_name(0) if device == "cuda" else None,
                       "torch": torch.__version__, "python": platform.python_version(),
                       "os": platform.platform()}}
json.dump(result, open(out_path, "w", encoding="utf-8"), indent=2)
print(json.dumps({k: v for k, v in result.items() if k != "per_row_loss"}))
