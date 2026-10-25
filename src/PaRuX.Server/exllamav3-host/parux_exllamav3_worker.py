import argparse
import contextlib
import json
import math
import sys
import traceback

import torch
from exllamav3 import Generator, GreedySampler, Job, TopPSampler, model_init

API_VERSION = 2
MAX_MESSAGES = 256
MAX_MESSAGE_CHARS = 1_000_000
MAX_OUTPUT_TOKENS = 32768
THINK_OPEN = "<think>"
THINK_CLOSE = "</think>"


def _emit(payload):
    print(json.dumps(payload, separators=(",", ":"), ensure_ascii=False), flush=True)


def _validate_request_id(command):
    request_id = str(command.get("id", "")).strip()
    if not request_id or len(request_id) > 128:
        raise ValueError("request id is required and limited to 128 characters")
    return request_id


def _validate_messages(messages):
    if not isinstance(messages, list) or not messages or len(messages) > MAX_MESSAGES:
        raise ValueError(f"messages must contain 1..{MAX_MESSAGES} items")
    normalized = []
    total = 0
    allowed_roles = {"system", "user", "assistant", "tool"}
    for item in messages:
        if not isinstance(item, dict):
            raise ValueError("each message must be an object")
        role = str(item.get("role", "")).strip().lower()
        content = item.get("content")
        if role not in allowed_roles:
            raise ValueError("unsupported message role: " + role)
        if content is None:
            content = ""
        if not isinstance(content, str):
            raise ValueError("message content must be text")
        total += len(content)
        if total > MAX_MESSAGE_CHARS:
            raise ValueError("message payload exceeds the PaRuX worker safety bound")
        normalized.append({"role": role, "content": content})
    return normalized


def _render_prompt(tokenizer, command):
    request_id = _validate_request_id(command)
    messages = _validate_messages(command.get("messages"))
    enable_thinking = bool(command.get("enable_thinking", True))
    input_ids = tokenizer.hf_chat_template(
        messages=messages,
        add_generation_prompt=True,
        enable_thinking=enable_thinking,
    )
    prompt_tokens = int(input_ids.shape[-1])
    if prompt_tokens <= 0:
        raise RuntimeError("chat template produced an empty prompt")
    return request_id, input_ids, prompt_tokens, enable_thinking


def _sampler(temperature):
    try:
        temperature = float(temperature)
    except (TypeError, ValueError):
        temperature = 0.0
    if not math.isfinite(temperature) or temperature < 0.0 or temperature > 2.0:
        raise ValueError("temperature must be finite and in 0..2")
    if temperature <= 1e-6:
        return GreedySampler()
    # ExLlamaV3 upstream examples use TopPSampler(top_p, temperature).
    return TopPSampler(0.9, temperature)


class _ThinkingSplitter:
    """Separates common <think>...</think> output without leaking tags into visible content.

    Fragments from generator.iterate() may split either marker at arbitrary character boundaries, so
    retain only the small suffix that can still become a marker. Models/templates that do not emit
    thinking tags pass through unchanged apart from at most six buffered characters.
    """

    def __init__(self, enabled, request_id):
        self.enabled = bool(enabled)
        self.request_id = request_id
        self.mode = "content"
        self.buffer = ""

    def _emit_text(self, kind, text):
        if text:
            _emit({"apiVersion": API_VERSION, "id": self.request_id, "type": kind, "content": text})

    def feed(self, text):
        if not text:
            return
        if not self.enabled:
            self._emit_text("delta", text)
            return

        self.buffer += text
        while self.buffer:
            marker = THINK_OPEN if self.mode == "content" else THINK_CLOSE
            index = self.buffer.find(marker)
            if index >= 0:
                prefix = self.buffer[:index]
                self._emit_text("delta" if self.mode == "content" else "reasoning", prefix)
                self.buffer = self.buffer[index + len(marker):]
                self.mode = "reasoning" if self.mode == "content" else "content"
                continue

            # Keep only the suffix that could be the beginning of a split marker.
            keep = min(len(marker) - 1, len(self.buffer))
            emit_count = len(self.buffer) - keep
            if emit_count <= 0:
                break
            prefix = self.buffer[:emit_count]
            self.buffer = self.buffer[emit_count:]
            self._emit_text("delta" if self.mode == "content" else "reasoning", prefix)

    def flush(self):
        if self.buffer:
            self._emit_text("delta" if self.mode == "content" else "reasoning", self.buffer)
            self.buffer = ""


def _count_prompt(tokenizer, command):
    request_id, _, prompt_tokens, _ = _render_prompt(tokenizer, command)
    _emit({
        "apiVersion": API_VERSION,
        "id": request_id,
        "type": "prompt_count",
        "promptTokens": prompt_tokens,
    })


def _generate(generator, tokenizer, config, command):
    request_id, input_ids, prompt_tokens, enable_thinking = _render_prompt(tokenizer, command)
    max_new_tokens = int(command.get("max_new_tokens", 4096))
    if max_new_tokens < 1 or max_new_tokens > MAX_OUTPUT_TOKENS:
        raise ValueError(f"max_new_tokens must be in 1..{MAX_OUTPUT_TOKENS}")

    job = Job(
        input_ids=input_ids,
        max_new_tokens=max_new_tokens,
        stop_conditions=config.eos_token_id_list,
        sampler=_sampler(command.get("temperature", 0.7)),
        token_healing=False,
    )
    generator.enqueue(job)
    final_result = None
    splitter = _ThinkingSplitter(enable_thinking, request_id)
    while generator.num_remaining_jobs():
        for result in generator.iterate():
            final_result = result
            splitter.feed(result.get("text", ""))
            if result.get("eos"):
                break
    splitter.flush()

    generated_tokens = int(getattr(job, "new_tokens", 0) or 0)
    if final_result is not None:
        generated_tokens = int(final_result.get("new_tokens", generated_tokens) or generated_tokens)
    _emit({
        "apiVersion": API_VERSION,
        "id": request_id,
        "type": "done",
        "promptTokens": prompt_tokens,
        "generatedTokens": generated_tokens,
        "timePrefill": float(getattr(job, "time_prefill", 0.0) or 0.0),
        "timeGenerate": float(getattr(job, "time_generate", 0.0) or 0.0),
        "timeFirstToken": float(getattr(job, "time_first_token", 0.0) or 0.0),
        "timeLastToken": float(getattr(job, "time_last_token", 0.0) or 0.0),
    })


def main():
    parser = argparse.ArgumentParser(allow_abbrev=False)
    model_init.add_args(
        parser,
        default_cache_size=8192,
        default_autosplit_max_batch_size=1,
    )
    args = parser.parse_args()

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to the PaRuX-managed ExLlamaV3 runtime")

    # Keep ExLlama/model-loader diagnostics away from the JSONL protocol on stdout.
    with contextlib.redirect_stdout(sys.stderr):
        model, config, cache, tokenizer = model_init.init(args)
        generator = Generator(model, cache, tokenizer)

    _emit({"apiVersion": API_VERSION, "type": "ready"})

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        request_id = ""
        try:
            command = json.loads(raw)
            if not isinstance(command, dict):
                raise ValueError("worker command must be a JSON object")
            request_id = str(command.get("id", "")).strip()
            operation = str(command.get("op", "")).strip().lower()
            if operation == "generate":
                _generate(generator, tokenizer, config, command)
            elif operation == "count_prompt":
                _count_prompt(tokenizer, command)
            elif operation == "ping":
                _emit({"apiVersion": API_VERSION, "id": request_id, "type": "pong"})
            elif operation == "shutdown":
                _emit({"apiVersion": API_VERSION, "id": request_id, "type": "stopped"})
                return
            else:
                raise ValueError("unsupported worker operation: " + operation)
        except Exception as exc:
            # Fail closed: generation/protocol errors may leave generator/KV state uncertain, so
            # report the error and terminate. The C# engine reloads a clean worker next request.
            traceback.print_exc(file=sys.stderr)
            _emit({
                "apiVersion": API_VERSION,
                "id": request_id,
                "type": "error",
                "error": str(exc)[:2000],
            })
            return


if __name__ == "__main__":
    main()
