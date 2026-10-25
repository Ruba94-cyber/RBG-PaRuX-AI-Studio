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
from datetime import date
from daterange import days_inclusive
check('same', days_inclusive(date(2024, 3, 1), date(2024, 3, 1)) == 1)
check('week', days_inclusive(date(2024, 3, 1), date(2024, 3, 7)) == 7)
check('leap', days_inclusive(date(2024, 2, 28), date(2024, 3, 1)) == 3)
check('reversed', raises(ValueError, days_inclusive, date(2024, 3, 2), date(2024, 3, 1)))
done()
