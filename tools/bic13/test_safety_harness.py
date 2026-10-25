"""CPU-only safety regressions. All subprocess/model fixtures are explicitly synthetic."""
from __future__ import annotations

import base64
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest import mock
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from safety import OwnedProcessTree, ensure_fresh_directory, read_before_deadline, reserve_output, save_reserved_json  # noqa: E402
from safety import BoundedLineReader  # noqa: E402
import io  # noqa: E402

SYNTHETIC_WORKER = r'''import base64, json, os, sys, time
mode = os.environ.get("BIC13_SYNTHETIC_WORKER_MODE", "success")
if mode == "ready-timeout": time.sleep(30)
print(json.dumps({"type":"ready","sampleRate":24000,"syntheticFixture":True}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    if request.get("op") == "shutdown": break
    rid = request["id"]
    if mode == "error" and rid == "ws-0":
        print(json.dumps({"type":"error","id":rid,"error":"SYNTHETIC fixture failure"}), flush=True)
        continue
    if mode == "flood" and rid in ("ws-0", "stt-0", "tts-train-0"):
        for _ in range(150):
            print(json.dumps({"type":"audio","id":rid,"base64":base64.b64encode(b"\0\0").decode()}), flush=True)
            time.sleep(0.001)
        print(json.dumps({"type":"done","id":rid,"ttfaMilliseconds":1}), flush=True)
        continue
    if mode == "mm-error" and rid == "tts-train-0":
        print(json.dumps({"type":"error","id":rid,"error":"SYNTHETIC fixture failure"}), flush=True)
        continue
    if mode == "timeout" and rid in ("ws-0", "stt-0"):
        time.sleep(30)
        continue
    print(json.dumps({"type":"audio","id":rid,"base64":base64.b64encode(b"\0\0").decode()}), flush=True)
    if rid == "barge-1":
        time.sleep(30)
        continue
    print(json.dumps({"type":"audio","id":rid,"base64":base64.b64encode(b"\0\0").decode()}), flush=True)
    print(json.dumps({"type":"done","id":rid,"ttfaMilliseconds":1}), flush=True)
'''


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


class SafetyHarnessTests(unittest.TestCase):
    def test_non_windows_owned_process_launch_fails_before_popen(self):
        with mock.patch("safety.os.name", "posix"), mock.patch("safety.subprocess.Popen") as popen:
            with self.assertRaisesRegex(NotImplementedError, "Windows Job Objects"):
                OwnedProcessTree.start([sys.executable, "-c", "pass"])
            popen.assert_not_called()

    @unittest.skipUnless(os.name == "nt", "owned worker tests require Windows Job Objects")
    def test_stt_nonzero_asr_child_cleans_owned_descendant_and_preserves_sibling(self):
        stt = load_module("bic13_stt_nonzero_cleanup_test", HERE / "measure_stt_quality.py")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            owned_marker = root / "SYNTHETIC-owned-descendant-survived"
            sibling_marker = root / "SYNTHETIC-unrelated-sibling-alive"
            descendant = root / "SYNTHETIC-descendant.py"
            descendant.write_text(
                "import sys,time\nfrom pathlib import Path\ntime.sleep(0.3)\nPath(sys.argv[1]).write_text('unexpectedly survived')\n",
                encoding="utf-8")
            child = root / "SYNTHETIC-asr-child.py"
            child.write_text(
                "import subprocess,sys\n"
                "subprocess.Popen([sys.executable,sys.argv[1],sys.argv[2]], stdin=subprocess.DEVNULL, "
                "stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n"
                "raise SystemExit(7)\n", encoding="utf-8")
            sibling = root / "SYNTHETIC-unrelated.py"
            sibling.write_text(
                "import sys,time\nfrom pathlib import Path\ntime.sleep(0.7)\nPath(sys.argv[1]).write_text('alive')\n",
                encoding="utf-8")
            unrelated = subprocess.Popen([sys.executable, str(sibling), str(sibling_marker)],
                                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                         stderr=subprocess.DEVNULL)
            real_start = OwnedProcessTree.start

            def launch_fixture(args, **kwargs):
                if "--asr-child" in args:
                    return real_start([sys.executable, str(child), str(descendant), str(owned_marker)], **kwargs)
                return real_start(args, **kwargs)

            stt_owner = types.SimpleNamespace(start=staticmethod(launch_fixture))
            try:
                with mock.patch.object(stt, "OwnedProcessTree", stt_owner), \
                     mock.patch.object(stt, "synthesize_worker", return_value=["SYNTHETIC.wav"] * len(stt.SENTENCES)):
                    with self.assertRaisesRegex(RuntimeError, "exited 7"):
                        stt.main(["SYNTHETIC-worker.py", str(root / "result.json"), str(root / "wavs")])
                unrelated.wait(timeout=5)
                self.assertEqual(sibling_marker.read_text(encoding="utf-8"), "alive")
                self.assertFalse(owned_marker.exists(), "the ASR Job Object should terminate its descendant")
                result = json.loads((root / "result.json").read_text(encoding="utf-8"))
                self.assertFalse(result["benchmark_complete"])
                self.assertEqual(result["status"], "failed")
            finally:
                if unrelated.poll() is None:
                    unrelated.terminate(); unrelated.wait(timeout=5)

    def test_exclusive_output_and_nonempty_directory_refusal(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "evidence.json"
            handle = reserve_output(path)
            save_reserved_json(handle, {"SYNTHETIC": True, "value": 1})
            handle.close()
            original = path.read_bytes()
            with self.assertRaises(FileExistsError):
                reserve_output(path)
            self.assertEqual(path.read_bytes(), original)
            directory = Path(temp) / "data"
            directory.mkdir(); (directory / "old.json").write_text("preserve", encoding="utf-8")
            with self.assertRaises(FileExistsError):
                ensure_fresh_directory(directory)
            self.assertEqual((directory / "old.json").read_text(encoding="utf-8"), "preserve")
            linked = Path(temp) / "linked"
            target = Path(temp) / "outside"
            target.mkdir()
            try:
                linked.symlink_to(target, target_is_directory=True)
            except OSError:
                pass
            else:
                with self.assertRaises(FileExistsError):
                    ensure_fresh_directory(linked)

    def test_owned_tree_timeout_kills_only_owned_launch(self):
        if os.name != "nt":
            self.skipTest("Windows Job Object ownership probe is covered on Windows host")
        with tempfile.TemporaryDirectory() as temp:
            marker = Path(temp) / "SYNTHETIC-unrelated-alive"
            unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
            owner = OwnedProcessTree.start([sys.executable, "-c", "import time; time.sleep(30)"],
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                owner.cleanup(timeout=10)
                self.assertIsNotNone(owner.process.poll())
                self.assertIsNone(unrelated.poll())
                marker.write_text("SYNTHETIC owned launch stopped; unrelated remains", encoding="utf-8")
            finally:
                unrelated.terminate(); unrelated.wait(timeout=5)
            self.assertTrue(marker.exists())

    @unittest.skipUnless(os.name == "nt", "owned worker tests require Windows Job Objects")
    def test_tts_synthetic_success_and_required_phase_failure_are_truthful(self):
        tool = HERE / "measure_tts_stream.py"
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            worker = root / "SYNTHETIC-worker.py"
            worker.write_text(SYNTHETIC_WORKER, encoding="utf-8")
            output = root / "synthetic-success.json"
            wavs = root / "wav-success"
            run = subprocess.run([sys.executable, str(tool), str(worker), str(output), str(wavs)],
                                 capture_output=True, text=True, timeout=20)
            self.assertEqual(run.returncode, 0, run.stderr)
            result = json.loads(output.read_text(encoding="utf-8"))
            self.assertTrue(result["benchmark_complete"])
            self.assertTrue(result["synthetic_fixture"])
            self.assertFalse(result["performance_benchmark"])
            self.assertEqual(len(result["warm_short"]["runs"]), 5)
            self.assertEqual(len(result["warm_long"]["runs"]), 3)
            self.assertTrue(result["barge_in"]["owned_tree_cleanup_verified"])

            failed_out = root / "synthetic-failure.json"
            env = dict(os.environ, BIC13_SYNTHETIC_WORKER_MODE="error")
            failed = subprocess.run([sys.executable, str(tool), str(worker), str(failed_out)],
                                    capture_output=True, text=True, timeout=20, env=env)
            self.assertNotEqual(failed.returncode, 0)
            failure = json.loads(failed_out.read_text(encoding="utf-8"))
            self.assertFalse(failure["benchmark_complete"])
            self.assertEqual(failure["status"], "failed")
            reused = subprocess.run([sys.executable, str(tool), str(worker), str(output)],
                                    capture_output=True, text=True, timeout=10)
            self.assertNotEqual(reused.returncode, 0)

            # Tighten the in-process fixture deadline to test frame timeout without waiting on production limits.
            tts = load_module("bic13_tts_timeout_synthetic_test", tool)
            tts.REQUEST_TIMEOUT_SECONDS = 0.05
            tts.READY_TIMEOUT_SECONDS = 2
            timeout_worker = None
            try:
                with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "timeout"}):
                    timeout_worker = tts.Worker(str(worker))
                    with self.assertRaises(TimeoutError):
                        timeout_worker.request("ws-0", "SYNTHETIC bounded timeout")
            finally:
                if timeout_worker is not None:
                    timeout_worker.cleanup()
            tts.READY_TIMEOUT_SECONDS = 0.05
            with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "ready-timeout"}):
                with self.assertRaises(TimeoutError):
                    tts.Worker(str(worker))

            tts.REQUEST_TIMEOUT_SECONDS = 0.05
            tts.READY_TIMEOUT_SECONDS = 2
            flood_worker = None
            try:
                with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "flood"}):
                    flood_worker = tts.Worker(str(worker))
                    started = __import__("time").monotonic()
                    with self.assertRaises(TimeoutError):
                        flood_worker.request("ws-0", "SYNTHETIC continuous timely frames")
                    self.assertLess(__import__("time").monotonic() - started, 1.0)
            finally:
                if flood_worker is not None:
                    flood_worker.cleanup()

    def test_bounded_line_reader_surfaces_timeout(self):
        release = __import__("threading").Event()
        class BlockingStream:
            def __iter__(self): return self
            def __next__(self):
                release.wait(1)
                raise StopIteration
        reader = BoundedLineReader(BlockingStream())
        with self.assertRaises(TimeoutError):
            reader.read(0.01, "SYNTHETIC blocked frame")
        release.set()

    def test_absolute_deadline_is_checked_before_read(self):
        reader = mock.Mock()
        with self.assertRaises(TimeoutError):
            read_before_deadline(reader, __import__("time").monotonic() - 1, "SYNTHETIC expired frame")
        reader.read.assert_not_called()

    def test_plain_dataset_generator_preserves_existing_output(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp) / "SYNTHETIC-dataset"
            command = [sys.executable, str(HERE / "make_datasets.py"), str(out), "17"]
            first = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertEqual(first.returncode, 0, first.stderr)
            heldout = out / "heldout.jsonl"
            original = heldout.read_bytes()
            again = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertNotEqual(again.returncode, 0)
            self.assertEqual(heldout.read_bytes(), original)
            meta = json.loads((out / "dataset-metadata.json").read_text(encoding="utf-8"))
            self.assertTrue(meta["synthetic"])

    @unittest.skipUnless(os.name == "nt", "owned worker tests require Windows Job Objects")
    def test_stt_and_multimodal_tts_worker_frames_are_bounded_and_validated(self):
        stt = load_module("bic13_stt_synthetic_test", HERE / "measure_stt_quality.py")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            worker = root / "SYNTHETIC-worker.py"
            worker.write_text(SYNTHETIC_WORKER, encoding="utf-8")
            wav_dir = root / "stt-wavs"
            wav_dir.mkdir()
            wavs = stt.synthesize_worker(str(worker), wav_dir)
            self.assertEqual(len(wavs), len(stt.SENTENCES))
            self.assertTrue(all(Path(path).stat().st_size > 44 for path in wavs))

            stt.FRAME_TIMEOUT_SECONDS = 0.05
            failed_wavs = root / "stt-timeout-wavs"; failed_wavs.mkdir()
            with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "timeout"}):
                with self.assertRaises(TimeoutError):
                    stt.synthesize_worker(str(worker), failed_wavs)
            stt.FRAME_TIMEOUT_SECONDS = 0.05
            flood_wavs = root / "stt-flood-wavs"; flood_wavs.mkdir()
            with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "flood"}):
                with self.assertRaises(TimeoutError):
                    stt.synthesize_worker(str(worker), flood_wavs)

            out = root / "multimodal"
            env = dict(os.environ)
            run = subprocess.run([sys.executable, str(HERE / "make_multimodal_datasets.py"), str(out), str(worker)],
                                 capture_output=True, text=True, timeout=30, env=env)
            self.assertEqual(run.returncode, 0, run.stderr)
            meta = json.loads((out / "dataset-metadata.json").read_text(encoding="utf-8"))
            self.assertEqual(meta["status"], "complete")
            self.assertIn("no unseen-combination generalization claim", meta["vision_split_limitation"])
            self.assertTrue(meta["synthetic"])
            old = (out / "vision-heldout.jsonl").read_bytes()
            again = subprocess.run([sys.executable, str(HERE / "make_multimodal_datasets.py"), str(out), str(worker)],
                                   capture_output=True, text=True, timeout=10)
            self.assertNotEqual(again.returncode, 0)
            self.assertEqual((out / "vision-heldout.jsonl").read_bytes(), old)

            failed_out = root / "multimodal-failed"
            with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "mm-error"}):
                failed = subprocess.run([sys.executable, str(HERE / "make_multimodal_datasets.py"),
                                          str(failed_out), str(worker)], capture_output=True, text=True, timeout=20)
            self.assertNotEqual(failed.returncode, 0)
            failed_meta = json.loads((failed_out / "dataset-metadata.json").read_text(encoding="utf-8"))
            self.assertEqual(failed_meta["status"], "failed")
            self.assertFalse(failed_meta["complete"])

            multi = root / "multimodal-flood"
            old_argv = sys.argv
            try:
                sys.argv = [str(HERE / "make_multimodal_datasets.py"), str(multi), str(worker)]
                module = load_module("bic13_multimodal_flood_synthetic_test", HERE / "make_multimodal_datasets.py")
            finally:
                sys.argv = old_argv
            module.FRAME_TIMEOUT_SECONDS = 0.05
            with mock.patch.dict(os.environ, {"BIC13_SYNTHETIC_WORKER_MODE": "flood"}):
                with self.assertRaises(TimeoutError):
                    module.main()
            flood_meta = json.loads((multi / "dataset-metadata.json").read_text(encoding="utf-8"))
            self.assertFalse(flood_meta["complete"])

    def test_provenance_complete_manifest_hash_truth_and_traversal_rejection(self):
        fake = types.ModuleType("huggingface_hub")
        payload = b"SYNTHETIC provenance fixture"
        git_blob = hashlib.sha1(f"blob {len(payload)}\0".encode() + payload).hexdigest()

        class Sibling:
            rfilename = "weights.bin"
            size = len(payload)
            lfs = None
            blob_id = git_blob

        class Info:
            sha = "synthetic-revision"
            card_data = {"license": "mit"}
            tags = []
            siblings = [Sibling()]

        class Api:
            def model_info(self, *args, **kwargs):
                return Info()

        fake.HfApi = Api
        old = sys.modules.get("huggingface_hub")
        sys.modules["huggingface_hub"] = fake
        try:
            module = load_module("bic13_provenance_synthetic_test", HERE / "record_provenance.py")
            with tempfile.TemporaryDirectory() as temp:
                root = Path(temp); local = root / "snapshot"; local.mkdir()
                (local / "weights.bin").write_bytes(payload)
                manifest = root / "required.json"; manifest.write_text('["weights.bin"]', encoding="utf-8")
                out = root / "valid.json"
                self.assertEqual(module.main(["test/repo", "main", str(out), "--local-dir", str(local), str(manifest)]), 0)
                record = json.loads(out.read_text(encoding="utf-8"))
                self.assertTrue(record["complete"])
                self.assertEqual(record["files"][0]["upstream_digest_kind"], "hub-git-blob-sha1")
                self.assertIn("not a complete Hub snapshot", record["scope"])

                missing_manifest = root / "missing.json"; missing_manifest.write_text('["absent.bin"]', encoding="utf-8")
                missing_out = root / "missing-out.json"
                with self.assertRaises(RuntimeError):
                    module.main(["test/repo", "main", str(missing_out), "--local-dir", str(local), str(missing_manifest)])
                self.assertFalse(json.loads(missing_out.read_text(encoding="utf-8"))["complete"])

                traversal = root / "traversal.json"; traversal.write_text('["../escape.bin"]', encoding="utf-8")
                traversal_out = root / "traversal-out.json"
                with self.assertRaises(ValueError):
                    module.main(["test/repo", "main", str(traversal_out), "--local-dir", str(local), str(traversal)])
                self.assertFalse(json.loads(traversal_out.read_text(encoding="utf-8"))["complete"])

                (local / "weights.bin").write_bytes(b"tampered")
                mismatch_out = root / "mismatch.json"
                with self.assertRaises(RuntimeError):
                    module.main(["test/repo", "main", str(mismatch_out), "--local-dir", str(local), str(manifest)])
                self.assertFalse(json.loads(mismatch_out.read_text(encoding="utf-8"))["complete"])

                (local / "weights.bin").write_bytes(payload)
                Sibling.blob_id = None
                unverifiable = root / "unverifiable.json"
                with self.assertRaises(RuntimeError):
                    module.main(["test/repo", "main", str(unverifiable), "--local-dir", str(local), str(manifest)])
                self.assertFalse(json.loads(unverifiable.read_text(encoding="utf-8"))["complete"])

                outside = root / "outside.bin"; outside.write_bytes(payload)
                link = local / "link.bin"
                try:
                    link.symlink_to(outside)
                except OSError:
                    pass
                else:
                    with self.assertRaises(ValueError):
                        module._confined_regular_file(local, "link.bin")
        finally:
            if old is None:
                sys.modules.pop("huggingface_hub", None)
            else:
                sys.modules["huggingface_hub"] = old

    def test_evaluators_default_to_redacted_privacy_contract(self):
        text = (HERE / "eval_adapter.py").read_text(encoding="utf-8") + (HERE / "eval_vision_adapter.py").read_text(encoding="utf-8")
        self.assertGreaterEqual(text.count('"model_identity"'), 2)
        self.assertIn('if include_sensitive_samples else "redacted"', text)
        self.assertIn('"sample_text_opt_in": include_sensitive_samples', text)
        vision = (HERE / "eval_vision_adapter.py").read_text(encoding="utf-8")
        self.assertIn("base_id_override or PeftConfig.from_pretrained(adapter_dir).base_model_name_or_path", vision)
        self.assertIn("base_id_for_adapter_override applies only when evaluating an adapter", vision)

    @unittest.skipUnless(shutil.which("pwsh") or shutil.which("powershell"), "PowerShell unavailable")
    def test_training_matrix_fails_fast_with_unique_run_state(self):
        ps = shutil.which("pwsh") or shutil.which("powershell")
        source = HERE / "run-training-matrix.ps1"
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            driver = root / "run-training-matrix.ps1"
            shutil.copy2(source, driver)
            child = root / "run-training-method.ps1"
            child.write_text("param([string]$Method,[string]$EvidenceRoot,[int]$MaxSteps,[double]$LearningRate)\n"
                             "Add-Content -LiteralPath (Join-Path $EvidenceRoot 'synthetic-started.txt') -Value $Method\n"
                             "if ($Method -eq 'dpo') { exit 7 }\nexit 0\n", encoding="utf-8")
            evidence = root / "evidence"; evidence.mkdir()
            run = subprocess.run([ps, "-NoProfile", "-File", str(driver), "-EvidenceRoot", str(evidence)],
                                 capture_output=True, text=True, timeout=20)
            self.assertNotEqual(run.returncode, 0)
            self.assertEqual((evidence / "synthetic-started.txt").read_text(encoding="utf-8").strip(), "dpo")
            states = list((evidence / "matrix-runs").glob("*/matrix-status.json"))
            self.assertEqual(len(states), 1)
            state = json.loads(states[0].read_text(encoding="utf-8"))
            self.assertEqual(state["status"], "failed")
            self.assertFalse(state["complete"])
            self.assertEqual(len(state["methods"]), 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
