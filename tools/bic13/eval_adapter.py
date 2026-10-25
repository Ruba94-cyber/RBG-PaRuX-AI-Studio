"""Reload a PaRuX training adapter independently of the trainer and score it on held-out data.

Reload path is plain transformers + peft (NOT unsloth), so it proves the saved adapter directory is
self-contained and loadable. The base model is evaluated in the same process on the same prompts so the
result is a before/after comparison on identical hardware.

Usage: python eval_adapter.py <adapter_dir|none> <heldout.jsonl> <out.json> [method-label]
Metrics (generation, greedy, 24 new tokens): json_valid_rate, exact_match_rate, sum_correct_rate;
(likelihood) heldout_ce_loss on the reference answer given the chat prompt; for DPO also
preference_margin_rate = fraction of held-out prompts where logp(correct JSON) > logp(wrong JSON).
"""
import json
import atexit
import math
import os
import platform
import sys
import time
from safety import reserve_output, save_reserved_json

import torch
from peft import PeftConfig, PeftModel
from transformers import AutoModelForCausalLM, AutoTokenizer

adapter = sys.argv[1]
heldout_path = sys.argv[2]
out_path = sys.argv[3]
label = sys.argv[4] if len(sys.argv) > 4 else "eval"
include_sensitive_samples = "--include-sensitive-samples" in sys.argv[5:]
result_handle = reserve_output(out_path)
finished = False
save_reserved_json(result_handle, {"benchmark_complete": False, "status": "running"})


def save_incomplete_if_needed():
    if not finished and not result_handle.closed:
        save_reserved_json(result_handle, {"benchmark_complete": False, "status": "failed_or_interrupted",
                                           "error": "evaluation exited before all rows completed"})
        result_handle.close()


atexit.register(save_incomplete_if_needed)

rows = [json.loads(line) for line in open(heldout_path, encoding="utf-8") if line.strip()]


def load(adapter_dir):
    if adapter_dir == "none":
        raise SystemExit("base model name is required for the base run: pass base:<hf-id>")
    if adapter_dir.startswith("base:"):
        name = adapter_dir[5:]
        tok = AutoTokenizer.from_pretrained(name)
        model = AutoModelForCausalLM.from_pretrained(name, device_map={"": 0})
        return tok, model, name, None
    cfg = PeftConfig.from_pretrained(adapter_dir)
    base = cfg.base_model_name_or_path
    tok = AutoTokenizer.from_pretrained(base)
    model = AutoModelForCausalLM.from_pretrained(base, device_map={"": 0})
    model = PeftModel.from_pretrained(model, adapter_dir)
    return tok, model, base, adapter_dir


def chat(tok, prompt):
    return tok.apply_chat_template([{"role": "user", "content": prompt}], tokenize=False, add_generation_prompt=True)


@torch.no_grad()
def completion_logp(model, tok, prompt, completion):
    p_ids = tok(chat(tok, prompt), return_tensors="pt", add_special_tokens=False).input_ids.to(model.device)
    c_ids = tok(completion, return_tensors="pt", add_special_tokens=False).input_ids.to(model.device)
    ids = torch.cat([p_ids, c_ids], dim=1)
    logits = model(ids).logits[0, :-1].float()
    targets = ids[0, 1:]
    logp = torch.log_softmax(logits, dim=-1).gather(1, targets.unsqueeze(1)).squeeze(1)
    comp = logp[p_ids.shape[1] - 1:]
    return comp.sum().item(), comp.numel()


tok, model, base_name, adapter_dir = load(adapter)
model.eval()
started = time.time()
valid = exact = summ = 0
ce_sum = 0.0
ce_tokens = 0
margin_wins = 0
samples = []
for row in rows:
    text = chat(tok, row["prompt"])
    ids = tok(text, return_tensors="pt", add_special_tokens=False).to(model.device)
    with torch.no_grad():
        gen = model.generate(**ids, max_new_tokens=24, do_sample=False, pad_token_id=tok.eos_token_id)
    reply = tok.decode(gen[0, ids.input_ids.shape[1]:], skip_special_tokens=True).strip()
    ok = False
    try:
        parsed = json.loads(reply)
        ok = isinstance(parsed, dict)
        if ok and parsed.get("sum") == row["a"] + row["b"]:
            summ += 1
    except Exception:
        ok = False
    valid += 1 if ok else 0
    exact += 1 if reply == row["reference"] else 0
    lp, n = completion_logp(model, tok, row["prompt"], row["reference"])
    ce_sum += -lp
    ce_tokens += n
    wrong = json.dumps({"sum": row["a"] + row["b"] + 10})
    lp_wrong, _ = completion_logp(model, tok, row["prompt"], wrong)
    margin_wins += 1 if lp > lp_wrong else 0
    if include_sensitive_samples and len(samples) < 6:
        samples.append({"prompt": row["prompt"], "reply": reply, "reference": row["reference"]})

n = len(rows)
if n == 0 or ce_tokens == 0:
    raise RuntimeError("held-out evaluation contains no scoreable rows/tokens")
result = {
    "label": label,
    "model_kind": "adapter" if adapter_dir else "base-model",
    "model_identity": {"adapter": adapter_dir, "base_model": base_name} if include_sensitive_samples else "redacted",
    "heldout_rows": n,
    "json_valid_rate": valid / n,
    "exact_match_rate": exact / n,
    "sum_correct_rate": summ / n,
    "heldout_ce_loss_per_token": ce_sum / ce_tokens,
    "heldout_perplexity": math.exp(ce_sum / ce_tokens),
    "preference_margin_rate": margin_wins / n,
    "samples": samples if include_sensitive_samples else None,
    "privacy": {"local_paths_redacted": not include_sensitive_samples,
                "sample_text_opt_in": include_sensitive_samples},
    "benchmark_complete": True,
    "seconds": round(time.time() - started, 1),
    "hardware": {
        "gpu": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
        "torch": torch.__version__,
        "python": platform.python_version(),
        "os": platform.platform(),
    },
}
with result_handle:
    save_reserved_json(result_handle, result)
finished = True
print(json.dumps({k: v for k, v in result.items() if k != "samples"}))
