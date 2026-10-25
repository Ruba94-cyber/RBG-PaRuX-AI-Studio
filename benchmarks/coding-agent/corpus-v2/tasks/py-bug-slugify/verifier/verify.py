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
from slugify import slugify
check('basic', slugify('Hello World') == 'hello-world')
check('runs', slugify('a  --  b') == 'a-b')
check('edges', slugify('  !!Hi!! ') == 'hi')
check('digits', slugify('Route 66, again') == 'route-66-again')
check('empty', slugify('***') == '')
done()
