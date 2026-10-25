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
import ast, inspect
import pricing
check('basic', pricing.price_basic(10000) == 11400)
check('plus', pricing.price_plus(10000) == 10800)
check('pro', pricing.price_pro(10000) == 10370)
check('odd basic', pricing.price_basic(999) == 1139)
check('odd pro', pricing.price_pro(333) == 345)
check('zero', pricing.price_plus(0) == 0)
tree = ast.parse(open(os.path.join(ROOT, 'src', 'pricing.py'), encoding='utf-8').read())
funcs = {n.name: n for n in tree.body if isinstance(n, ast.FunctionDef)}
check('helper exists', '_apply_pricing' in funcs)
if '_apply_pricing' in funcs:
    h = [a.arg for a in funcs['_apply_pricing'].args.args]
    check('helper signature', h == ['base_cents', 'discount_percent', 'tax_percent'])
for name in ('price_basic', 'price_plus', 'price_pro'):
    f = funcs.get(name)
    calls = [n for n in ast.walk(f) if isinstance(n, ast.Call) and getattr(n.func, 'id', '') == '_apply_pricing'] if f else []
    check(name + ' delegates', len(calls) == 1)
    check(name + ' has no inline arithmetic', not any(isinstance(n, ast.BinOp) and isinstance(n.op, ast.FloorDiv) for n in ast.walk(f))) if f else None
done()
