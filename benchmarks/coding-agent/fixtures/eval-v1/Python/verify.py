import importlib.util, pathlib, sys

here = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("fixture", here / "fixture.py")
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)

case = sys.argv[1]
if case == "python-cache-identity":
    a = fixture.cache_key("local","alias","tok-a","rev-1","hello")
    b = fixture.cache_key("local","alias","tok-b","rev-1","hello")
    c = fixture.cache_key("local","alias","tok-a","rev-2","hello")
    assert a != b and a != c
elif case == "python-incremental-index":
    state = {"a.py":"1","b.py":"2"}
    ops = [
        {"kind":"remove","path":"a.py"},
        {"kind":"rename","from":"b.py","to":"c.py"},
        {"kind":"add","path":"d.py","hash":"4"},
    ]
    assert fixture.apply_index_ops(state, ops) == {"c.py":"2","d.py":"4"}
elif case == "python-retry-backoff":
    assert [fixture.retry_delay(i) for i in range(6)] == [100,200,400,800,1600,1600]
elif case == "python-versioned-merge":
    current = {"mode":{"version":2,"value":"new"},"x":{"version":1,"value":"x"}}
    incoming = {"mode":{"version":1,"value":"old"},"x":{"version":3,"value":"xx"},"y":{"version":1,"value":"y"}}
    out = fixture.merge_versioned(current,incoming)
    assert out["mode"]["value"] == "new" and out["x"]["value"] == "xx" and out["y"]["value"] == "y"
elif case == "python-ndjson-stream":
    chunks = ['{"a":1}\n{"b"', ':2}\n\n{"done":', 'true}\n']
    assert fixture.parse_ndjson(chunks) == [{"a":1},{"b":2},{"done":True}]
else:
    raise SystemExit(f"unknown case {case}")
print("PASS")
