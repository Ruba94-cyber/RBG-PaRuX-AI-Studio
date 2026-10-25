class LRUCache:
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
