import os, sys
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
from flags import parse_args
check('plain', parse_args(['a', 'b']) == ({}, ['a', 'b']))
check('bare flag', parse_args(['--verbose']) == ({'verbose': True}, []))
check('equals', parse_args(['--name=x']) == ({'name': 'x'}, []))
check('space value', parse_args(['--name', 'x', 'pos']) == ({'name': 'x'}, ['pos']))
check('repeat', parse_args(['--tag=a', '--tag=b', '--tag', 'c'])[0] == {'tag': ['a', 'b', 'c']})
check('double dash', parse_args(['--x', '--', '--y', 'z']) == ({'x': True}, ['--y', 'z']))
check('flag before flag', parse_args(['--a', '--b=1']) == ({'a': True, 'b': '1'}, []))
done()
