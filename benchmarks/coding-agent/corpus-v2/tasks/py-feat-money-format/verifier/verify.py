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
from money import format_money
check('small', format_money(500) == 'USD 5.00')
check('thousands', format_money(123450) == 'USD 1,234.50')
check('millions', format_money(123456789) == 'USD 1,234,567.89')
check('negative', format_money(-123450) == '(USD 1,234.50)')
check('negative small', format_money(-5) == '(USD 0.05)')
check('zero', format_money(0) == 'USD 0.00')
check('eur', format_money(100000, 'EUR') == 'EUR 1,000.00')
done()
