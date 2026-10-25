import unittest

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
