"""Deterministic synthetic datasets for the BIC-13 training evidence (public-safe, no third-party data).

Task: "Add A and B. Reply with JSON only." -> {"sum": N}. Train and held-out pairs are disjoint, so a
held-out score measures generalisation of the learned format/arithmetic, not memorisation.

Usage: python make_datasets.py <out_dir> [seed]
"""
import json
import os
import random
import sys
from safety import ensure_fresh_directory

out = ensure_fresh_directory(sys.argv[1])
seed = int(sys.argv[2]) if len(sys.argv) > 2 else 20261004
rng = random.Random(seed)
os.makedirs(out, exist_ok=True)

pairs = [(a, b) for a in range(10, 100) for b in range(10, 100)]
rng.shuffle(pairs)
train_pairs = pairs[:240]
heldout_pairs = pairs[240:240 + 60]
assert not set(train_pairs) & set(heldout_pairs)

TEMPLATES = [
    "Add {a} and {b}. Reply with JSON only.",
    "What is {a} plus {b}? Reply with JSON only.",
    "Compute {a} + {b}. Reply with JSON only.",
]


def prompt(a, b, i):
    return TEMPLATES[i % len(TEMPLATES)].format(a=a, b=b)


def answer(a, b):
    return json.dumps({"sum": a + b})


def write(name, rows):
    with open(os.path.join(out, name), "x", encoding="utf-8", newline="\n") as handle:
        for row in rows:
            handle.write(json.dumps(row) + "\n")


# SFT: chat messages.
write("sft-train.jsonl", [
    {"messages": [{"role": "user", "content": prompt(a, b, i)}, {"role": "assistant", "content": answer(a, b)}]}
    for i, (a, b) in enumerate(train_pairs)])

# DPO: chosen is the exact JSON, rejected is a wrong sum or a chatty non-JSON reply.
dpo = []
for i, (a, b) in enumerate(train_pairs):
    if i % 2 == 0:
        wrong = a + b + rng.choice([-10, -1, 1, 10])
        rejected = json.dumps({"sum": wrong})
    else:
        rejected = f"Sure! The answer is probably around {a + b}."
    dpo.append({"prompt": prompt(a, b, i), "chosen": answer(a, b), "rejected": rejected})
write("dpo-train.jsonl", dpo)

# CPT: raw text lines in the target format.
write("cpt-train.jsonl", [
    {"text": f"Q: {prompt(a, b, i)}\nA: {answer(a, b)}"} for i, (a, b) in enumerate(train_pairs)])

# GRPO: prompts with an exact reference.
write("grpo-train.jsonl", [
    {"prompt": prompt(a, b, i), "reference": answer(a, b)} for i, (a, b) in enumerate(train_pairs)])

# Held-out evaluation set (never seen in any training file).
write("heldout.jsonl", [
    {"prompt": prompt(a, b, i), "reference": answer(a, b), "a": a, "b": b}
    for i, (a, b) in enumerate(heldout_pairs)])
with open(os.path.join(out, "dataset-metadata.json"), "x", encoding="utf-8", newline="\n") as handle:
    json.dump({"status": "complete", "synthetic": True, "seed": seed,
               "train_pairs": len(train_pairs), "heldout_pairs": len(heldout_pairs),
               "pair_overlap": False}, handle, indent=2)
    handle.write("\n")
print("synthetic datasets written; train=", len(train_pairs), "heldout=", len(heldout_pairs), "seed=", seed)
