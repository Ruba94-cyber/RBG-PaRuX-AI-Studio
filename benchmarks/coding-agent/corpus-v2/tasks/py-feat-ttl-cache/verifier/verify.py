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
from ttlcache import SimpleCache
now = [100.0]
c = SimpleCache(clock=lambda: now[0])
c.put('a', 1)
c.put('b', 2, ttl=10)
check('fresh', c.get('a') == 1 and c.get('b') == 2)
now[0] = 109.9
check('not yet expired', c.get('b') == 2)
now[0] = 110.0
check('expired at deadline', c.get('b', 'gone') == 'gone')
check('no ttl never expires', c.get('a') == 1)
check('expired entry dropped', len(c) == 1)
c.put('b', 3, ttl=1)
now[0] = 200.0
check('re-put respects new ttl', c.get('b') is None)
d = SimpleCache()
d.put('x', 1, ttl=60)
check('default clock works', d.get('x') == 1)
done()
