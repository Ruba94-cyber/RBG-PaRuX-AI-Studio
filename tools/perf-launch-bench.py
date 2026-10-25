#!/usr/bin/env python3
"""Launch-flag measurement harness for llama-server (any build: PaRuX, Unsloth, LM Studio, LU).

Starts the REAL server with a candidate flag set on a read-only model path, measures cold TTFT,
2k-token prefill speed, warm decode speed (prose + code prompts), speculative acceptance (from the
server's own `timings`), and prefix-cache reuse TTFT, then ALWAYS kills the process it started.
Appends one JSON record per run to --out. Nothing is downloaded; the model file is only read.

Usage (run with `python -I`):
  tools/perf-launch-bench.py --label x --server <llama-server.exe> --model <gguf> \
      --flags "-c 8192 -ngl 99 -fa on -np 1" --out results.jsonl [--path-prepend DIR ...]
Quiet gate: refuses to start while testhost/dotnet-test/another llama-server runs (use --wait N to
poll for N seconds) so a measurement is never taken on a busy machine.
"""
import argparse, hashlib, json, os, shlex, socket, statistics, subprocess, sys, time, urllib.request

FORBIDDEN = ("testhost", "llama-server", "lu-llama-server")


def busy_reason():
    import psutil
    for p in psutil.process_iter(["name", "cmdline"]):
        n = (p.info["name"] or "").lower()
        if any(f in n for f in FORBIDDEN):
            return n
        if n == "dotnet.exe" and " test" in " ".join(p.info["cmdline"] or []).lower():
            return "dotnet test"
    cpu = psutil.cpu_percent(interval=5)
    return f"cpu {cpu:.0f}%" if cpu > 15 else None


def free_port():
    s = socket.socket(); s.bind(("127.0.0.1", 0)); p = s.getsockname()[1]; s.close(); return p


def post_stream(port, body, timeout=300):
    """Returns (ttft_s, total_s, text, token_chunks, final_timings)."""
    req = urllib.request.Request(f"http://127.0.0.1:{port}/completion", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    t0 = time.perf_counter(); ttft = None; text = []; n = 0; timings = {}
    with urllib.request.urlopen(req, timeout=timeout) as r:
        for raw in r:
            line = raw.decode("utf-8", "replace").strip()
            if not line.startswith("data:"): continue
            payload = line[5:].strip()
            if not payload or payload == "[DONE]": continue
            j = json.loads(payload)
            if j.get("content"):
                if ttft is None: ttft = time.perf_counter() - t0
                text.append(j["content"]); n += 1
            if "timings" in j: timings = j["timings"]
    return ttft, time.perf_counter() - t0, "".join(text), n, timings


def filler(tokens_target, seed):
    words = ("alpha river stone window garden signal paper engine harbor lantern copper meadow "
             "bridge violet anchor thunder marble orchard compass ribbon").split()
    out, i = [], seed
    while len(out) < int(tokens_target * 0.75):
        out.append(words[(i * 7 + i // 5) % len(words)]); i += 1
    return " ".join(out)


CODE = '''import dataclasses, json
from typing import Iterable

@dataclasses.dataclass
class Item:
    name: str
    price: float
    qty: int = 1

    def total(self):
        return self.price * self.qty

class Cart:
    def __init__(self):
        self.items = []

    def add(self, item):
        for existing in self.items:
            if existing.name == item.name:
                existing.qty += item.qty
                return
        self.items.append(item)

    def remove(self, name):
        self.items = [i for i in self.items if i.name != name]

    def total(self):
        return sum(i.total() for i in self.items)

    def to_json(self):
        return json.dumps([dataclasses.asdict(i) for i in self.items], indent=2)

    @classmethod
    def from_json(cls, text):
        cart = cls()
        for d in json.loads(text):
            cart.add(Item(**d))
        return cart
'''
PROMPTS = {
    "code": "```python\n" + CODE + "```\nRewrite the module above exactly, adding full type hints and docstrings to every method:\n```python\n",
    "prose": "Write a long, flowing essay about how the invention of the printing press changed European society, covering economics, religion, science and everyday life.\n\n",
}


def run(a):
    port = free_port()
    env = os.environ.copy()
    env["PATH"] = os.pathsep.join(a.path_prepend + [env["PATH"]])
    cmd = [a.server, "-m", a.model, "--host", "127.0.0.1", "--port", str(port)] + shlex.split(a.flags, posix=False)
    rec = {"label": a.label, "server": a.server, "model": os.path.basename(a.model), "flags": a.flags,
           "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    log = open(a.out + f".{a.label}.log", "wb")
    t_start = time.perf_counter()
    proc = subprocess.Popen(cmd, stdout=log, stderr=subprocess.STDOUT, env=env)
    try:
        while True:
            if proc.poll() is not None: raise RuntimeError(f"server exited {proc.returncode}")
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=2) as r:
                    if r.status == 200: break
            except Exception: pass
            if time.perf_counter() - t_start > a.load_timeout: raise RuntimeError("load timeout")
            time.sleep(0.5)
        rec["load_s"] = round(time.perf_counter() - t_start, 2)
        base = {"temperature": 0, "stream": True, "cache_prompt": False}
        # cold: first request after load
        ttft, tot, txt, n, tm = post_stream(port, {**base, "prompt": "Hello, my name is", "n_predict": 64, "ignore_eos": True})
        rec["cold_ttft_s"] = round(ttft, 3); rec["cold_decode_tps"] = round(tm.get("predicted_per_second", 0), 1)
        # prefill: ~2k-token prompt, 1 token out
        pf = []
        for k in range(a.runs):
            ttft, _, _, _, tm = post_stream(port, {**base, "prompt": filler(2000, 100 + k), "n_predict": 1})
            pf.append({"ttft_s": ttft, "prompt_n": tm.get("prompt_n"), "tps": tm.get("prompt_per_second")})
        rec["prefill_tps"] = [round(x["tps"], 1) for x in pf]; rec["prefill_ttft_s"] = [round(x["ttft_s"], 3) for x in pf]
        rec["prefill_prompt_n"] = pf[0]["prompt_n"]
        # decode + speculative per prompt kind; first run discarded as warm-up of the speculative state
        for kind, prompt in PROMPTS.items():
            runs = []
            for k in range(a.runs + 1):
                ttft, _, txt, n, tm = post_stream(port, {**base, "prompt": prompt, "n_predict": a.tokens, "ignore_eos": True})
                runs.append({"ttft_s": ttft, "tps": tm.get("predicted_per_second"), "n": tm.get("predicted_n"),
                             "draft_n": tm.get("draft_n", 0), "draft_ok": tm.get("draft_n_accepted", 0),
                             "sha": hashlib.sha1(txt.encode()).hexdigest()[:12], "text": txt})
            warm = runs[1:]
            rec.setdefault("_texts", {})[kind] = warm[-1]["text"]
            rec[kind] = {"decode_tps": [round(r["tps"], 1) for r in warm], "ttft_s": [round(r["ttft_s"], 3) for r in warm],
                         "median_tps": round(statistics.median(r["tps"] for r in warm), 1),
                         "draft_n": warm[-1]["draft_n"], "draft_accepted": warm[-1]["draft_ok"],
                         "accept_pct": round(100 * warm[-1]["draft_ok"] / warm[-1]["draft_n"], 1) if warm[-1]["draft_n"] else None,
                         "output_sha": warm[-1]["sha"], "generated": warm[-1]["n"]}
        # prefix reuse: long shared prefix, different tail, cache_prompt on
        shared = filler(1800, 7); cache = {**base, "cache_prompt": True}
        post_stream(port, {**cache, "prompt": shared + " first tail question one", "n_predict": 1})
        t2 = [post_stream(port, {**cache, "prompt": shared + f" different tail number {i}", "n_predict": 1})[0] for i in range(a.runs)]
        rec["prefix_reuse_ttft_s"] = [round(x, 3) for x in t2]
        rec["status"] = "ok"
    except Exception as ex:
        rec["status"] = "failed"; rec["error"] = str(ex)
    finally:
        # whole tree: some launchers (e.g. LU's lu-llama-server) spawn the real server as a child
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
        proc.kill(); proc.wait()
        log.close()
    texts = rec.pop("_texts", None)
    if texts:  # kept beside the results so a changed output hash can be diffed token-for-token
        with open(a.out + ".texts.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps({"label": a.label, **texts}) + "\n")
    with open(a.out, "a", encoding="utf-8") as f: f.write(json.dumps(rec) + "\n")
    print(json.dumps(rec, indent=1))
    return 0 if rec["status"] == "ok" else 1


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", required=True); ap.add_argument("--server", required=True)
    ap.add_argument("--model", required=True); ap.add_argument("--flags", default="")
    ap.add_argument("--out", required=True); ap.add_argument("--path-prepend", nargs="*", default=[])
    ap.add_argument("--runs", type=int, default=3); ap.add_argument("--tokens", type=int, default=256)
    ap.add_argument("--load-timeout", type=int, default=240); ap.add_argument("--wait", type=int, default=0)
    ap.add_argument("--no-quiet-gate", action="store_true")
    a = ap.parse_args()
    if not a.no_quiet_gate:
        deadline = time.time() + a.wait
        while True:
            why = busy_reason()
            if not why: break
            if time.time() > deadline: sys.exit(f"machine not quiet ({why}); refusing to measure")
            time.sleep(15)
    sys.exit(run(a))
