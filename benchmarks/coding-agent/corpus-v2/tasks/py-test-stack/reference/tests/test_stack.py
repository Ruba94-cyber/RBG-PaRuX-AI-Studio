import unittest

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
