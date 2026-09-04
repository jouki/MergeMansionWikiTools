import json, os, sys, tempfile, unittest
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import common

class RunsTests(unittest.TestCase):
    def test_collapses_equal_consecutive_values(self):
        pairs = [("26.02.01", "a"), ("26.03.01", "a"), ("26.04.01", "b"), ("26.05.01", "b")]
        self.assertEqual(common.runs(pairs), [{"from": "26.02.01", "value": "a"}, {"from": "26.04.01", "value": "b"}])

    def test_absence_is_null_run(self):
        pairs = [("26.02.01", "a"), ("26.03.01", None), ("26.04.01", "a")]
        self.assertEqual(common.runs(pairs), [{"from": "26.02.01", "value": "a"}, {"from": "26.03.01", "value": None}, {"from": "26.04.01", "value": "a"}])

    def test_empty(self):
        self.assertEqual(common.runs([]), [])

class ReadJsonTests(unittest.TestCase):
    def test_bom_and_data_unwrap(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "x.json")
            with open(p, "w", encoding="utf-8-sig") as f:
                json.dump({"CreatedAt": "2026", "Data": [1, 2]}, f)
            self.assertEqual(common.read_json(p), [1, 2])

    def test_bare_root_kept(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "y.json")
            with open(p, "w", encoding="utf-8") as f:
                json.dump([{"a": 1}], f)
            self.assertEqual(common.read_json(p), [{"a": 1}])

class VersionsTests(unittest.TestCase):
    def test_filters_and_sorts(self):
        with tempfile.TemporaryDirectory() as d:
            for n in ["26.02.01", "22.02.06", "26.01.02_TEST", "Processed Images", "25.06.01"]:
                os.makedirs(os.path.join(d, n))
            self.assertEqual(common.versions(d), ["22.02.06", "25.06.01", "26.02.01"])

if __name__ == "__main__":
    unittest.main()
