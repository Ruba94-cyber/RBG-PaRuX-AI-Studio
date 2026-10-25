"""Live native PaRuX API smoke: cold concurrent routing, cancellation and recovery.

Uses the same /api/v1/chat/completions transport as the desktop. Never logs the token.
Unloads the current PaRuX model to exercise actual simultaneous preparation.
"""
import concurrent.futures
import ipaddress
import json
import os
from pathlib import Path
import sys
import time
import urllib.request
from urllib.parse import urlsplit

boot = json.loads((Path(os.environ['LOCALAPPDATA']) / 'PaRuX/server/bootstrap.json').read_text(encoding='utf-8-sig'))
endpoint = boot['endpoint'].rstrip('/')
parsed_endpoint = urlsplit(endpoint)
host = parsed_endpoint.hostname or ''
try:
    local_host = host.lower() == 'localhost' or ipaddress.ip_address(host).is_loopback
except ValueError:
    local_host = False
if parsed_endpoint.scheme not in ('http', 'https') or not local_host or parsed_endpoint.username or parsed_endpoint.password:
    raise ValueError('PaRuX bootstrap must contain a local loopback HTTP(S) endpoint without userinfo.')
headers = {'X-PaRuX-Token': boot['token'], 'Content-Type': 'application/json'}


class NoRedirectHandler(urllib.request.HTTPRedirectHandler):
    """The bootstrap token is valid only for its exact PaRuX endpoint."""
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


# Bootstrap auth is local-only. Ignore proxy environment variables so a configured
# HTTP_PROXY cannot receive the bearer token intended for the loopback server.
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirectHandler())


def run(prompt, cancel=False):
    payload = {'model': 'parux-auto:balanced', 'stream': True, 'preferLowLatency': True,
               'messages': [{'role': 'user', 'content': prompt}]}
    req = urllib.request.Request(endpoint + '/api/v1/chat/completions',
                                 json.dumps(payload).encode(), headers)
    start = time.monotonic()
    result = {'prompt': prompt, 'output': '', 'ttftMs': None, 'done': False, 'cancelled': False}
    try:
        with opener.open(req, timeout=90) as response:
            for line in response:
                if time.monotonic() - start > 90:
                    raise TimeoutError('Total native chat deadline exceeded')
                if not line.startswith(b'data:'):
                    continue
                data = line[5:].strip()
                if data == b'[DONE]':
                    result['done'] = True
                    break
                chunk = json.loads(data)
                if chunk.get('parux_event') == 'model_routed':
                    result['routing'] = chunk.get('modelFileName', chunk.get('model', 'event-observed'))
                choices = chunk.get('choices') or []
                content = choices[0].get('delta', {}).get('content') if choices else None
                if content:
                    if result['ttftMs'] is None:
                        result['ttftMs'] = round((time.monotonic()-start)*1000, 2)
                    result['output'] += content
                    if cancel:
                        result['cancelled'] = True
                        break
        result['success'] = bool(result['output']) and (result['done'] or result['cancelled'])
    except Exception as exc:
        result['success'] = False
        result['error'] = str(exc)
    result['totalMs'] = round((time.monotonic()-start)*1000, 2)
    return result


with opener.open(urllib.request.Request(endpoint + '/api/v1/models/library/unload',
                                        b'', headers), timeout=15) as response:
    assert json.load(response)['success']
prompts = ['Rispondi solo con il numero: quanto fa diciassette moltiplicato per ventitre?',
           'Traduci in italiano, solo la traduzione: The red house.']
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    concurrent_runs = list(pool.map(run, prompts))
cancelled = run('Scrivi un racconto lungo mille parole su un esploratore della Luna.', cancel=True)
recovery = run('Rispondi con una sola parola in italiano: il contrario di freddo.')
report = {'transport': '/api/v1/chat/completions', 'concurrentColdRequests': concurrent_runs,
          'cancelAfterFirstContent': cancelled, 'recovery': recovery,
          'qualityChecks': {'arithmetic': concurrent_runs[0]['output'].strip().rstrip('.') == '391',
                            'translation': 'casa rossa' in concurrent_runs[1]['output'].lower(),
                            'recovery': recovery['output'].strip().lower().rstrip('.') == 'caldo'}}
out = Path(sys.argv[1]) if len(sys.argv)>1 else Path('native-chat-concurrency.json')
out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, indent=2))
if not all(x['success'] for x in concurrent_runs + [cancelled, recovery]):
    sys.exit(1)
if not all(report['qualityChecks'].values()):
    sys.exit(2)
