"""Reload a PaRuX Vision SFT adapter (plain transformers + peft, not unsloth) and score it on held-out images.

Usage: python eval_vision_adapter.py <adapter_dir|base:<hf-id>> <vision-heldout.jsonl> <dataset_dir> <out.json> [base_id_for_adapter_override]
Metric: exact_match_rate of the generated '<colour> <shape>' answer (greedy, 8 new tokens).
"""
import atexit, json, os, platform, sys, time
from safety import reserve_output, save_reserved_json
import torch
from PIL import Image
from peft import PeftConfig, PeftModel
from transformers import AutoModelForImageTextToText, AutoProcessor

target, heldout_path, dataset_dir, out_path = sys.argv[1:5]
extra_args = sys.argv[5:]
include_sensitive_samples = "--include-sensitive-samples" in extra_args
positionals = [arg for arg in extra_args if arg != "--include-sensitive-samples"]
unknown_options = [arg for arg in positionals if arg.startswith("--")]
if unknown_options or len(positionals) > 1:
    raise SystemExit("Expected at most one base_id_for_adapter_override and optional --include-sensitive-samples")
base_id_override = positionals[0] if positionals else None
if target.startswith("base:") and base_id_override:
    raise SystemExit("base_id_for_adapter_override applies only when evaluating an adapter")
result_handle = reserve_output(out_path)
finished = False
save_reserved_json(result_handle, {"benchmark_complete": False, "status": "running"})


def save_incomplete_if_needed():
    if not finished and not result_handle.closed:
        save_reserved_json(result_handle, {"benchmark_complete": False, "status": "failed_or_interrupted",
                                           "error": "vision evaluation exited before all rows completed"})
        result_handle.close()


atexit.register(save_incomplete_if_needed)
rows = [json.loads(l) for l in open(heldout_path, encoding="utf-8") if l.strip()]
if target.startswith("base:"):
    base_id, adapter_dir = target[5:], None
else:
    adapter_dir = target
    base_id = base_id_override or PeftConfig.from_pretrained(adapter_dir).base_model_name_or_path
processor = AutoProcessor.from_pretrained(base_id)
model = AutoModelForImageTextToText.from_pretrained(base_id, device_map={"": 0})
if adapter_dir:
    model = PeftModel.from_pretrained(model, adapter_dir)
model.eval()
started = time.time()
exact = contains_color = contains_shape = 0
samples = []
for row in rows:
    image = Image.open(os.path.join(dataset_dir, row["image"])).convert("RGB")
    messages = [{"role": "user", "content": [{"type": "image"}, {"type": "text", "text": row["prompt"]}]}]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    inputs = processor(text=[text], images=[image], return_tensors="pt").to(model.device)
    with torch.no_grad():
        out = model.generate(**inputs, max_new_tokens=8, do_sample=False)
    reply = processor.batch_decode(out[:, inputs["input_ids"].shape[1]:], skip_special_tokens=True)[0].strip().lower()
    ref = row["response"].lower()
    color, shape = ref.split()
    exact += reply == ref
    contains_color += color in reply
    contains_shape += shape in reply
    if include_sensitive_samples and len(samples) < 8:
        samples.append({"image": row["image"], "reply": reply, "reference": ref})
n = len(rows)
if n == 0:
    raise RuntimeError("vision held-out evaluation has no rows")
result = {"model_kind": "adapter" if adapter_dir else "base-model",
          "model_identity": {"adapter": adapter_dir, "base_model": base_id} if include_sensitive_samples else "redacted",
          "heldout_rows": n, "exact_match_rate": exact / n,
          "colour_correct_rate": contains_color / n, "shape_correct_rate": contains_shape / n,
          "samples": samples if include_sensitive_samples else None,
          "privacy": {"local_paths_redacted": not include_sensitive_samples,
                      "sample_text_opt_in": include_sensitive_samples},
          "benchmark_complete": True,
          "seconds": round(time.time() - started, 1),
          "hardware": {"gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None, "torch": torch.__version__, "python": platform.python_version(),
                       "os": platform.platform()}}
save_reserved_json(result_handle, result)
result_handle.close()
finished = True
print(json.dumps({k: v for k, v in result.items() if k not in ("samples",)}))
