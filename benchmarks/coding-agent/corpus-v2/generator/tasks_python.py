from common import task, py_verify, mut_verify

S = ['src/']
TASKS = []
A = TASKS.append

# ---- 1 slugify (bugfix, optimization)
A(task('py-bug-slugify', 'python', 'bugfix', 'optimization', 'easy',
 'The function slugify in src/slugify.py must lowercase text, replace every run of non-alphanumeric ASCII characters with a single hyphen, and never return a leading or trailing hyphen. Fix it without changing its signature.',
 {'src/slugify.py': '''import re


def slugify(text):
    out = re.sub(r"[^a-z0-9]", "-", text.lower())
    return out
'''},
 py_verify('''
from slugify import slugify
check('basic', slugify('Hello World') == 'hello-world')
check('runs', slugify('a  --  b') == 'a-b')
check('edges', slugify('  !!Hi!! ') == 'hi')
check('digits', slugify('Route 66, again') == 'route-66-again')
check('empty', slugify('***') == '')
'''),
 {'src/slugify.py': '''import re


def slugify(text):
    out = re.sub(r"[^a-z0-9]+", "-", text.lower())
    return out.strip("-")
'''}, S, 300, 25))

# ---- 2 inclusive day count (bugfix, held-out)
A(task('py-bug-days-inclusive', 'python', 'bugfix', 'held-out', 'easy',
 'days_inclusive(start, end) in src/daterange.py takes two datetime.date values and must return how many calendar days the range covers, counting both endpoints (same day -> 1). If end is before start it must raise ValueError. Fix the function.',
 {'src/daterange.py': '''from datetime import date


def days_inclusive(start, end):
    return (end - start).days
'''},
 py_verify('''
from datetime import date
from daterange import days_inclusive
check('same', days_inclusive(date(2024, 3, 1), date(2024, 3, 1)) == 1)
check('week', days_inclusive(date(2024, 3, 1), date(2024, 3, 7)) == 7)
check('leap', days_inclusive(date(2024, 2, 28), date(2024, 3, 1)) == 3)
check('reversed', raises(ValueError, days_inclusive, date(2024, 3, 2), date(2024, 3, 1)))
'''),
 {'src/daterange.py': '''from datetime import date


def days_inclusive(start, end):
    if end < start:
        raise ValueError("end before start")
    return (end - start).days + 1
'''}, S, 300, 25))

# ---- 3 LRU (bugfix, held-out, mandatory)
A(task('py-bug-lru-recency', 'python', 'bugfix', 'held-out', 'medium',
 'LRUCache in src/lru.py must evict the least recently USED entry when capacity is exceeded. Reads via get() and overwrites via put() both count as use. Right now it behaves like a FIFO cache. Fix it and keep the public API (get returns None for a miss).',
 {'src/lru.py': '''class LRUCache:
    def __init__(self, capacity):
        if capacity < 1:
            raise ValueError("capacity")
        self.capacity = capacity
        self._data = {}

    def get(self, key):
        return self._data.get(key)

    def put(self, key, value):
        if key not in self._data and len(self._data) >= self.capacity:
            oldest = next(iter(self._data))
            del self._data[oldest]
        self._data[key] = value

    def __len__(self):
        return len(self._data)
'''},
 py_verify('''
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
'''),
 {'src/lru.py': '''class LRUCache:
    def __init__(self, capacity):
        if capacity < 1:
            raise ValueError("capacity")
        self.capacity = capacity
        self._data = {}

    def get(self, key):
        if key not in self._data:
            return None
        value = self._data.pop(key)
        self._data[key] = value
        return value

    def put(self, key, value):
        if key in self._data:
            del self._data[key]
        elif len(self._data) >= self.capacity:
            oldest = next(iter(self._data))
            del self._data[oldest]
        self._data[key] = value

    def __len__(self):
        return len(self._data)
'''}, S, 420, 30, mandatory=True))

# ---- 4 CSV quotes (bugfix, optimization)
A(task('py-bug-csv-quotes', 'python', 'bugfix', 'optimization', 'medium',
 'split_csv_line in src/csvline.py splits one CSV line into fields. It must support double-quoted fields containing commas and doubled quotes ("" means a literal quote inside a quoted field). Right now quoted fields containing escaped quotes are corrupted. Fix it.',
 {'src/csvline.py': '''def split_csv_line(line):
    fields = []
    cur = []
    in_quotes = False
    for ch in line:
        if ch == '"':
            in_quotes = not in_quotes
        elif ch == "," and not in_quotes:
            fields.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    fields.append("".join(cur))
    return fields
'''},
 py_verify('''
from csvline import split_csv_line
check('plain', split_csv_line('a,b,c') == ['a', 'b', 'c'])
check('comma in quotes', split_csv_line('a,"b,c",d') == ['a', 'b,c', 'd'])
check('escaped quote', split_csv_line('"he said ""hi""",x') == ['he said "hi"', 'x'])
check('empty fields', split_csv_line(',,') == ['', '', ''])
check('quoted empty', split_csv_line('"",a') == ['', 'a'])
'''),
 {'src/csvline.py': '''def split_csv_line(line):
    fields = []
    cur = []
    in_quotes = False
    i = 0
    while i < len(line):
        ch = line[i]
        if ch == '"':
            if in_quotes and i + 1 < len(line) and line[i + 1] == '"':
                cur.append('"')
                i += 1
            else:
                in_quotes = not in_quotes
        elif ch == "," and not in_quotes:
            fields.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
        i += 1
    fields.append("".join(cur))
    return fields
'''}, S, 420, 30))

# ---- 5 TTL cache (feature, held-out)
A(task('py-feat-ttl-cache', 'python', 'feature', 'held-out', 'medium',
 'Add time-to-live support to SimpleCache in src/ttlcache.py. The constructor must accept an optional clock callable (default time.monotonic). put(key, value, ttl=None) stores a value that expires ttl seconds later (ttl None = never expires); get(key, default=None) must not return expired entries and should drop them. Existing behaviour without ttl must be unchanged.',
 {'src/ttlcache.py': '''import time


class SimpleCache:
    def __init__(self):
        self._data = {}

    def put(self, key, value):
        self._data[key] = value

    def get(self, key, default=None):
        return self._data.get(key, default)

    def __len__(self):
        return len(self._data)
'''},
 py_verify('''
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
'''),
 {'src/ttlcache.py': '''import time


class SimpleCache:
    def __init__(self, clock=time.monotonic):
        self._data = {}
        self._clock = clock

    def put(self, key, value, ttl=None):
        deadline = None if ttl is None else self._clock() + ttl
        self._data[key] = (value, deadline)

    def get(self, key, default=None):
        entry = self._data.get(key)
        if entry is None:
            return default
        value, deadline = entry
        if deadline is not None and self._clock() >= deadline:
            del self._data[key]
            return default
        return value

    def __len__(self):
        return len(self._data)
'''}, S, 480, 35))

# ---- 6 flags parser (feature, optimization)
A(task('py-feat-flag-parser', 'python', 'feature', 'optimization', 'medium',
 'parse_args(argv) in src/flags.py returns (options, positionals). Extend it: support --key=value as well as --key value, a bare --flag becomes True, a repeated option collects values into a list, and a lone -- ends option parsing (everything after it is positional). Keep existing behaviour for plain positionals.',
 {'src/flags.py': '''def parse_args(argv):
    options = {}
    positionals = []
    for arg in argv:
        if arg.startswith("--"):
            options[arg[2:]] = True
        else:
            positionals.append(arg)
    return options, positionals
'''},
 py_verify('''
from flags import parse_args
check('plain', parse_args(['a', 'b']) == ({}, ['a', 'b']))
check('bare flag', parse_args(['--verbose']) == ({'verbose': True}, []))
check('equals', parse_args(['--name=x']) == ({'name': 'x'}, []))
check('space value', parse_args(['--name', 'x', 'pos']) == ({'name': 'x'}, ['pos']))
check('repeat', parse_args(['--tag=a', '--tag=b', '--tag', 'c'])[0] == {'tag': ['a', 'b', 'c']})
check('double dash', parse_args(['--x', '--', '--y', 'z']) == ({'x': True}, ['--y', 'z']))
check('flag before flag', parse_args(['--a', '--b=1']) == ({'a': True, 'b': '1'}, []))
'''),
 {'src/flags.py': '''def parse_args(argv):
    options = {}
    positionals = []

    def add(key, value):
        if key in options:
            current = options[key]
            if isinstance(current, list):
                current.append(value)
            else:
                options[key] = [current, value]
        else:
            options[key] = value

    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--":
            positionals.extend(argv[i + 1:])
            break
        if arg.startswith("--"):
            body = arg[2:]
            if "=" in body:
                key, value = body.split("=", 1)
                add(key, value)
            elif i + 1 < len(argv) and not argv[i + 1].startswith("--"):
                add(body, argv[i + 1])
                i += 1
            else:
                add(body, True)
        else:
            positionals.append(arg)
        i += 1
    return options, positionals
'''}, S, 480, 35))

# ---- 7 money format (feature, held-out)
A(task('py-feat-money-format', 'python', 'feature', 'held-out', 'easy',
 'format_money(cents, currency="USD") in src/money.py currently returns e.g. "USD 5.00". Extend it so amounts use thousands separators ("USD 1,234.50"), negative amounts are shown in parentheses ("(USD 1,234.50)"), and the currency code always stays in front, for any currency ("EUR 1,000.00"). Zero must be "USD 0.00". Use integer arithmetic only (no float rounding).',
 {'src/money.py': '''def format_money(cents, currency="USD"):
    whole, frac = divmod(cents, 100)
    return "%s %d.%02d" % (currency, whole, frac)
'''},
 py_verify('''
from money import format_money
check('small', format_money(500) == 'USD 5.00')
check('thousands', format_money(123450) == 'USD 1,234.50')
check('millions', format_money(123456789) == 'USD 1,234,567.89')
check('negative', format_money(-123450) == '(USD 1,234.50)')
check('negative small', format_money(-5) == '(USD 0.05)')
check('zero', format_money(0) == 'USD 0.00')
check('eur', format_money(100000, 'EUR') == 'EUR 1,000.00')
'''),
 {'src/money.py': '''def format_money(cents, currency="USD"):
    negative = cents < 0
    whole, frac = divmod(abs(cents), 100)
    text = "%s %s.%02d" % (currency, format(whole, ","), frac)
    return "(" + text + ")" if negative else text
'''}, S, 300, 25))

# ---- 8 refactor pricing (optimization)
A(task('py-refactor-pricing-dedupe', 'python', 'refactor', 'optimization', 'medium',
 'src/pricing.py has three public functions (price_basic, price_plus, price_pro) that each repeat the same discount-and-tax arithmetic with different constants. Refactor so all three delegate to one private helper named _apply_pricing(base_cents, discount_percent, tax_percent). Public behaviour and results must stay identical (integer cents, round half up on each step).',
 {'src/pricing.py': '''def price_basic(base_cents):
    discounted = base_cents - (base_cents * 5 + 50) // 100
    taxed = discounted + (discounted * 20 + 50) // 100
    return taxed


def price_plus(base_cents):
    discounted = base_cents - (base_cents * 10 + 50) // 100
    taxed = discounted + (discounted * 20 + 50) // 100
    return taxed


def price_pro(base_cents):
    discounted = base_cents - (base_cents * 15 + 50) // 100
    taxed = discounted + (discounted * 22 + 50) // 100
    return taxed
'''},
 py_verify('''
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
'''),
 {'src/pricing.py': '''def _apply_pricing(base_cents, discount_percent, tax_percent):
    discounted = base_cents - (base_cents * discount_percent + 50) // 100
    return discounted + (discounted * tax_percent + 50) // 100


def price_basic(base_cents):
    return _apply_pricing(base_cents, 5, 20)


def price_plus(base_cents):
    return _apply_pricing(base_cents, 10, 20)


def price_pro(base_cents):
    return _apply_pricing(base_cents, 15, 22)
'''}, S, 420, 30))

# ---- 9 extract module (held-out, mandatory)
A(task('py-refactor-extract-validators', 'python', 'refactor', 'held-out', 'medium',
 'src/app.py mixes request handling with input validators. Move is_email and is_port into a new module src/validators.py. app.py must import them from validators (so `app.is_email` still resolves) and must no longer define them itself. register(email, port) behaviour must not change.',
 {'src/app.py': '''def is_email(value):
    if not isinstance(value, str) or value.count("@") != 1:
        return False
    local, domain = value.split("@")
    return bool(local) and "." in domain and not domain.startswith(".") and not domain.endswith(".")


def is_port(value):
    return isinstance(value, int) and not isinstance(value, bool) and 1 <= value <= 65535


def register(email, port):
    if not is_email(email):
        return {"ok": False, "error": "email"}
    if not is_port(port):
        return {"ok": False, "error": "port"}
    return {"ok": True, "email": email.lower(), "port": port}
'''},
 py_verify('''
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
'''),
 {'src/app.py': '''from validators import is_email, is_port


def register(email, port):
    if not is_email(email):
        return {"ok": False, "error": "email"}
    if not is_port(port):
        return {"ok": False, "error": "port"}
    return {"ok": True, "email": email.lower(), "port": port}
''',
  'src/validators.py': '''def is_email(value):
    if not isinstance(value, str) or value.count("@") != 1:
        return False
    local, domain = value.split("@")
    return bool(local) and "." in domain and not domain.startswith(".") and not domain.endswith(".")


def is_port(value):
    return isinstance(value, int) and not isinstance(value, bool) and 1 <= value <= 65535
'''}, S, 420, 30, mandatory=True))

# ---- 10 test writing stack (optimization)
A(task('py-test-stack', 'python', 'testwriting', 'optimization', 'easy',
 'Write unit tests for the Stack class in src/stack.py as tests/test_stack.py using the standard unittest module (run with: python -m unittest tests.test_stack from the repository root, with PYTHONPATH=src). Cover push/pop order, peek, len, empty-stack errors and the max_size limit. Do not modify src/.',
 {'src/stack.py': '''class Stack:
    def __init__(self, max_size=None):
        self._items = []
        self.max_size = max_size

    def push(self, item):
        if self.max_size is not None and len(self._items) >= self.max_size:
            raise OverflowError("stack full")
        self._items.append(item)

    def pop(self):
        if not self._items:
            raise IndexError("pop from empty stack")
        return self._items.pop()

    def peek(self):
        if not self._items:
            raise IndexError("peek at empty stack")
        return self._items[-1]

    def __len__(self):
        return len(self._items)
''', 'tests/__init__.py': ''},
 mut_verify('''{
 'tests': ['tests/test_stack.py'],
 'cmd': ['PY', '-B', '-m', 'unittest', 'tests.test_stack'],
 'src': 'src/stack.py',
 'mutants': [
   ('return self._items.pop()', 'return self._items.pop(0)'),
   ('return self._items[-1]', 'return self._items[0]'),
   ('len(self._items) >= self.max_size', 'len(self._items) > self.max_size'),
   ('raise IndexError("pop from empty stack")', 'return None'),
   ('raise IndexError("peek at empty stack")', 'return None'),
   ('return len(self._items)', 'return len(self._items) + 1'),
 ]}'''),
 {'tests/test_stack.py': '''import unittest

from stack import Stack


class StackTests(unittest.TestCase):
    def test_lifo(self):
        s = Stack()
        s.push(1)
        s.push(2)
        self.assertEqual(s.pop(), 2)
        self.assertEqual(s.pop(), 1)

    def test_peek(self):
        s = Stack()
        s.push("a")
        s.push("b")
        self.assertEqual(s.peek(), "b")
        self.assertEqual(len(s), 2)

    def test_len(self):
        s = Stack()
        self.assertEqual(len(s), 0)
        s.push(1)
        self.assertEqual(len(s), 1)

    def test_pop_empty(self):
        with self.assertRaises(IndexError):
            Stack().pop()

    def test_peek_empty(self):
        with self.assertRaises(IndexError):
            Stack().peek()

    def test_max_size(self):
        s = Stack(max_size=2)
        s.push(1)
        s.push(2)
        with self.assertRaises(OverflowError):
            s.push(3)
        self.assertEqual(len(s), 2)


if __name__ == "__main__":
    unittest.main()
'''}, ['tests/'], 420, 30))

# ---- 11 test writing intervals (held-out)
A(task('py-test-merge-intervals', 'python', 'testwriting', 'held-out', 'medium',
 'Write unit tests for merge_intervals in src/intervals.py as tests/test_intervals.py using unittest (run with: python -m unittest tests.test_intervals from the repository root, PYTHONPATH=src). The tests must exercise unsorted input, touching intervals, nested intervals, empty input, and invalid intervals (start > end raises ValueError). Do not modify src/.',
 {'src/intervals.py': '''def merge_intervals(intervals):
    for start, end in intervals:
        if start > end:
            raise ValueError("invalid interval")
    merged = []
    for start, end in sorted(intervals):
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    return merged
''', 'tests/__init__.py': ''},
 mut_verify('''{
 'tests': ['tests/test_intervals.py'],
 'cmd': ['PY', '-B', '-m', 'unittest', 'tests.test_intervals'],
 'src': 'src/intervals.py',
 'mutants': [
   ('start <= merged[-1][1]', 'start < merged[-1][1]'),
   ('max(merged[-1][1], end)', 'end'),
   ('sorted(intervals)', 'intervals'),
   ('if start > end:', 'if start >= end:'),
   ('merged[-1][0], max', 'start, max'),
   ('for start, end in intervals:\\n        if start > end:\\n            raise ValueError("invalid interval")', 'pass'),
 ]}'''),
 {'tests/test_intervals.py': '''import unittest

from intervals import merge_intervals


class MergeTests(unittest.TestCase):
    def test_empty(self):
        self.assertEqual(merge_intervals([]), [])

    def test_unsorted(self):
        self.assertEqual(merge_intervals([(5, 6), (1, 2)]), [(1, 2), (5, 6)])

    def test_overlap(self):
        self.assertEqual(merge_intervals([(1, 3), (2, 6), (8, 10)]), [(1, 6), (8, 10)])

    def test_touching(self):
        self.assertEqual(merge_intervals([(1, 2), (2, 3)]), [(1, 3)])

    def test_nested(self):
        self.assertEqual(merge_intervals([(1, 10), (2, 3), (4, 5)]), [(1, 10)])

    def test_unsorted_nested_start(self):
        self.assertEqual(merge_intervals([(4, 5), (1, 10), (2, 3)]), [(1, 10)])

    def test_point_interval_valid(self):
        self.assertEqual(merge_intervals([(3, 3)]), [(3, 3)])

    def test_invalid(self):
        with self.assertRaises(ValueError):
            merge_intervals([(5, 1)])
        with self.assertRaises(ValueError):
            merge_intervals([(1, 2), (9, 4)])


if __name__ == "__main__":
    unittest.main()
'''}, ['tests/'], 480, 30))

# ---- 12 retry backoff (bugfix, held-out)
A(task('py-bug-retry-backoff', 'python', 'bugfix', 'held-out', 'medium',
 'retry(fn, attempts, base_delay, max_delay, sleep) in src/retry.py should call fn up to `attempts` times. After failed attempt n (1-based) it sleeps min(base_delay * 2**(n-1), max_delay) seconds, but never sleeps after the final attempt; if all attempts fail it re-raises the last exception. The current code sleeps linearly and also sleeps after the last failure. Fix it.',
 {'src/retry.py': '''def retry(fn, attempts, base_delay, max_delay, sleep):
    last = None
    for n in range(1, attempts + 1):
        try:
            return fn()
        except Exception as exc:  # noqa: BLE001
            last = exc
            sleep(min(base_delay * n, max_delay))
    raise last
'''},
 py_verify('''
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
'''),
 {'src/retry.py': '''def retry(fn, attempts, base_delay, max_delay, sleep):
    last = None
    for n in range(1, attempts + 1):
        try:
            return fn()
        except Exception as exc:  # noqa: BLE001
            last = exc
            if n < attempts:
                sleep(min(base_delay * 2 ** (n - 1), max_delay))
    raise last
'''}, S, 420, 30))
