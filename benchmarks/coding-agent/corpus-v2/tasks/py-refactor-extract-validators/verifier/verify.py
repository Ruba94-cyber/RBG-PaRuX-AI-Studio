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
import ast
import app, validators
check('email ok', validators.is_email('A@b.co') and not validators.is_email('a@b') and not validators.is_email('@b.co') and not validators.is_email('a@@b.co'))
check('port ok', validators.is_port(80) and not validators.is_port(0) and not validators.is_port(70000) and not validators.is_port(True) and not validators.is_port('80'))
check('app re-exports', app.is_email is validators.is_email and app.is_port is validators.is_port)
check('register ok', app.register('A@B.co', 8080) == {'ok': True, 'email': 'a@b.co', 'port': 8080})
check('register email err', app.register('nope', 80) == {'ok': False, 'error': 'email'})
check('register port err', app.register('a@b.co', 0) == {'ok': False, 'error': 'port'})
tree = ast.parse(open(os.path.join(ROOT, 'src', 'app.py'), encoding='utf-8').read())
defs = {n.name for n in tree.body if isinstance(n, ast.FunctionDef)}
check('app no longer defines validators', 'is_email' not in defs and 'is_port' not in defs)
vt = ast.parse(open(os.path.join(ROOT, 'src', 'validators.py'), encoding='utf-8').read())
vdefs = {n.name for n in vt.body if isinstance(n, ast.FunctionDef)}
check('validators defines them', {'is_email', 'is_port'} <= vdefs)
done()
