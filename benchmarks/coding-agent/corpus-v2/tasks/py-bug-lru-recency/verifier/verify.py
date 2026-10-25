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
from lru import LRUCache
c = LRUCache(2)
c.put('a', 1); c.put('b', 2)
check('get hit', c.get('a') == 1)
c.put('c', 3)
check('b evicted after a was read', c.get('b') is None and c.get('a') == 1 and c.get('c') == 3)
d = LRUCache(2)
d.put('a', 1); d.put('b', 2); d.put('a', 10); d.put('c', 3)
check('overwrite refreshes', d.get('a') == 10 and d.get('b') is None)
check('len', len(d) == 2)
check('miss', d.get('zzz') is None)
check('capacity guard', raises(ValueError, LRUCache, 0))
done()
