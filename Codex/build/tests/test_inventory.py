import io, json, os, sys, tempfile, unittest, zipfile
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import inventory

def make_apk(path, names, nested=None):
    with zipfile.ZipFile(path, "w") as z:
        for n in names:
            z.writestr(n, b"x")
        if nested:
            buf = io.BytesIO()
            with zipfile.ZipFile(buf, "w") as iz:
                for n in nested[1]:
                    iz.writestr(n, b"x")
            z.writestr(nested[0], buf.getvalue())

class InventoryTests(unittest.TestCase):
    def test_scan_classifies_sources(self):
        with tempfile.TemporaryDirectory() as d:
            v1 = os.path.join(d, "22.02.06"); os.makedirs(v1)
            make_apk(os.path.join(v1, "a.apk"), ["assets/Localizations/en.mpc", "assets/SharedGameConfig.mpa"])
            v2 = os.path.join(d, "26.07.01"); os.makedirs(os.path.join(v2, "Dump"))
            make_apk(os.path.join(v2, "b.xapk"), ["manifest.json"], nested=("UnityDataAssetPack.apk", ["assets/Localizations/en.mpc"]))
            open(os.path.join(v2, "Dump", "dialogues.json"), "w").close()
            open(os.path.join(v2, "B08303BA4AFC29FF-C94EBB336F7F2810"), "wb").close()   # archives are files
            os.makedirs(os.path.join(d, "Processed Images"))
            inv = inventory.scan(d)
            self.assertEqual(sorted(inv["versions"]), ["22.02.06", "26.07.01"])
            a = inv["versions"]["22.02.06"]
            self.assertEqual(a["loc"], "apk"); self.assertTrue(a["embeddedConfig"]); self.assertEqual(a["dumps"], [])
            b = inv["versions"]["26.07.01"]
            self.assertEqual(b["loc"], "inner:UnityDataAssetPack.apk"); self.assertFalse(b["embeddedConfig"])
            self.assertEqual(len(b["configArchives"]), 1)
            self.assertEqual(b["dumps"][0]["files"], ["dialogues.json"])

    def test_missing_months(self):
        self.assertEqual(inventory.missing_months(["22.02.06", "22.04.01", "22.05.02"]), ["2022-03"])

if __name__ == "__main__":
    unittest.main()
