"""Shared helpers for the coding-agent corpus v2 generator.

Every task is a plain dict built with `task(...)`. Fixture, verifier and reference
solution are kept as three separate file maps and written to three separate
directories, so a runner can mount ONLY the fixture into the agent workspace.
"""

PY_PRE = '''import os, sys
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
sys.path.insert(0, os.path.join(ROOT, 'src'))
_failed = []
def check(name, cond):
    if not cond:
        _failed.append(name)
        print('FAIL: ' + name)
def raises(exc, fn, *a, **k):
    try:
        fn(*a, **k)
    except exc:
        return True
    except Exception:
        return False
    return False
def done():
    if _failed:
        print(str(len(_failed)) + ' check(s) failed')
        sys.exit(1)
    print('ok')
    sys.exit(0)
'''

JS_PRE = '''"use strict";
const assert = require("node:assert");
const path = require("node:path");
const SRC = path.join(__dirname, "..", "src");
const load = (n) => require(path.join(SRC, n));
let failed = 0;
function t(name, fn) { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
async function ta(name, fn) { try { await fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + e.message); } }
function done() { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
'''

TS_PRE = '''import assert from "node:assert";
let failed = 0;
function t(name: string, fn: () => void): void { try { fn(); } catch (e) { failed++; console.log("FAIL: " + name + " :: " + (e as Error).message); } }
function done(): void { if (failed) { console.log(failed + " failed"); process.exit(1); } console.log("ok"); }
'''

CS_PRE = '''static class T
{
    static int failed;
    public static void Run(string name, Func<bool> f)
    {
        try { if (!f()) { failed++; Console.WriteLine("FAIL: " + name); } }
        catch (Exception e) { failed++; Console.WriteLine("FAIL: " + name + " :: " + e.GetType().Name + " " + e.Message); }
    }
    public static int Done() { Console.WriteLine(failed == 0 ? "ok" : failed + " failed"); return failed == 0 ? 0 : 1; }
}
'''

CS_PROJ = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <InvariantGlobalization>true</InvariantGlobalization>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\\src\\**\\*.cs" Exclude="..\\src\\obj\\**;..\\src\\bin\\**" />
    <Compile Include="Verify.cs" />
  </ItemGroup>
</Project>
'''

CS_FIXTURE_PROJ = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
</Project>
'''

# Mutation-based verifier for test-writing tasks. The agent's tests must pass on the
# real source and fail on every hidden mutant. Task bodies set CFG before this runs.
MUT_VERIFY = '''import os, shutil, subprocess, sys, tempfile
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
'''


def py_verify(body):
    return {'verify.py': PY_PRE + body.strip('\n') + '\ndone()\n'}


def js_verify(body, wrap_async=False):
    b = body.strip('\n')
    if wrap_async:
        b = '(async () => {\n' + b + '\nvalue_done();\n})();\n'.replace('value_done', 'done')
    else:
        b += '\ndone();'
    return {'verify.js': JS_PRE + b + '\n'}


def ts_verify(body):
    return {'verify.ts': TS_PRE + body.strip('\n') + '\ndone();\n'}


def cs_verify(body):
    return {'Verify.csproj': CS_PROJ, 'Verify.cs': CS_PRE + body.strip('\n') + '\n'}


def mut_verify(cfg_literal):
    return {'verify.py': 'CFG = ' + cfg_literal.strip() + '\n' + MUT_VERIFY}


CMD = {
    'python': ['{python}', '-B', '.verify/verify.py'],
    'javascript': ['{node}', '.verify/verify.js'],
    'typescript': ['{node}', '.verify/verify.ts'],
    'csharp': ['{dotnet}', 'run', '--project', '.verify/Verify.csproj', '-c', 'Release', '--nologo', '-v', 'q'],
}
TOOLCHAINS = {
    'python': ['python'],
    'javascript': ['node'],
    'typescript': ['node'],
    'csharp': ['dotnet'],
}
PKG_MODULE = '{"name":"fixture","private":true,"type":"module"}\n'
PKG_CJS = '{"name":"fixture","private":true,"type":"commonjs"}\n'


def task(id, lang, kind, split, difficulty, goal, fixture, verifier, solution,
         scope, time_s=600, steps=40, mandatory=False, toolchains=None, cmd=None):
    """kind: bugfix | feature | refactor | testwriting; split: optimization | held-out."""
    assert split in ('optimization', 'held-out'), id
    assert kind in ('bugfix', 'feature', 'refactor', 'testwriting'), id
    return {
        'id': id, 'language': lang, 'kind': kind, 'split': split, 'difficulty': difficulty,
        'goal': goal, 'fixture': fixture, 'verifier': verifier, 'solution': solution,
        'scope': scope, 'timeBudgetSeconds': time_s, 'stepBudget': steps,
        'mandatory': mandatory,
        'toolchains': toolchains or TOOLCHAINS[lang],
        'verifyCommand': cmd or CMD[lang],
    }
