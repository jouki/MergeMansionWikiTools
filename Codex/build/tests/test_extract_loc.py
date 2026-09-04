import io, os, sys, tempfile, unittest, zipfile
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import extract_loc

PAYLOAD = bytes([0x0F, 0x02, 0x0C, 0x02, 0x04]) + b"en"
MPE_HEADER = b"MPE" + bytes([0xE6, 0xFF, 0x00, 0x0A, 0x20, 0x0D, 0x3A, 0x56, 0xBA, 0x41, 0x00])


class ExtractMpcTests(unittest.TestCase):
    def test_top_level(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "a.apk")
            with zipfile.ZipFile(p, "w") as z:
                z.writestr("assets/Localizations/en.mpc", b"TOP")
            self.assertEqual(extract_loc.extract_mpc(p, "apk"), b"TOP")

    def test_nested(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "a.xapk")
            buf = io.BytesIO()
            with zipfile.ZipFile(buf, "w") as iz:
                iz.writestr("assets/Localizations/en.mpc", b"INNER")
            with zipfile.ZipFile(p, "w") as z:
                z.writestr("UnityDataAssetPack.apk", buf.getvalue())
            self.assertEqual(extract_loc.extract_mpc(p, "inner:UnityDataAssetPack.apk"), b"INNER")

    def test_mpe_envelope_stripped(self):
        self.assertEqual(extract_loc.strip_envelope(MPE_HEADER + PAYLOAD), PAYLOAD)

    def test_plain_payload_untouched(self):
        self.assertEqual(extract_loc.strip_envelope(PAYLOAD + b"xyz"), PAYLOAD + b"xyz")

    def test_none_raises(self):
        with self.assertRaises(ValueError):
            extract_loc.extract_mpc("x.apk", "none")


if __name__ == "__main__":
    unittest.main()
