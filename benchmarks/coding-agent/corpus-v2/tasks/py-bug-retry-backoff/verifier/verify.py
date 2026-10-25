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
from retry import retry
sleeps = []
state = {'n': 0}
def flaky():
    state['n'] += 1
    if state['n'] < 4:
        raise RuntimeError('boom %d' % state['n'])
    return 'done'
check('succeeds', retry(flaky, 5, 1.0, 100.0, sleeps.append) == 'done')
check('exponential', sleeps == [1.0, 2.0, 4.0])
sleeps2 = []
def always():
    raise KeyError('k')
check('reraises last', raises(KeyError, retry, always, 4, 1.0, 3.0, sleeps2.append))
check('capped, no sleep after last', sleeps2 == [1.0, 2.0, 3.0])
sleeps3 = []
check('first try no sleep', retry(lambda: 7, 3, 1.0, 5.0, sleeps3.append) == 7 and sleeps3 == [])
done()
