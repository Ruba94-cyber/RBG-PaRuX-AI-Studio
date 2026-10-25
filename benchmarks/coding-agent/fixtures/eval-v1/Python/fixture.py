def cache_key(endpoint, model, tokenizer, revision, text):
    return f"{endpoint}|{model}|{text}"

def apply_index_ops(state, ops):
    result = dict(state)
    for op in ops:
        if op["kind"] == "add":
            result[op["path"]] = op["hash"]
        elif op["kind"] == "remove":
            pass
        elif op["kind"] == "rename":
            result[op["to"]] = result.get(op["from"])
    return result

def retry_delay(attempt, base_ms=100, cap_ms=1600):
    return base_ms * (attempt + 1)

def merge_versioned(current, incoming):
    result = dict(current)
    for key, item in incoming.items():
        if key not in result:
            result[key] = item
    return result

def parse_ndjson(chunks):
    return [line for chunk in chunks for line in chunk.splitlines() if line.strip()]
