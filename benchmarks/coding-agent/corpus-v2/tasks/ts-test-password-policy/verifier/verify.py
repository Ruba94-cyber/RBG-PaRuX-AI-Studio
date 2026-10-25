CFG = {
 'tests': ['tests/policy.test.ts'],
 'cmd': ['NODE', '--test', 'tests/policy.test.ts'],
 'src': 'src/policy.ts',
 'mutants': [
   ('pw.length < 10', 'pw.length < 9'),
   ('pw.length < 10', 'pw.length <= 10'),
   ('pw.length > 64', 'pw.length > 65'),
   ('pw.length > 64', 'pw.length >= 64'),
   ('/[a-z]/', '/[a-y]/'),
   ('!/[A-Z]/.test(pw)', '!/[A-Z]/.test(pw.toLowerCase())'),
   ('/[0-9]/', '/[1-9]/'),
   ('username.length >= 3', 'username.length >= 4'),
   ('pw.toLowerCase().includes(username.toLowerCase())', 'pw.includes(username)'),
 ]}
import os, shutil, subprocess, sys, tempfile
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
NODE = shutil.which('node')
def expand(cmd):
    return [sys.executable if c == 'PY' else (NODE or 'node') if c == 'NODE' else c for c in cmd]
def run_in(base):
    env = dict(os.environ)
    env['PYTHONPATH'] = os.path.join(base, 'src')
    env['PYTHONDONTWRITEBYTECODE'] = '1'
    return subprocess.run(expand(CFG['cmd']), cwd=base, env=env, capture_output=True, text=True, timeout=120).returncode
def fail(msg):
    print('FAIL: ' + msg)
    sys.exit(1)
for rel in CFG['tests']:
    if not os.path.isfile(os.path.join(ROOT, rel)):
        fail('missing test file ' + rel)
def copy():
    tmp = tempfile.mkdtemp(prefix='mut-')
    dst = os.path.join(tmp, 'w')
    shutil.copytree(ROOT, dst, ignore=shutil.ignore_patterns('.verify', '__pycache__', '.git', 'node_modules'))
    return tmp, dst
tmp, dst = copy()
try:
    if run_in(dst) != 0:
        fail('tests do not pass on the unmodified source')
finally:
    shutil.rmtree(tmp, ignore_errors=True)
survivors = []
for i, (old, new) in enumerate(CFG['mutants']):
    tmp, dst = copy()
    try:
        p = os.path.join(dst, CFG['src'])
        text = open(p, encoding='utf-8').read()
        if old not in text:
            fail('verifier mutant ' + str(i) + ' does not apply (source changed?)')
        open(p, 'w', encoding='utf-8', newline='').write(text.replace(old, new, 1))
        if run_in(dst) == 0:
            survivors.append(i)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
if survivors:
    fail('tests did not detect mutant(s) ' + ','.join(map(str, survivors)))
print('ok')
