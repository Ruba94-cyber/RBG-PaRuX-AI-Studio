class Stack:
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
