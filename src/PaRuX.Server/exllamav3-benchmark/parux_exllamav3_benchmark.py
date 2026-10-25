import argparse
import contextlib
import json
import math
import sys
import time

import torch
from exllamav3 import Generator, GreedySampler, Job, model_init

API_VERSION = 1
MAX_NEW_TOKENS = 192


def _prompt(workload: str, run: int) -> str:
    workload = (workload or "chat").strip().lower()
    stable = (
        "PaRuX local inference benchmark. Work only from the information in this prompt. "
        "Be precise, deterministic, concise, and do not call tools. "
    )
    if workload == "coding":
        body = (
            "Review a C# service that owns a local AI runtime. Explain three concrete ways to "
            "avoid deadlocks, stale process state, and unsafe model promotion. Include one short "
            "verification checklist."
        )
    elif workload in ("long-context", "research"):
        body = (
            "Summarize the tradeoffs among latency, throughput, memory pressure, model quality, "
            "thermal stability, and rollback safety when selecting a local inference engine. "
            "Prioritize evidence over raw token speed."
        )
    else:
        body = (
            "Explain in four short bullets how a local-first AI application should choose between "
            "two inference engines while preserving privacy, quality, stability, and responsiveness."
        )
    filler = " ".join([
        "The benchmark uses a stable local context containing model identity, runtime identity, "
        "hardware constraints, task quality evidence, rollback rules, and deterministic verification facts."
        for _ in range(18)
    ])
    return f"System: {stable}\nContext: {filler}\nUser: {body}\nRun: {run}\nAssistant:"


def _run_job(generator, tokenizer, config, workload: str, run: int):
    input_ids = tokenizer.encode(_prompt(workload, run), add_bos=True)
    prompt_tokens = int(input_ids.shape[-1])
    if prompt_tokens <= 0:
        raise RuntimeError("Tokenizer produced an empty benchmark prompt")

    stop = []
    eos_list = getattr(config, "eos_token_id_list", None)
    if eos_list:
        stop.extend(int(value) for value in eos_list if value is not None)
    elif getattr(tokenizer, "eos_token_id", None) is not None:
        stop.append(int(tokenizer.eos_token_id))

    job = Job(
        input_ids=input_ids,
        max_new_tokens=MAX_NEW_TOKENS,
        sampler=GreedySampler(),
        seed=0,
        stop_conditions=stop,
        token_healing=False,
    )
    generator.enqueue(job)
    while generator.num_remaining_jobs():
        for _ in generator.iterate():
            pass

    prefill_seconds = float(getattr(job, "time_prefill", 0.0) or 0.0)
    generate_seconds = float(getattr(job, "time_generate", 0.0) or 0.0)
    generated_tokens = int(getattr(job, "new_tokens", 0) or 0)
    generated_tokens = max(1, generated_tokens)

    if not math.isfinite(prefill_seconds) or prefill_seconds <= 0:
        raise RuntimeError("ExLlamaV3 reported invalid prefill timing")
    if not math.isfinite(generate_seconds) or generate_seconds <= 0:
        raise RuntimeError("ExLlamaV3 reported invalid generation timing")

    prompt_tps = prompt_tokens / prefill_seconds
    decode_tps = generated_tokens / generate_seconds

    enqueue = getattr(job, "time_enqueue", None)
    first = getattr(job, "time_first_token", None)
    if enqueue is not None and first is not None and first >= enqueue:
        ttft_ms = (float(first) - float(enqueue)) * 1000.0
    else:
        # Fallback remains grounded in ExLlamaV3's own split timing rather than host wall-clock.
        ttft_ms = (prefill_seconds + generate_seconds / generated_tokens) * 1000.0

    for name, value in (
        ("prompt_tps", prompt_tps),
        ("decode_tps", decode_tps),
        ("ttft_ms", ttft_ms),
    ):
        if not math.isfinite(value) or value <= 0:
            raise RuntimeError(f"ExLlamaV3 reported invalid {name}")

    return {
        "prompt_tokens": prompt_tokens,
        "generated_tokens": generated_tokens,
        "prompt_tps": prompt_tps,
        "decode_tps": decode_tps,
        "ttft_ms": ttft_ms,
    }


@torch.inference_mode()
def _benchmark(args):
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to the PaRuX-managed ExLlamaV3 runtime")

    torch.cuda.empty_cache()
    load_started = time.perf_counter()
    model, config, cache, tokenizer = model_init.init(args)
    torch.cuda.synchronize()
    load_ms = (time.perf_counter() - load_started) * 1000.0

    # Resetting peak stats keeps the current model allocation as the floor and then captures any
    # extra KV/temporary allocations reached by the benchmark jobs.
    torch.cuda.reset_peak_memory_stats()
    generator = Generator(model, cache, tokenizer)

    primary = _run_job(generator, tokenizer, config, args.parux_workload, 0)
    sustained_values = [primary["decode_tps"]]
    runs = 4 if args.parux_sustained else 1
    for run in range(1, runs):
        measured = _run_job(generator, tokenizer, config, args.parux_workload, run)
        sustained_values.append(measured["decode_tps"])

    torch.cuda.synchronize()
    peak_bytes = max(
        int(torch.cuda.max_memory_allocated()),
        int(torch.cuda.max_memory_reserved()),
    )
    sustained = sum(sustained_values) / len(sustained_values)

    result = {
        "api_version": API_VERSION,
        "prompt_tokens_per_second": primary["prompt_tps"],
        "decode_tokens_per_second": primary["decode_tps"],
        "time_to_first_token_milliseconds": primary["ttft_ms"],
        "sustained_decode_tokens_per_second": sustained,
        "peak_vram_mib": peak_bytes / (1024.0 * 1024.0),
        "load_milliseconds": load_ms,
        "generated_tokens": primary["generated_tokens"],
        "prompt_tokens": primary["prompt_tokens"],
    }
    return result


def main():
    parser = argparse.ArgumentParser(allow_abbrev=False)
    model_init.add_args(
        parser,
        default_cache_size=4096,
        default_autosplit_max_batch_size=1,
    )
    parser.add_argument("--parux-workload", default="chat")
    parser.add_argument("--parux-sustained", action="store_true")
    args = parser.parse_args()

    # ExLlamaV3's loader prints useful human diagnostics. Keep them on stderr so stdout remains a
    # single machine-readable PaRuX protocol record.
    with contextlib.redirect_stdout(sys.stderr):
        result = _benchmark(args)
    print(json.dumps(result, separators=(",", ":"), sort_keys=True), flush=True)


if __name__ == "__main__":
    main()
