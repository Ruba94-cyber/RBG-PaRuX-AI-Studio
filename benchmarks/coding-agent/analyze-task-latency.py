#!/usr/bin/env python3
"""PaRuX coding-task latency evidence analyzer (local only).

Reads the durable orchestrator SQLite database directly and emits machine-readable JSON for BIC-09.
No network access, model call, or mutation is performed. Metrics are derived from authoritative task
and log timestamps, so VS Code, Core UI, CLI and scheduled coding tasks use the same evidence base.
"""
from __future__ import annotations

import argparse
import json
import math
import sqlite3
import statistics
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

TERMINAL = {"completed", "failed", "cancelled"}


def parse_utc(value: str | None) -> datetime | None:
    if not value:
        return None
    text = value.strip().replace("Z", "+00:00")
    parsed = datetime.fromisoformat(text)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def milliseconds(start: datetime | None, end: datetime | None) -> float | None:
    if start is None or end is None:
        return None
    return max(0.0, (end - start).total_seconds() * 1000.0)


def percentile(values: list[float], p: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * p
    low = math.floor(position)
    high = math.ceil(position)
    if low == high:
        return ordered[low]
    return ordered[low] + (ordered[high] - ordered[low]) * (position - low)


def distribution(values: Iterable[float | None]) -> dict[str, Any]:
    samples = [float(value) for value in values if value is not None]
    return {
        "samples": len(samples),
        "minMs": min(samples) if samples else None,
        "p50Ms": percentile(samples, 0.50),
        "p95Ms": percentile(samples, 0.95),
        "maxMs": max(samples) if samples else None,
        "meanMs": statistics.fmean(samples) if samples else None,
    }


def load_rows(connection: sqlite3.Connection) -> list[sqlite3.Row]:
    connection.row_factory = sqlite3.Row
    return list(connection.execute(
        """SELECT id,parent_id,goal,role,status,stage,progress_percent,result_json,
                  created_utc,updated_utc
             FROM agent_tasks
         ORDER BY created_utc ASC"""
    ))


def first_log_times(connection: sqlite3.Connection) -> dict[str, datetime]:
    result: dict[str, datetime] = {}
    for task_id, timestamp in connection.execute(
        """SELECT task_id, MIN(timestamp_utc)
             FROM agent_task_logs
         GROUP BY task_id"""
    ):
        parsed = parse_utc(timestamp)
        if parsed is not None:
            result[str(task_id)] = parsed
    return result


PHASE_PREFIX = "[phase-timing]"
PHASE_TOTAL_PREFIX = "[phase-timing-total]"


def parse_phase_line(message: str | None) -> tuple[str, dict[str, int]] | None:
    """Parses a CodingAgentPhaseTimer log line; returns (kind, {field: ms-or-count}) or None."""
    if not message:
        return None
    text = message.strip()
    if text.startswith(PHASE_TOTAL_PREFIX):
        kind, rest = "total", text[len(PHASE_TOTAL_PREFIX):]
    elif text.startswith(PHASE_PREFIX):
        kind, rest = "step", text[len(PHASE_PREFIX):]
    else:
        return None
    fields: dict[str, int] = {}
    for token in rest.split():
        key, sep, raw = token.partition("=")
        if not sep:
            return None
        raw = raw[:-2] if raw.endswith("ms") else raw
        if not raw.isdigit():
            return None
        fields[key] = int(raw)
    return (kind, fields) if "total" in fields else None


def phase_timing_by_task(connection: sqlite3.Connection) -> dict[str, dict[str, Any]]:
    """Sums per-step phase timings per task (a resumed task has several run segments)."""
    result: dict[str, dict[str, Any]] = {}
    for task_id, message in connection.execute(
        "SELECT task_id, message FROM agent_task_logs WHERE message LIKE '[phase-timing]%' ORDER BY timestamp_utc ASC"
    ):
        parsed = parse_phase_line(message)
        if parsed is None or parsed[0] != "step":
            continue
        entry = result.setdefault(str(task_id), {"steps": 0, "phasesMs": {}})
        entry["steps"] += 1
        for key, value in parsed[1].items():
            if key == "step":
                continue
            entry["phasesMs"][key] = entry["phasesMs"].get(key, 0) + value
    return result


def changed_file_count(result_json: str | None) -> int | None:
    if not result_json:
        return None
    try:
        value = json.loads(result_json)
    except (json.JSONDecodeError, TypeError):
        return None
    if not isinstance(value, dict):
        return None
    for key in ("changed_files", "changedFiles", "files"):
        files = value.get(key)
        if isinstance(files, list):
            return len(files)
    return None


def analyze(database: Path) -> dict[str, Any]:
    uri = f"file:{database.as_posix()}?mode=ro"
    with sqlite3.connect(uri, uri=True) as connection:
        rows = load_rows(connection)
        first_logs = first_log_times(connection)
        phases = phase_timing_by_task(connection)

    by_id = {str(row["id"]): row for row in rows}
    samples: list[dict[str, Any]] = []
    for row in rows:
        task_id = str(row["id"])
        created = parse_utc(row["created_utc"])
        updated = parse_utc(row["updated_utc"])
        first_log = first_logs.get(task_id)
        parent_id = row["parent_id"]
        parent = by_id.get(str(parent_id)) if parent_id else None
        parent_created = parse_utc(parent["created_utc"]) if parent else None
        parent_updated = parse_utc(parent["updated_utc"]) if parent else None
        child_created = created if parent else None

        sample = {
            "taskId": task_id,
            "parentId": parent_id,
            "role": row["role"],
            "status": row["status"],
            "stage": row["stage"],
            "goal": row["goal"],
            "createdUtc": created.isoformat() if created else None,
            "updatedUtc": updated.isoformat() if updated else None,
            "queueToFirstEventMs": milliseconds(created, first_log),
            "endToEndMs": milliseconds(created, updated) if str(row["status"]).lower() in TERMINAL else None,
            "parentCreatedToChildCreatedMs": milliseconds(parent_created, child_created),
            "parentLastUpdateToChildCreatedMs": milliseconds(parent_updated, child_created),
            "changedFileCount": changed_file_count(row["result_json"]),
            "phaseTiming": phases.get(task_id),
        }
        samples.append(sample)

    coding = [sample for sample in samples if str(sample.get("role") or "").lower() == "coding"]
    delegated = [sample for sample in samples if sample.get("parentId")]
    multi_file = [sample for sample in coding if (sample.get("changedFileCount") or 0) > 1]

    return {
        "schemaVersion": 1,
        "source": str(database.resolve()),
        "capturedUtc": datetime.now(timezone.utc).isoformat(),
        "taskCount": len(samples),
        "codingTaskCount": len(coding),
        "delegatedTaskCount": len(delegated),
        "multiFileCodingTaskCount": len(multi_file),
        "codingQueueToFirstEvent": distribution(sample["queueToFirstEventMs"] for sample in coding),
        "codingEndToEnd": distribution(sample["endToEndMs"] for sample in coding),
        "delegationParentToChild": distribution(sample["parentCreatedToChildCreatedMs"] for sample in delegated),
        "delegationAfterParentUpdate": distribution(sample["parentLastUpdateToChildCreatedMs"] for sample in delegated),
        "multiFileEndToEnd": distribution(sample["endToEndMs"] for sample in multi_file),
        "phaseTimedTaskCount": sum(1 for sample in samples if sample.get("phaseTiming")),
        "samples": samples,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyze durable PaRuX coding-task latency evidence.")
    parser.add_argument("database", type=Path, help="Path to orchestrator.db")
    parser.add_argument("--output", type=Path, help="Optional JSON output path; stdout is always emitted")
    args = parser.parse_args()

    if not args.database.is_file():
        parser.error(f"database does not exist: {args.database}")

    report = analyze(args.database)
    encoded = json.dumps(report, indent=2, ensure_ascii=False)
    print(encoded)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.output.with_suffix(args.output.suffix + ".tmp")
        temporary.write_text(encoded + "\n", encoding="utf-8")
        temporary.replace(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
