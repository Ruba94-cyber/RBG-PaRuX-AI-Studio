"""Record file-level provenance for an explicitly declared required-file subset.

This does not claim a complete Hub repository snapshot. The required-files JSON is a list of
repository-relative filenames used by the supported runtime/training contract. Every declared file
must exist locally and remotely and have verifiable bytes. Symlinks, traversal, and root escapes fail.
Usage: record_provenance.py <repo_id> <revision> <out.json> (--local-dir|--hf-home) <root> <required-files.json>
"""
import hashlib
import json
import os
import sys
from pathlib import Path, PurePosixPath

from huggingface_hub import HfApi

from safety import reserve_output, save_reserved_json

ACCEPTED = {"apache-2.0", "mit", "cc-by-4.0"}


def _parse_manifest(path):
    value = json.loads(Path(path).read_text(encoding="utf-8"))
    files = value.get("required_files") if isinstance(value, dict) else value
    if not isinstance(files, list) or not files or any(not isinstance(item, str) for item in files):
        raise ValueError("required-file manifest must be a nonempty JSON list of strings")
    clean = []
    for name in files:
        rel = PurePosixPath(name)
        if not name or "\\" in name or rel.is_absolute() or any(part in ("", ".", "..") for part in rel.parts):
            raise ValueError(f"unsafe required-file path: {name!r}")
        clean.append(rel.as_posix())
    if len(set(clean)) != len(clean):
        raise ValueError("required-file manifest contains duplicates")
    return clean


def _confined_regular_file(root: Path, relative: str) -> Path:
    base = root.resolve(strict=True)
    candidate = base
    for part in PurePosixPath(relative).parts:
        candidate = candidate / part
        if candidate.is_symlink():
            raise ValueError(f"symlink is not accepted in provenance snapshot: {relative}")
    real = candidate.resolve(strict=True)
    try:
        real.relative_to(base)
    except ValueError as exc:
        raise ValueError(f"resolved path escaped provenance root: {relative}") from exc
    if not real.is_file():
        raise ValueError(f"required snapshot path is not a regular file: {relative}")
    return real


def _hash_file(path: Path):
    sha256 = hashlib.sha256()
    git_sha1 = hashlib.sha1()
    size = path.stat().st_size
    git_sha1.update(f"blob {size}\0".encode("ascii"))
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            sha256.update(chunk)
            git_sha1.update(chunk)
    return size, sha256.hexdigest(), git_sha1.hexdigest()


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if len(args) != 6 or args[3] not in ("--local-dir", "--hf-home"):
        raise SystemExit("Usage: record_provenance.py <repo_id> <revision> <out.json> (--local-dir|--hf-home) <root> <required-files.json>")
    repo, revision, out, mode, root_arg, manifest_path = args
    output = reserve_output(out)
    record = {"status": "running", "complete": False, "scope": "declared required files only; not a complete Hub snapshot",
              "repo": repo, "requested_revision": revision, "files": []}
    try:
        required = _parse_manifest(manifest_path)
        info = HfApi().model_info(repo, revision=revision, files_metadata=True)
        licence = (info.card_data.get("license") if info.card_data else None) or next(
            (tag.split(":", 1)[1] for tag in info.tags if tag.startswith("license:")), None)
        siblings = {item.rfilename: item for item in info.siblings}
        missing_remote = sorted(set(required) - siblings.keys())
        if missing_remote:
            raise RuntimeError(f"required files absent from resolved Hub revision: {missing_remote}")
        root = Path(root_arg).resolve(strict=True)
        rows = []
        for name in required:
            sibling = siblings[name]
            if mode == "--local-dir":
                file_path = _confined_regular_file(root, name)
            else:
                cache_rel = PurePosixPath("hub", "models--" + repo.replace("/", "--"), "snapshots", info.sha, *PurePosixPath(name).parts)
                file_path = _confined_regular_file(root, cache_rel.as_posix())
            size, sha256, git_sha1 = _hash_file(file_path)
            expected_size = getattr(sibling, "size", None)
            if expected_size is not None and int(expected_size) != size:
                raise RuntimeError(f"required file size differs from Hub metadata: {name}")
            lfs = getattr(sibling, "lfs", None)
            expected_lfs = getattr(lfs, "sha256", None) if lfs else None
            blob_id = getattr(sibling, "blob_id", None)
            if expected_lfs:
                verified_by, matches = "hub-lfs-sha256", sha256.lower() == str(expected_lfs).lower()
            elif blob_id:
                verified_by, matches = "hub-git-blob-sha1", git_sha1.lower() == str(blob_id).lower()
            else:
                raise RuntimeError(f"Hub exposes no verifiable digest for required file: {name}")
            if not matches:
                raise RuntimeError(f"required file digest mismatch: {name}")
            rows.append({"file": name, "size": size, "sha256": sha256,
                         "upstream_digest_kind": verified_by, "upstream_digest_verified": True})
        record.update({"status": "complete", "complete": True, "resolved_revision": info.sha,
                       "licence": licence, "licence_accepted": (licence or "").lower() in ACCEPTED,
                       "required_file_count": len(required), "files": rows,
                       "total_required_bytes": sum(row["size"] for row in rows),
                       "all_declared_required_files_present_and_verified": True})
        if not record["licence_accepted"]:
            record.update(status="failed", complete=False, failure="licence not in accepted permissive set")
            save_reserved_json(output, record); output.close()
            return 2
        save_reserved_json(output, record); output.close()
        print(json.dumps({k: v for k, v in record.items() if k != "files"}))
        return 0
    except BaseException as exc:
        record.update(status="failed", complete=False, failure=f"{type(exc).__name__}: {exc}")
        save_reserved_json(output, record); output.close()
        raise


if __name__ == "__main__":
    raise SystemExit(main())
