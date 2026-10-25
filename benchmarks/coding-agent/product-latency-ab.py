#!/usr/bin/env python3
"""PaRuX in-product coding latency A/B (BIC-09 S5). Local only; never touches an installed PaRuX.

Runs the REAL product loop: a published PaRuX desktop app (which owns the coding worker) launched on
an isolated data root, its Core server, llama.cpp and a real local model. Tasks from
parux-eval-v1.json are created through Core's desktop coding boundary (the same one the chat /code
handoff uses), polled to a terminal state, and judged twice: by Core (status completed + verification
pass) and independently on the host by applying the task's diff to a clean clone of the fixture and
running the corpus verification command(s).

Condition A/B differ ONLY by the PARUX_CODING_BATCH_READ / PARUX_CODING_PARENT_CONTINUATION process
flags, so binary, model, runtime, prompts-minus-the-feature and tasks are identical. Conditions are
interleaved per repetition and the app is relaunched for each run so no warm state favours one side.

Phase timing comes from the "[phase-timing]" log lines the agent writes (CodingAgentPhaseTimer).
Nothing here claims a speed-up: summarise() reports the numbers and marks the comparison
INCONCLUSIVE unless the evidence rules in decide() are met.
"""
import argparse, json, os, re, shutil, statistics, subprocess, sys, time, urllib.request, urllib.error
from pathlib import Path

HANDOFF_SUFFIX = ("\n\nCHAT HANDOFF CONTEXT (supporting evidence; the objective and repository state are authoritative):\n\n"
                  "Use the existing repository conventions, preserve unrelated user changes, run the discovered verification after the final "
                  "code mutation, and report concrete blockers rather than fabricating success.")
TERMINAL = {"completed", "failed", "cancelled"}
PHASES = ["firsttoken", "generate", "context", "read", "search", "git", "write", "command", "coordination", "checkpoint"]


def run(cmd, cwd=None, env=None, timeout=300):
    return subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, timeout=timeout, encoding="utf-8", errors="replace")


class Product:
    """One launched PaRuX desktop app on an isolated data root."""

    def __init__(self, app_exe, env_root, flags):
        self.app_exe, self.env_root, self.flags = Path(app_exe), Path(env_root), flags
        self.proc = None
        self.base = None
        self.token = None

    def start(self, timeout=180):
        data = self.env_root / "data"
        boot = data / "server" / "bootstrap.json"
        if boot.exists():
            boot.unlink()
        env = dict(os.environ)
        env.update({"PARUX_DATA_ROOT": str(data), "PARUX_LOCALAI_STATE_DIR": str(self.env_root / "state"), "PARUX_UI_TESTS": "0", "PARUX_BENCHMARK_MODE": "1"})
        env.update(self.flags)
        self.proc = subprocess.Popen([str(self.app_exe)], cwd=str(self.app_exe.parent), env=env)
        t0 = time.time()
        while time.time() - t0 < timeout:
            if boot.exists():
                try:
                    info = json.loads(boot.read_text())
                    self.base, self.token = info["endpoint"], info["token"]
                    self.call("GET", "/api/v1/orchestrator/tasks")
                    return
                except Exception:
                    pass
            time.sleep(1)
        raise RuntimeError("PaRuX did not become ready")

    def call(self, method, path, body=None, timeout=120):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(self.base + path, data=data, method=method,
                                     headers={"Authorization": "Bearer " + self.token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                txt = r.read().decode()
                return json.loads(txt) if txt else None
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"{method} {path} -> {e.code} {e.read().decode()[:300]}")

    def stop(self):
        """Kill only the process tree this object launched."""
        if self.proc is not None:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(self.proc.pid)], capture_output=True)
            try:
                self.proc.wait(timeout=30)
            except Exception:
                pass
            self.proc = None
        time.sleep(3)


def parse_phase_total(messages):
    """Last [phase-timing-total] line -> dict of phase -> ms."""
    total = None
    for m in messages:
        if m.startswith("[phase-timing-total]"):
            total = m
    if total is None:
        return None
    out = {}
    for token in total.split()[1:]:
        if "=" in token:
            k, v = token.split("=", 1)
            try:
                out[k] = float(v.rstrip("ms"))
            except ValueError:
                pass
    return out


def host_check(fx_repo, worktree, task_cfg, workdir):
    """Independent correctness: apply the task diff to a clean clone and run the corpus verifier."""
    if not worktree or not Path(worktree).exists():
        return {"performed": False, "reason": "worktree unavailable", "passed": None}
    clone = Path(workdir) / ("hc-" + Path(worktree).name)
    if clone.exists():
        shutil.rmtree(clone, ignore_errors=True)
    run(["git", "clone", "-q", "--no-hardlinks", str(fx_repo), str(clone)])
    patch = Path(workdir) / (clone.name + ".patch")
    run(["git", "-C", str(worktree), "add", "-N", "."])  # intent-to-add so new files appear in the diff (worktree only)
    diff = run(["git", "-C", str(worktree), "diff", "--binary", "HEAD"]).stdout
    changed = [l[len("+++ b/"):] for l in diff.splitlines() if l.startswith("+++ b/")]
    patch.write_text(diff, encoding="utf-8", newline="\n")
    applied = True
    if diff.strip():
        applied = run(["git", "-C", str(clone), "apply", "--whitespace=nowarn", str(patch)]).returncode == 0
    acc = task_cfg["acceptance"]
    prefixes = acc.get("allowedChangedPathPrefixes", [])
    outside = [c for c in changed if not any(c.startswith(p) for p in prefixes)]
    results = []
    ok = applied and bool(changed) and not outside
    if ok:
        for cmd in acc.get("verificationCommands", []):
            r = run(cmd, cwd=str(clone), timeout=300) if False else subprocess.run(cmd, cwd=str(clone), shell=True, capture_output=True, text=True, timeout=300, encoding="utf-8", errors="replace")
            results.append({"command": cmd, "exit": r.returncode, "tail": (r.stdout + r.stderr)[-300:]})
            ok = ok and r.returncode == 0
    shutil.rmtree(clone, ignore_errors=True)
    return {"performed": True, "applied": applied, "changedPaths": changed, "outsideAllowedPrefixes": outside, "verification": results, "passed": ok}


def run_one(product, fx_repo, task_cfg, timeout_min, outdir):
    t_submit = time.time()
    task = product.call("POST", "/api/v1/orchestrator/coding-tasks", {
        "goal": task_cfg["goal"] + HANDOFF_SUFFIX, "workspaceRoot": str(fx_repo),
        "codingPermissionProfile": "offline", "maxRepairAttempts": task_cfg.get("maxRepairAttempts", 1), "additionalRepositories": []})
    product.call("POST", "/api/v1/orchestrator/schedule", {})
    deadline = time.time() + timeout_min * 60
    timed_out = False
    while True:
        time.sleep(4)
        st = product.call("GET", f"/api/v1/orchestrator/tasks/{task['id']}")
        if st["status"] in TERMINAL:
            break
        if time.time() > deadline:
            timed_out = True
            try:
                product.call("POST", f"/api/v1/orchestrator/tasks/{task['id']}/cancel", {})
            except Exception:
                pass
            time.sleep(10)
            st = product.call("GET", f"/api/v1/orchestrator/tasks/{task['id']}")
            break
    wall = time.time() - t_submit
    logs = [l["message"] for l in product.call("GET", f"/api/v1/orchestrator/tasks/{task['id']}/logs")["logs"]]
    (Path(outdir) / f"task-{task['id']}-logs.json").write_text(json.dumps(logs, indent=1), encoding="utf-8")
    steps = max([int(m.group(1)) for l in logs for m in [re.match(r"^Step (\d+):", l)] if m] or [0])
    batch_calls = sum(1 for l in logs if re.match(r"^Step \d+: batch_read", l))
    phases = parse_phase_total(logs)
    worktree = None
    try:
        worktree = product.call("GET", f"/api/v1/orchestrator/tasks/{task['id']}/desktop-worktree").get("worktreePath")
    except Exception:
        pass
    return {"taskId": task["id"], "status": st["status"], "stage": st.get("stage"), "verification": st.get("verificationStatus"),
            "coreVerified": st["status"] == "completed" and (st.get("verificationStatus") or "").lower() == "pass",
            "timedOut": timed_out, "wallSeconds": round(wall, 1), "agentSeconds": agent_seconds(st), "steps": steps,
            "batchReadSteps": batch_calls, "phasesMs": phases, "worktree": worktree,
            "codingModel": next((l for l in logs if l.startswith("Coding model:")), None)}


def agent_seconds(st):
    try:
        rj = json.loads(st.get("resultJson") or "{}")
        return rj.get("agentElapsedSeconds")
    except Exception:
        return None


def decide(rows_a, rows_b, min_pairs=3, min_effect=0.10):
    """Numbers-only verdict. Both conditions must be correct on the SAME tasks, at least min_pairs
    paired successes, and the median paired wall-time reduction must exceed min_effect AND the
    per-pair direction must be consistent (>= 75%); otherwise INCONCLUSIVE (or NO-IMPROVEMENT)."""
    pairs = []
    for a in rows_a:
        for b in rows_b:
            if a["task"] == b["task"] and a["rep"] == b["rep"] and a["hostPassed"] and b["hostPassed"]:
                pairs.append((a, b))
    if len(pairs) < min_pairs:
        return {"verdict": "INCONCLUSIVE", "reason": f"only {len(pairs)} paired host-verified successes (< {min_pairs})", "pairs": len(pairs)}
    reductions = [(a["wallSeconds"] - b["wallSeconds"]) / a["wallSeconds"] for a, b in pairs if a["wallSeconds"] > 0]
    med = statistics.median(reductions)
    consistent = sum(1 for r in reductions if r > 0) / len(reductions)
    if med >= min_effect and consistent >= 0.75:
        verdict = "IMPROVED"
    elif med <= -min_effect and consistent <= 0.25:
        verdict = "REGRESSED"
    else:
        verdict = "INCONCLUSIVE"
    return {"verdict": verdict, "pairs": len(pairs), "medianWallReduction": round(med, 3), "fractionFaster": round(consistent, 2),
            "reductions": [round(r, 3) for r in reductions]}


def summarise(rows):
    out = {}
    for cond in sorted({r["condition"] for r in rows}):
        sub = [r for r in rows if r["condition"] == cond]
        ok = [r for r in sub if r["hostPassed"]]
        phases = {p: [] for p in PHASES}
        for r in sub:
            for p in PHASES:
                if r.get("phasesMs") and p in r["phasesMs"]:
                    phases[p].append(r["phasesMs"][p])
        out[cond] = {"runs": len(sub), "coreVerified": sum(1 for r in sub if r["coreVerified"]), "hostPassed": len(ok),
                     "medianWallS": statistics.median([r["wallSeconds"] for r in ok]) if ok else None,
                     "medianSteps": statistics.median([r["steps"] for r in ok]) if ok else None,
                     "meanPhaseMs": {p: round(statistics.fmean(v), 1) for p, v in phases.items() if v},
                     "batchReadSteps": sum(r["batchReadSteps"] for r in sub)}
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", required=True)
    ap.add_argument("--env-root", required=True, help="dir containing data/ state/ localai/")
    ap.add_argument("--fixture-repo", required=True, help="git repo containing benchmarks/coding-agent/fixtures/eval-v1")
    ap.add_argument("--corpus", required=True)
    ap.add_argument("--tasks", required=True, help="comma separated task ids")
    ap.add_argument("--reps", type=int, default=2)
    ap.add_argument("--timeout-min", type=int, default=15)
    ap.add_argument("--out", required=True)
    ap.add_argument("--conditions", default="A,B", help="A=features off (baseline), B=features on")
    args = ap.parse_args()
    out = Path(args.out); out.mkdir(parents=True, exist_ok=True)
    corpus = {t["id"]: t for t in json.load(open(args.corpus, encoding="utf-8"))["tasks"]}
    flags = {"A": {"PARUX_CODING_BATCH_READ": "0", "PARUX_CODING_PARENT_CONTINUATION": "0"}, "B": {}}
    rows = []
    for rep in range(1, args.reps + 1):
        for tid in args.tasks.split(","):
            order = args.conditions.split(",")
            if rep % 2 == 0:
                order = order[::-1]  # counterbalance order effects
            for cond in order:
                prod = Product(args.app, args.env_root, flags[cond])
                try:
                    prod.start()
                    res = run_one(prod, args.fixture_repo, corpus[tid], args.timeout_min, out)
                    hc = host_check(args.fixture_repo, res["worktree"], corpus[tid], out)
                finally:
                    prod.stop()
                row = {"condition": cond, "task": tid, "rep": rep, **res, "hostCheck": hc, "hostPassed": bool(hc.get("passed"))}
                rows.append(row)
                (out / "runs.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
                print(json.dumps({k: row[k] for k in ("condition", "task", "rep", "status", "coreVerified", "hostPassed", "steps", "wallSeconds", "batchReadSteps")}), flush=True)
    report = {"summary": summarise(rows), "decision": decide([r for r in rows if r["condition"] == "A"], [r for r in rows if r["condition"] == "B"])}
    (out / "report.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
    print(json.dumps(report, indent=1))


if __name__ == "__main__":
    main()
