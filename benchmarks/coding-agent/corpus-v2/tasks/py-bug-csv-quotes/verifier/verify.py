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
from csvline import split_csv_line
check('plain', split_csv_line('a,b,c') == ['a', 'b', 'c'])
check('comma in quotes', split_csv_line('a,"b,c",d') == ['a', 'b,c', 'd'])
check('escaped quote', split_csv_line('"he said ""hi""",x') == ['he said "hi"', 'x'])
check('empty fields', split_csv_line(',,') == ['', '', ''])
check('quoted empty', split_csv_line('"",a') == ['', 'a'])
done()
