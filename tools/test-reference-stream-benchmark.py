"""Exercise benchmark reporting against deterministic HTTP/SSE fixtures, not a real model."""
import json
import hashlib
import os
import pathlib
import subprocess
import tempfile
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = pathlib.Path(__file__).resolve().parents[1]
POWERSHELL = os.environ.get('PARUX_BENCHMARK_TEST_SHELL', 'pwsh')


class Handler(BaseHTTPRequestHandler):
    mode = 'plain'
    mutate_path = None
    request_models = []
    request_payloads = []
    request_counts = {}
    request_lock = threading.Lock()

    def log_message(self, *_):
        pass

    def do_GET(self):
        if not self.path.endswith('/v1/models'):
            self.send_response(404)
            self.end_headers()
            return
        provider = self.path.split('/')[1]
        model = {'alpha': 'model-a', 'beta': 'model-b'}.get(provider)
        if model is None:
            self.send_response(404)
            self.end_headers()
            return
        body = json.dumps({'data': [{'id': model}]}).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        request_body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
        if self.path.endswith('/v1/embeddings'):
            self.send_response(404)
            self.end_headers()
            return
        if not self.path.endswith('/v1/chat/completions'):
            self.send_response(404)
            self.end_headers()
            return
        request_payload = json.loads(request_body)
        requested_model = request_payload.get('model')
        with self.request_lock:
            self.request_models.append(requested_model)
            self.request_payloads.append(request_payload)
            self.request_counts[requested_model] = self.request_counts.get(requested_model, 0) + 1
            fail_this_request = (self.mode == 'late_fail' and requested_model == 'model-b'
                                 and self.request_counts[requested_model] == 3)
            mutate_path = self.mutate_path if self.mode in ('artifact_mutation', 'manifest_mutation') and len(self.request_models) == 1 else None
        if mutate_path:
            pathlib.Path(mutate_path).write_bytes(b'changed fixture bytes')
        if fail_this_request:
            failure_body = b'controlled fixture failure'
            self.send_response(500)
            self.send_header('Content-Length', str(len(failure_body)))
            self.end_headers()
            self.wfile.write(failure_body)
            return
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.end_headers()
        if self.mode == 'stall':
            self.wfile.flush()
            time.sleep(3)
            return
        try:
            if self.mode == 'competitive' and requested_model == 'model-b':
                time.sleep(.12)
            if self.mode == 'reasoning':
                self.event({'choices': [{'delta': {'reasoning_content': 'reason first'}}]})
            self.event({'choices': [{'delta': {'content': 'Answer '}}]})
            time.sleep(.03)
            self.event({'choices': [{'delta': {'content': 'complete.'}}]})
            self.event({'choices': [{'delta': {}, 'finish_reason': 'stop'}], 'usage': {
                'completion_tokens': 10, 'completion_tokens_details': {
                    'reasoning_tokens': 5 if self.mode == 'reasoning' else 0}}})
            self.wfile.write(b'data: [DONE]\n\n')
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass

    def event(self, obj):
        self.wfile.write(('data: ' + json.dumps(obj) + '\n\n').encode())
        self.wfile.flush()


server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()


def write_parity_manifest(path, cases, mismatch=None):
    """Create operator-attested parity input for synthetic competitive-gate fixtures."""
    path.parent.mkdir(parents=True, exist_ok=True)
    providers = {}
    runtime_hash = hashlib.sha256(b'fixture-runtime-context=8192;parallel=1').hexdigest()
    for index, case in enumerate(cases):
        name, rest = case.split('=', 1)
        _, model_id = rest.rsplit('|', 1)
        content = b'fixture-model-weights'
        if mismatch == 'artifact' and index == 1:
            content = b'different-fixture-model-weights'
        model_path = path.parent / f'{name}-weights.gguf'
        model_path.write_bytes(content)
        provider = {
            'apiModelId': model_id,
            'artifactPath': str(model_path),
            'artifactSha256': (
                '0' * 64 if mismatch == 'artifact-hash' and index == 1
                else hashlib.sha256(content).hexdigest()
            ),
            'runtimeSettingsSha256': (
                hashlib.sha256(b'different-fixture-runtime').hexdigest()
                if mismatch == 'runtime' and index == 1 else runtime_hash
            ),
            'chatTemplateSha256': hashlib.sha256(
                b'different-fixture-chat-template' if mismatch == 'template' and index == 1
                else b'fixture-chat-template'
            ).hexdigest(),
            'contextTokens': 4096 if mismatch == 'context' and index == 1 else 8192,
            'parallel': 1,
            'warmStateConfirmed': True,
            'warmupRequests': 3 if mismatch == 'warmup' and index == 1 else 2,
            'verificationNote': 'Synthetic fixture attestation; no real model is used.',
        }
        if mismatch == 'fractional-context' and index == 1:
            provider['contextTokens'] = 8192.5
        elif mismatch == 'string-parallel' and index == 1:
            provider['parallel'] = '1'
        elif mismatch == 'missing-warmup' and index == 1:
            del provider['warmupRequests']
        providers[name] = provider
    path.write_text(json.dumps({
        'schemaVersion': 1,
        'verifiedBy': 'synthetic-test-fixture',
        'verifiedUtc': datetime.now(timezone.utc).isoformat(),
        'providers': providers,
    }), encoding='utf-8')
try:
    with tempfile.TemporaryDirectory(prefix='parux-stream-test-') as directory:
        for mode in ('plain', 'reasoning', 'stall', 'assert_smoke', 'assert_single', 'order', 'assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'bootstrap_boundary', 'late_fail', 'parity_missing', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup', 'tolerance_too_high', 'parity_smoke', 'artifact_mutation', 'manifest_mutation'):
            Handler.mode = 'plain' if mode in ('assert_single', 'order', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'bootstrap_boundary', 'parity_smoke') else 'competitive' if mode == 'assert_pair' else mode
            Handler.mutate_path = None
            with Handler.request_lock:
                Handler.request_models.clear()
                Handler.request_payloads.clear()
                Handler.request_counts.clear()
            output = pathlib.Path(directory) / mode
            cases = [f'fixture=http://127.0.0.1:{server.server_port}|model']
            repetitions = '20' if mode in ('assert_single', 'assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'parity_missing', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup', 'artifact_mutation', 'manifest_mutation') else '3' if mode in ('order', 'parity_smoke') else '2'
            if mode in ('order', 'assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'parity_missing', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup', 'parity_smoke', 'artifact_mutation', 'manifest_mutation'):
                cases = [f'alpha=http://127.0.0.1:{server.server_port}|model-a',
                         f'beta=http://127.0.0.1:{server.server_port}|model-b']
            script = str(ROOT / 'scripts/benchmark-reference-stream.ps1')
            if mode in ('order', 'assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'parity_missing', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup', 'parity_smoke', 'artifact_mutation', 'manifest_mutation'):
                escaped_script = script.replace("'", "''")
                case_array = ', '.join("'" + case.replace("'", "''") + "'" for case in cases)
                command = (f"& '{escaped_script}' -Cases @({case_array}) "
                           f"-ModelIdentity 'synthetic-fixture' -Repetitions {repetitions} "
                           f"-MaxTokens 16 -TimeoutSeconds 1 -OutputDirectory '{output}'")
                if mode in ('assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'artifact_mutation', 'manifest_mutation'):
                    command += " -AssertProvider alpha"
                elif mode in ('parity_missing', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup'):
                    command += " -AssertProvider alpha"
                if mode in ('assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup', 'parity_smoke', 'artifact_mutation', 'manifest_mutation'):
                    manifest_path = output / 'parity-manifest.json'
                    mismatch = {
                        'parity_mismatch': 'artifact',
                        'artifact_hash_mismatch': 'artifact-hash',
                        'runtime_mismatch': 'runtime',
                        'template_mismatch': 'template',
                        'context_mismatch': 'context',
                        'warmup_mismatch': 'warmup',
                        'fractional_context': 'fractional-context',
                        'string_parallel': 'string-parallel',
                        'missing_warmup': 'missing-warmup',
                    }.get(mode)
                    write_parity_manifest(manifest_path, cases, mismatch)
                    if mode in ('artifact_mutation', 'manifest_mutation'):
                        Handler.mutate_path = str(manifest_path.parent / 'alpha-weights.gguf') if mode == 'artifact_mutation' else str(manifest_path)
                    escaped_manifest = str(manifest_path).replace("'", "''")
                    command += f" -ParityManifestPath '{escaped_manifest}'"
                if mode == 'order':
                    command += (" -SystemPrompt 'System rule stays identical.' "
                                "-DeveloperPrompt 'Developer rule stays identical.' "
                                "-Temperature 0.25 -TopP 0.9 -FrequencyPenalty 0.1 -PresencePenalty 0.2")
                args = [POWERSHELL, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command]
            else:
                args = [POWERSHELL, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script,
                        '-Cases', cases[0],
                        '-ModelIdentity', 'synthetic-fixture', '-Repetitions', repetitions, '-MaxTokens', '16',
                        '-TimeoutSeconds', '1', '-OutputDirectory', str(output)]
            if mode in ('assert_smoke', 'assert_single'):
                args += ['-AssertProvider', 'fixture']
            if mode == 'tolerance_too_high':
                args += ['-TolerancePercent', '100']
            if mode == 'assert_pair_no_gain_zero_tolerance':
                args += ['-TolerancePercent', '0']
            if mode == 'bootstrap_boundary':
                args += ['-StatisticsSelfTest']
            if mode == 'assert_single':
                manifest_path = output / 'parity-manifest.json'
                write_parity_manifest(manifest_path, cases)
                args += ['-ParityManifestPath', str(manifest_path)]
            result = subprocess.run(args, capture_output=True, text=True, timeout=15)
            if mode == 'bootstrap_boundary':
                assert result.returncode == 0, result.stdout + result.stderr
                assert 'PASS bootstrap threshold' in result.stdout, result.stdout
                assert not Handler.request_models, 'bootstrap self-test contacted a provider'
                print('PASS', mode)
                continue
            if mode == 'tolerance_too_high':
                assert result.returncode != 0, 'excessive tolerated regression unexpectedly accepted'
                assert 'TolerancePercent' in result.stderr + result.stdout
                assert not Handler.request_models, 'invalid tolerance contacted a provider'
                print('PASS', mode)
                continue
            if mode in ('assert_smoke', 'assert_single'):
                combined_output = result.stdout + result.stderr
                assert result.returncode != 0, f'{mode} unexpectedly passed the competitive gate'
                if mode == 'assert_smoke':
                    assert '20 repeated measurements' in combined_output, combined_output
                    print('PASS', mode)
                    continue
                assert 'at least two providers' in combined_output, combined_output
                assert not Handler.request_models, f'{mode} contacted a provider with no competitor'
                print('PASS', mode)
                continue
            if mode in ('parity_missing', 'parity_mismatch', 'artifact_hash_mismatch', 'runtime_mismatch', 'template_mismatch', 'context_mismatch', 'warmup_mismatch', 'fractional_context', 'string_parallel', 'missing_warmup'):
                combined_output = result.stdout + result.stderr
                assert result.returncode != 0, f'{mode} unexpectedly passed parity preflight'
                assert not Handler.request_models, f'{mode} contacted providers before parity preflight: {Handler.request_models}'
                if mode == 'parity_missing':
                    assert 'ParityManifestPath' in combined_output, combined_output
                elif mode == 'parity_mismatch':
                    assert "differs in 'artifactSha256'" in combined_output, combined_output
                elif mode == 'artifact_hash_mismatch':
                    assert 'does not match the file on disk' in combined_output, combined_output
                elif mode == 'runtime_mismatch':
                    assert "differs in 'runtimeSettingsSha256'" in combined_output, combined_output
                elif mode == 'template_mismatch':
                    assert "differs in 'chatTemplateSha256'" in combined_output, combined_output
                elif mode == 'context_mismatch':
                    assert "differs in 'contextTokens'" in combined_output, combined_output
                elif mode == 'warmup_mismatch':
                    assert "differs in 'warmupRequests'" in combined_output, combined_output
                elif mode in ('fractional_context', 'string_parallel'):
                    assert 'must be a JSON integer number' in combined_output, combined_output
                else:
                    assert 'warmupRequests' in combined_output and 'JSON integer number' in combined_output, combined_output
                print('PASS', mode)
                continue
            report_path = output / 'reference-stream-benchmark.json'
            assert report_path.exists(), (
                f'{mode} did not write a benchmark report; exit={result.returncode}\n'
                f'stdout:\n{result.stdout}\nstderr:\n{result.stderr}'
            )
            report = json.loads(report_path.read_text(encoding='utf-8-sig'))
            provider = report['providers'].get('fixture')
            markdown = (output / 'reference-stream-benchmark.md').read_text(encoding='utf-8-sig')
            assert report['schemaVersion'] == 5, report
            expected_latency_evidence = 'repeated-sample-size' if mode in ('assert_single', 'assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'artifact_mutation', 'manifest_mutation') else 'smoke-only'
            assert report['latencyEvidence'] == expected_latency_evidence, report
            assert report['providerSchedule']['strategy'] == 'paired-round-robin', report
            assert report['providerSchedule']['repeatedRoundStartRotates'] is True, report
            expected_parity_status = 'invalidated-during-run' if mode in ('artifact_mutation', 'manifest_mutation') else 'operator-attested' if mode in ('assert_single', 'assert_pair', 'assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance', 'late_fail', 'parity_smoke') else 'not-provided'
            assert report['parityEvidence']['status'] == expected_parity_status, report
            assert report['reasoningEffort'] == 'none', report
            assert 'End-to-end tok/s' in markdown, markdown
            assert 'Median last visible ms' in markdown and 'p95 last visible ms' in markdown, markdown
            if mode == 'assert_single':
                assert provider['medianLastVisibleMs'] is not None, provider
            elif mode == 'stall':
                assert not provider['reachable'] and 'deadline' in provider['error'], provider
            elif mode == 'order':
                expected_order = ['model-a', 'model-b', 'model-a', 'model-b',
                                  'model-b', 'model-a', 'model-a', 'model-b']
                assert Handler.request_models == expected_order, Handler.request_models
                assert report['providers']['alpha']['totalRuns'] == 3, report
                assert report['providers']['beta']['totalRuns'] == 3, report
                assert [run['repetition'] for run in report['providers']['alpha']['runs']] == [1, 2, 3], report
                assert [run['schedulePosition'] for run in report['providers']['alpha']['runs']] == [1, 2, 1], report
                assert report['requestProfile']['systemPrompt'] == 'System rule stays identical.'
                assert report['requestProfile']['developerPrompt'] == 'Developer rule stays identical.'
                assert report['requestProfile']['temperature'] == 0.25
                assert report['requestProfile']['topP'] == 0.9
                assert report['requestProfile']['frequencyPenalty'] == 0.1
                assert report['requestProfile']['presencePenalty'] == 0.2
                for payload in Handler.request_payloads:
                    assert payload['messages'] == [
                        {'role': 'system', 'content': 'System rule stays identical.'},
                        {'role': 'developer', 'content': 'Developer rule stays identical.'},
                        {'role': 'user', 'content': 'Explain how a hash table handles collisions, using exactly five concise sentences.'},
                    ], payload
                    assert payload['temperature'] == 0.25 and payload['top_p'] == 0.9, payload
                    assert payload['frequency_penalty'] == 0.1 and payload['presence_penalty'] == 0.2, payload
            elif mode == 'assert_pair':
                expected_order = ['model-a', 'model-b']
                for repetition in range(20):
                    expected_order.extend(['model-a', 'model-b'] if repetition % 2 == 0
                                           else ['model-b', 'model-a'])
                assert result.returncode == 0, result.stderr
                assert Handler.request_models == expected_order, Handler.request_models
                for name in ('alpha', 'beta'):
                    assert report['providers'][name]['totalRuns'] == 20, report
                    assert report['providers'][name]['allRunsComparable'], report
                assert report['parityEvidence']['speedAssertionEligible'] is True, report
                assert report['parityEvidence']['runtimeAssociationIndependentlyVerified'] is False, report
                assert 'cannot independently prove' in report['parityEvidence']['scope'], report
                assert report['speedAssertion']['result'] == 'pass', report['speedAssertion']
                assert report['speedAssertion']['comparisons'], report['speedAssertion']
                for metric in ('ttftMs', 'lastVisibleMs'):
                    evidence = report['speedAssertion']['comparisons'][0]['pairedMedianLatencyImprovement'][metric]
                    assert evidence['pairedSamples'] == 20 and evidence['bootstrapSamples'] == 4000, evidence
                    assert evidence['lower95'] > 2, evidence
            elif mode in ('assert_pair_no_gain', 'assert_pair_no_gain_zero_tolerance'):
                assert result.returncode != 0, 'equal-latency fixture must not assert a winner'
                assert report['speedAssertion']['result'] == 'fail', report['speedAssertion']
                assert any('95% bootstrap lower bound' in failure for failure in report['speedAssertion']['failures']), report['speedAssertion']
            elif mode == 'late_fail':
                assert result.returncode != 0, 'the competitive gate must reject a provider that failed mid-run'
                normalized_output = ' '.join((result.stdout + result.stderr).split())
                assert 'Competitor beta has no comparable result.' in normalized_output, normalized_output
                failed = report['providers']['beta']
                healthy = report['providers']['alpha']
                assert not failed['reachable'], failed
                assert failed['requestedRuns'] == 20 and failed['completedRuns'] == 1, failed
                assert len(failed['runs']) == failed['completedRuns'], failed
                assert healthy['reachable'] and healthy['completedRuns'] == 20, healthy
            elif mode == 'parity_smoke':
                assert result.returncode == 0, result.stderr
                assert report['parityEvidence']['speedAssertionEligible'] is False, report
                assert 'smoke-only' in report['parityEvidence']['reason'], report
                assert all(report['providers'][name]['reachable'] for name in ('alpha', 'beta')), report
            elif mode in ('artifact_mutation', 'manifest_mutation'):
                assert result.returncode != 0, 'post-run integrity mutation must fail the assertion'
                assert report['parityEvidence']['status'] == 'invalidated-during-run', report['parityEvidence']
                assert report['parityEvidence']['postRunIntegrityVerified'] is False, report['parityEvidence']
                assert report['speedAssertion']['result'] == 'fail', report['speedAssertion']
            else:
                assert result.returncode == 0, result.stderr
                assert provider['reachable'], provider
                assert provider['runs'][0]['output'] == 'Answer complete.'
                assert provider['runs'][0]['lastVisibleMs'] >= provider['runs'][0]['ttftMs'], provider
                assert provider['medianLastVisibleMs'] >= provider['medianTtftMs'], provider
                assert provider['p95LastVisibleMs'] >= provider['p95TtftMs'], provider
                assert provider['medianCompletionTokensPerSecondEndToEnd'] is not None, provider
                assert provider['exactTokenRateAvailableForAllRuns'] == (mode == 'plain'), provider
                if mode == 'reasoning':
                    assert provider['medianDecodeTokensPerSecond'] is None
            print('PASS', mode)

        Handler.mode = 'plain'
        with Handler.request_lock:
            Handler.request_models.clear()
            Handler.request_payloads.clear()
        output = pathlib.Path(directory) / 'local-ai-competitors'
        script = str(ROOT / 'scripts/benchmark-local-ai-competitors.ps1').replace("'", "''")
        providers = ', '.join("'" + f'{name}=http://127.0.0.1:{server.server_port}/{name}' + "'"
                              for name in ('alpha', 'beta'))
        model_overrides = "'alpha=model-a', 'beta=model-b'"
        escaped_output = str(output).replace("'", "''")
        command = (f"& '{script}' -Providers @({providers}) -ChatModels @({model_overrides}) "
                   f"-OutputDirectory '{escaped_output}' -Repetitions 3 -ChatMaxTokens 16")
        result = subprocess.run([POWERSHELL, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command],
                                capture_output=True, text=True, timeout=30)
        assert result.returncode == 0, result.stdout + result.stderr
        competitor_report = json.loads((output / 'competitor-benchmark.json').read_text(encoding='utf-8-sig'))
        expected_order = ['model-a', 'model-b', 'model-a', 'model-b',
                          'model-b', 'model-a', 'model-a', 'model-b']
        assert Handler.request_models == expected_order, Handler.request_models
        assert competitor_report['schemaVersion'] == 5, competitor_report
        assert 'no parity manifest supplied' in competitor_report['chatBenchmark']['modelWeightParity']
        for name in ('alpha', 'beta'):
            chat = competitor_report['providers'][name]['chat']
            assert chat['supported'] and chat['providerSchedule'] == 'paired-round-robin', chat
            assert chat['requestedRuns'] == chat['completedRuns'] == 3, chat
        for payload in Handler.request_payloads:
            assert payload['messages'] == [{'role': 'user', 'content': 'Explain what a hash table is in three sentences.'}], payload
            assert payload['temperature'] == 0 and payload['top_p'] == 1, payload
        print('PASS local_ai_competitor_pairing')

        with Handler.request_lock:
            Handler.request_models.clear()
            Handler.request_payloads.clear()
            Handler.request_counts.clear()
        parity_output = pathlib.Path(directory) / 'local-ai-competitors-parity'
        parity_manifest = parity_output / 'parity-manifest.json'
        parity_cases = [f'alpha=http://127.0.0.1:{server.server_port}|model-a',
                        f'beta=http://127.0.0.1:{server.server_port}|model-b']
        write_parity_manifest(parity_manifest, parity_cases)
        escaped_output = str(parity_output).replace("'", "''")
        escaped_manifest = str(parity_manifest).replace("'", "''")
        command = (f"& '{script}' -Providers @({providers}) -ChatModels @({model_overrides}) "
                   f"-OutputDirectory '{escaped_output}' -Repetitions 3 -ChatMaxTokens 16 "
                   f"-ChatParityManifestPath '{escaped_manifest}'")
        result = subprocess.run([POWERSHELL, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', command],
                                capture_output=True, text=True, timeout=30)
        assert result.returncode == 0, result.stdout + result.stderr
        parity_report = json.loads((parity_output / 'competitor-benchmark.json').read_text(encoding='utf-8-sig'))
        assert 'operator-attested manifest' in parity_report['chatBenchmark']['modelWeightParity'], parity_report
        assert parity_report['chatBenchmark']['parityManifestSource'].startswith('operator-attested'), parity_report
        for name in ('alpha', 'beta'):
            chat = parity_report['providers'][name]['chat']
            assert chat['parityEvidenceStatus'] == 'operator-attested', chat
            assert chat['speedAssertionEligible'] is False, chat
            assert chat['parityManifestSha256'], chat
        print('PASS local_ai_competitor_parity_manifest')
finally:
    server.shutdown()
    server.server_close()
