import time


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
