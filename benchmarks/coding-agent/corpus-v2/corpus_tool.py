#!/usr/bin/env python3
"""PaRuX coding-agent corpus v2 tool.

Subcommands
  build        regenerate tasks/<id>/{fixture,verifier,reference} and ../parux-corpus-v2.json
  check        validate corpus integrity (no toolchains needed, no code executed)
  selftest     prove fail-before / pass-after for every task on this machine
  materialize  copy ONE task fixture into a fresh workspace (never reference/verifier)
  verify       run the hidden verifier against a workspace (scope audit + verifier exit code)

The reference solutions and hidden verifiers are never copied into an agent workspace.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
TASKS_DIR = os.path.join(HERE, 'tasks')
MANIFEST = os.path.join(os.path.dirname(HERE), 'parux-corpus-v2.json')
SCHEMA_VERSION = 5
CORPUS_VERSION = '2.0.0'
IGNORED_DIRS = {'.git', '.verify', '__pycache__', 'bin', 'obj', 'node_modules'}
LANGS = {'python', 'javascript', 'typescript', 'csharp'}
KINDS = {'bugfix', 'feature', 'refactor', 'testwriting'}
ID_RE = re.compile(r'^[a-z][a-z0-9-]{2,60}$')
ENV_EXTRA = {
    'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
    'PYTHONDONTWRITEBYTECODE': '1', 'NODE_NO_WARNINGS': '1',
}


# ---------------------------------------------------------------- hashing / fs
def sha256_bytes(b):
    return hashlib.sha256(b).hexdigest()


def list_files(root):
    out = []
    for base, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in IGNORED_DIRS)
        for f in sorted(files):
            full = os.path.join(base, f)
            out.append(os.path.relpath(full, root).replace(os.sep, '/'))
    return sorted(out)


def read_bytes(p):
    with open(p, 'rb') as fh:
        return fh.read()


def tree_hashes(root):
    # CRLF is normalized so a Windows autocrlf checkout hashes identically to the LF-generated tree
    return {rel: sha256_bytes(read_bytes(os.path.join(root, rel)).replace(b'\r\n', b'\n')) for rel in list_files(root)}


def tree_hash(root):
    h = hashlib.sha256()
    for rel, fh in sorted(tree_hashes(root).items()):
        h.update(rel.encode('utf-8') + b'\0' + fh.encode('ascii') + b'\n')
    return h.hexdigest()


def write_tree(root, files):
    if os.path.isdir(root):
        shutil.rmtree(root)
    os.makedirs(root, exist_ok=True)
    for rel, content in files.items():
        p = os.path.join(root, *rel.split('/'))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, 'w', encoding='utf-8', newline='\n') as fh:
            fh.write(content)


def copy_tree(src, dst):
    os.makedirs(dst, exist_ok=True)
    for rel in list_files(src):
        d = os.path.join(dst, *rel.split('/'))
        os.makedirs(os.path.dirname(d), exist_ok=True)
        shutil.copyfile(os.path.join(src, *rel.split('/')), d)


# ---------------------------------------------------------------- manifest
def load_manifest(path=None):
    with open(path or MANIFEST, encoding='utf-8') as fh:
        return json.load(fh)


def task_dir(task, part):
    return os.path.join(TASKS_DIR, task['id'], part)


def resolve_toolchains(task):
    found, missing = {}, []
    for tc in task['toolchains']:
        exe = sys.executable if tc == 'python' else shutil.which(tc)
        if exe:
            found[tc] = exe
        else:
            missing.append(tc)
    return found, missing


def expand_command(task, found):
    out = []
    for part in task['verifyCommand']:
        m = re.fullmatch(r'\{(\w+)\}', part)
        out.append(found[m.group(1)] if m and m.group(1) in found else part)
    return out


# ---------------------------------------------------------------- build
def cmd_build(_args):
    sys.path.insert(0, os.path.join(HERE, 'generator'))
    import tasks_python, tasks_js, tasks_ts, tasks_cs
    all_tasks = tasks_python.TASKS + tasks_js.TASKS + tasks_ts.TASKS + tasks_cs.TASKS
    if os.path.isdir(TASKS_DIR):
        shutil.rmtree(TASKS_DIR)
    entries = []
    for t in all_tasks:
        write_tree(task_dir(t, 'fixture'), t['fixture'])
        write_tree(task_dir(t, 'verifier'), t['verifier'])
        write_tree(task_dir(t, 'reference'), t['solution'])
        entries.append({
            'id': t['id'], 'language': t['language'], 'kind': t['kind'], 'split': t['split'],
            'difficulty': t['difficulty'], 'goal': t['goal'], 'toolchains': t['toolchains'],
            'verifyCommand': t['verifyCommand'],
            'budgets': {'timeSeconds': t['timeBudgetSeconds'], 'steps': t['stepBudget']},
            'allowedChangedPathPrefixes': t['scope'],
            'expectedBaseline': {'verifierFailsOnFixture': True, 'verifierPassesWithReference': True},
            'fixtureDir': 'corpus-v2/tasks/%s/fixture' % t['id'],
            'verifierDir': 'corpus-v2/tasks/%s/verifier' % t['id'],
            'referenceDir': 'corpus-v2/tasks/%s/reference' % t['id'],
            'fixtureSha256': tree_hash(task_dir(t, 'fixture')),
            'verifierSha256': tree_hash(task_dir(t, 'verifier')),
            'referenceSha256': tree_hash(task_dir(t, 'reference')),
        })
    held = [e['id'] for e in entries if e['split'] == 'held-out']
    opt = [e['id'] for e in entries if e['split'] == 'optimization']
    mandatory = [t['id'] for t in all_tasks if t['mandatory']]
    doc = {
        'schemaVersion': SCHEMA_VERSION,
        'corpusVersion': CORPUS_VERSION,
        'name': 'PaRuX coding-agent self-contained corpus v2 (held-out + optimization)',
        'description': 'Small self-contained tasks (bug fix, feature, refactor, test writing) in C#, Python, TypeScript and JavaScript. Each task has a generated fixture, an independent hidden verifier and a separately stored reference solution. Regenerate with corpus-v2/corpus_tool.py build; validate with check; prove fail-before/pass-after with selftest. Supersedes parux-corpus.json (schemaVersion 3 smoke tasks, kept for orchestrator smoke runs) for any comparative claim.',
        'evaluationPolicy': {
            'absoluteSuccessFloorPercent': 70,
            'claimSplit': 'held-out',
            'mandatoryCaseIds': mandatory,
            'splitCounts': {'held-out': len(held), 'optimization': len(opt)},
            'minimumHeldOutTasks': 15,
            'tuningRule': 'Prompts, tool policies, routing and model choices may be tuned ONLY against tasks with split=optimization. Tasks with split=held-out must never be read, run for tuning, or used to select configurations; any held-out run used for tuning permanently invalidates that corpusVersion for claims. A new held-out set requires a new corpusVersion.',
            'budgetRule': 'A task counts as solved only if the hidden verifier passes AND wall time <= budgets.timeSeconds AND, when the provider reports steps, steps <= budgets.steps. Providers that cannot report steps are recorded with stepsEnforced=false and cannot produce a WIN.',
            'verifierIsolation': 'The verifier is copied into the workspace only after the agent stops, from a hash-checked directory; edits to the fixture outside allowedChangedPathPrefixes fail the task.',
        },
        'tasks': entries,
    }
    with open(MANIFEST, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(doc, fh, indent=2)
        fh.write('\n')
    print('built %d tasks (%d held-out, %d optimization) -> %s' % (len(entries), len(held), len(opt), MANIFEST))
    return 0


# ---------------------------------------------------------------- check
def distinctive_lines(text, baseline_text=''):
    base = {l.strip() for l in baseline_text.splitlines()}
    boiler = ('<', 'import ', 'from ', 'using ', '//', '#', '"use strict"')
    return {l.strip() for l in text.splitlines()
            if len(l.strip()) >= 25 and l.strip() not in base and not l.strip().startswith(boiler)}


def cmd_check(_args):
    errors = []
    err = errors.append
    doc = load_manifest()
    if doc.get('schemaVersion') != SCHEMA_VERSION:
        err('schemaVersion must be %d' % SCHEMA_VERSION)
    if not re.fullmatch(r'\d+\.\d+\.\d+', str(doc.get('corpusVersion', ''))):
        err('corpusVersion must be semver')
    tasks = doc.get('tasks', [])
    pol = doc.get('evaluationPolicy', {})
    if not 30 <= len(tasks) <= 50:
        err('task count %d outside 30..50' % len(tasks))
    ids = [t.get('id') for t in tasks]
    if len(set(ids)) != len(ids):
        err('duplicate task ids')
    langs = {t.get('language') for t in tasks}
    if not {'python', 'csharp'} <= langs or not ({'typescript', 'javascript'} & langs):
        err('corpus must cover C#, Python and TypeScript/JavaScript')
    if {t.get('kind') for t in tasks} != KINDS:
        err('corpus must contain all kinds: ' + ','.join(sorted(KINDS)))
    counts = {'held-out': 0, 'optimization': 0}
    for t in tasks:
        tid = t.get('id', '?')
        if not ID_RE.match(str(tid)):
            err('%s: invalid id' % tid)
        if t.get('language') not in LANGS:
            err('%s: unknown language' % tid)
        if t.get('kind') not in KINDS:
            err('%s: unknown kind' % tid)
        if t.get('split') not in counts:
            err('%s: split must be held-out or optimization' % tid)
        else:
            counts[t['split']] += 1
        if not str(t.get('goal', '')).strip() or len(t['goal']) < 40:
            err('%s: goal missing/too short' % tid)
        cmdv = t.get('verifyCommand')
        if not isinstance(cmdv, list) or not cmdv or not all(isinstance(x, str) and x for x in cmdv):
            err('%s: verifyCommand must be a non-empty argv list' % tid)
        elif not any('.verify' in x for x in cmdv):
            err('%s: verifyCommand must run the hidden verifier under .verify/' % tid)
        b = t.get('budgets', {})
        for k in ('timeSeconds', 'steps'):
            if not isinstance(b.get(k), int) or b[k] < 1:
                err('%s: budgets.%s must be a positive integer' % (tid, k))
        if not t.get('allowedChangedPathPrefixes'):
            err('%s: allowedChangedPathPrefixes missing' % tid)
        if t.get('expectedBaseline', {}).get('verifierFailsOnFixture') is not True:
            err('%s: expectedBaseline.verifierFailsOnFixture must be true' % tid)
        dirs = {p: task_dir({'id': tid}, p) for p in ('fixture', 'verifier', 'reference')}
        missing = [p for p, d in dirs.items() if not os.path.isdir(d) or not list_files(d)]
        if missing:
            err('%s: missing/empty dir(s): %s' % (tid, ','.join(missing)))
            continue
        for part, key in (('fixture', 'fixtureSha256'), ('verifier', 'verifierSha256'), ('reference', 'referenceSha256')):
            if tree_hash(dirs[part]) != t.get(key):
                err('%s: %s mismatch (files changed without rebuilding manifest)' % (tid, part))
        fx, vf, rf = tree_hashes(dirs['fixture']), tree_hashes(dirs['verifier']), tree_hashes(dirs['reference'])
        if any(p.startswith('.verify') or os.path.basename(p).lower().startswith('verify') for p in fx):
            err('%s: fixture contains verifier-looking file' % tid)
        if any(p in fx and fx[p] == vf[p] for p in vf):
            err('%s: verifier file duplicated in fixture' % tid)
        if not rf:
            err('%s: empty reference' % tid)
        changed = [p for p in rf if fx.get(p) != rf[p]]
        if not changed:
            err('%s: reference does not change the fixture' % tid)
        for p in rf:
            if not any(p.startswith(pref) for pref in t.get('allowedChangedPathPrefixes', [])):
                err('%s: reference file %s outside allowedChangedPathPrefixes' % (tid, p))
        # leak checks: fixture must not already contain the solution
        fixture_text = {p: read_bytes(os.path.join(dirs['fixture'], *p.split('/'))).decode('utf-8', 'replace') for p in fx}
        all_fixture = '\n'.join(fixture_text.values())
        # files the reference rewrites may legitimately lose lines (moves); only unrelated files count as leak sources
        unrelated_fixture = '\n'.join(v for k, v in fixture_text.items() if k not in rf)
        for p in rf:
            ref_text = read_bytes(os.path.join(dirs['reference'], *p.split('/'))).decode('utf-8', 'replace')
            if p in fx and fx[p] == rf[p]:
                err('%s: reference file %s is identical to fixture' % (tid, p))
            if p in fx:
                for line in distinctive_lines(ref_text, fixture_text[p]):
                    if line in unrelated_fixture:
                        err('%s: solution line leaked into fixture: %r' % (tid, line[:60]))
                        break
            else:
                for line in distinctive_lines(ref_text):
                    if line in unrelated_fixture:
                        err('%s: solution line leaked into fixture: %r' % (tid, line[:60]))
                        break
        for p in vf:
            vtext = read_bytes(os.path.join(dirs['verifier'], *p.split('/'))).decode('utf-8', 'replace')
            for line in distinctive_lines(vtext):
                if line in all_fixture:
                    err('%s: verifier line leaked into fixture: %r' % (tid, line[:60]))
                    break
        if re.search(r'(?i)reference|solution|\.verify|hidden', all_fixture):
            err('%s: fixture mentions reference/solution/verifier' % tid)
    if counts != pol.get('splitCounts'):
        err('splitCounts policy %r != actual %r' % (pol.get('splitCounts'), counts))
    if counts['held-out'] < int(pol.get('minimumHeldOutTasks', 15)) or counts['optimization'] < 10:
        err('too few tasks per split: %r' % counts)
    floor = pol.get('absoluteSuccessFloorPercent')
    if not isinstance(floor, (int, float)) or not 0 < floor <= 100:
        err('absoluteSuccessFloorPercent must be in (0,100]')
    by_id = {t['id']: t for t in tasks if 'id' in t}
    for mid in pol.get('mandatoryCaseIds', []):
        if mid not in by_id:
            err('mandatory id %s missing' % mid)
        elif by_id[mid].get('split') != 'held-out':
            err('mandatory id %s must be held-out' % mid)
    if not pol.get('mandatoryCaseIds'):
        err('mandatoryCaseIds must not be empty')
    if not pol.get('tuningRule'):
        err('tuningRule missing')
    for e in errors:
        print('ERROR: ' + e)
    print('corpus check: %s (%d tasks: %s)' % ('FAIL' if errors else 'PASS', len(tasks), json.dumps(counts)))
    return 1 if errors else 0


# ---------------------------------------------------------------- run verifier
def run_verifier(task, workdir, timeout=None):
    """Scope audit + hidden verifier. Returns dict; never trusts the agent."""
    timeout = timeout or max(60, int(task['budgets']['timeSeconds']))
    fx = tree_hashes(os.path.join(TASKS_DIR, task['id'], 'fixture'))
    now = tree_hashes(workdir)
    changed = sorted({p for p in set(fx) | set(now) if fx.get(p) != now.get(p)})
    prefixes = task['allowedChangedPathPrefixes']
    out_of_scope = [p for p in changed if not any(p.startswith(x) for x in prefixes)]
    vdir = os.path.join(workdir, '.verify')
    if os.path.isdir(vdir):
        shutil.rmtree(vdir, ignore_errors=True)
    copy_tree(os.path.join(TASKS_DIR, task['id'], 'verifier'), vdir)
    found, missing = resolve_toolchains(task)
    result = {'changedPaths': changed, 'outOfScopePaths': out_of_scope, 'verifierExitCode': None,
              'verifierTimedOut': False, 'verifierWallMs': 0, 'outputTail': '', 'toolchainMissing': missing}
    if missing:
        result['status'] = 'SKIPPED_TOOLCHAIN'
        return result
    env = dict(os.environ)
    env.update(ENV_EXTRA)
    start = time.time()
    try:
        p = subprocess.run(expand_command(task, found), cwd=workdir, env=env, capture_output=True, text=True,
                           timeout=timeout, errors='replace')
        result['verifierExitCode'] = p.returncode
        result['outputTail'] = (p.stdout + '\n' + p.stderr)[-1500:]
    except subprocess.TimeoutExpired:
        result['verifierTimedOut'] = True
        result['verifierExitCode'] = 124
    result['verifierWallMs'] = int((time.time() - start) * 1000)
    shutil.rmtree(vdir, ignore_errors=True)
    passed = result['verifierExitCode'] == 0 and not result['verifierTimedOut'] and not out_of_scope
    result['status'] = 'PASS' if passed else 'FAIL'
    return result


def find_task(doc, tid):
    for t in doc['tasks']:
        if t['id'] == tid:
            return t
    raise SystemExit('unknown task id: ' + tid)


def cmd_materialize(args):
    doc = load_manifest()
    t = find_task(doc, args.task)
    dest = os.path.abspath(args.dest)
    if os.path.exists(dest) and os.listdir(dest):
        raise SystemExit('destination must be empty: ' + dest)
    copy_tree(os.path.join(TASKS_DIR, t['id'], 'fixture'), dest)
    prompt = os.path.join(dest, '..', os.path.basename(dest) + '.prompt.txt')
    with open(os.path.abspath(prompt), 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(t['goal'] + '\n')
    print(json.dumps({'task': t['id'], 'workspace': dest, 'promptFile': os.path.abspath(prompt),
                      'timeBudgetSeconds': t['budgets']['timeSeconds'], 'stepBudget': t['budgets']['steps']}))
    return 0


def cmd_verify(args):
    doc = load_manifest()
    t = find_task(doc, args.task)
    res = run_verifier(t, os.path.abspath(args.workdir))
    print(json.dumps(res))
    return 0


# ---------------------------------------------------------------- selftest
def selftest_one(t):
    entry = {'id': t['id'], 'language': t['language'], 'split': t['split'], 'status': None, 'reason': ''}
    found, missing = resolve_toolchains(t)
    if missing:
        entry.update(status='SKIPPED', reason='missing toolchain: ' + ','.join(missing))
        return entry
    tmp = tempfile.mkdtemp(prefix='corpus-st-')
    try:
        work = os.path.join(tmp, 'w')
        copy_tree(os.path.join(TASKS_DIR, t['id'], 'fixture'), work)
        before = run_verifier(t, work)
        entry['failBefore'] = {'exit': before['verifierExitCode'], 'tail': before['outputTail'][-300:]}
        if before['status'] == 'PASS':
            entry.update(status='BROKEN', reason='verifier PASSES on unfixed fixture')
            return entry
        copy_tree(os.path.join(TASKS_DIR, t['id'], 'reference'), work)
        after = run_verifier(t, work)
        entry['passAfter'] = {'exit': after['verifierExitCode'], 'outOfScope': after['outOfScopePaths'], 'tail': after['outputTail'][-300:]}
        if after['status'] != 'PASS':
            entry.update(status='BROKEN', reason='reference solution does not pass verifier')
            return entry
        entry['status'] = 'VERIFIED'
        return entry
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def cmd_selftest(args):
    doc = load_manifest()
    tasks = doc['tasks']
    if args.only:
        tasks = [t for t in tasks if t['id'] in args.only]
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as ex:
        results = list(ex.map(selftest_one, tasks))
    summary = {s: sum(1 for r in results if r['status'] == s) for s in ('VERIFIED', 'SKIPPED', 'BROKEN')}
    report = {'corpusVersion': doc['corpusVersion'], 'summary': summary, 'results': results}
    if args.report:
        with open(args.report, 'w', encoding='utf-8', newline='\n') as fh:
            json.dump(report, fh, indent=2)
    for r in results:
        print('%-9s %-34s %s' % (r['status'], r['id'], r['reason']))
        if r['status'] == 'BROKEN':
            print('    evidence: ' + json.dumps(r.get('passAfter') or r.get('failBefore'))[:700])
    print('selftest: verified=%d skipped=%d broken=%d of %d' % (summary['VERIFIED'], summary['SKIPPED'], summary['BROKEN'], len(results)))
    return 1 if summary['BROKEN'] else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    sub.add_parser('build')
    sub.add_parser('check')
    st = sub.add_parser('selftest')
    st.add_argument('--report')
    st.add_argument('--workers', type=int, default=4)
    st.add_argument('--only', nargs='*')
    m = sub.add_parser('materialize')
    m.add_argument('--task', required=True)
    m.add_argument('--dest', required=True)
    v = sub.add_parser('verify')
    v.add_argument('--task', required=True)
    v.add_argument('--workdir', required=True)
    args = ap.parse_args()
    return {'build': cmd_build, 'check': cmd_check, 'selftest': cmd_selftest,
            'materialize': cmd_materialize, 'verify': cmd_verify}[args.cmd](args)


if __name__ == '__main__':
    sys.exit(main())
