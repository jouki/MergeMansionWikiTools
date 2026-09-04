import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import extract_structure as es


class PickDumpTests(unittest.TestCase):
    def test_prefers_plain_dump(self):
        dumps = [{"folder": r"X\Dump 3", "files": ["areas.json"]}, {"folder": r"X\Dump", "files": ["areas.json"]}]
        self.assertEqual(es.pick_dump(dumps), r"X\Dump")

    def test_highest_numbered_otherwise(self):
        dumps = [{"folder": r"X\Dump 2", "files": ["areas.json"]}, {"folder": r"X\Dump 10", "files": ["areas.json"]},
                 {"folder": r"X\Dump 6_bkup", "files": ["areas.json"]}]
        self.assertEqual(es.pick_dump(dumps), r"X\Dump 10")

    def test_none(self):
        self.assertIsNone(es.pick_dump([]))


if __name__ == "__main__":
    unittest.main()
