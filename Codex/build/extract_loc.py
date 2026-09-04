# -*- coding: utf-8 -*-
"""Per game version: en.mpc out of the APK/XAPK -> DumpHarness --dump-loc -> Codex/_cache/loc/<ver>.json
Run: python Codex/build/extract_loc.py [ver ...]   (no args = every version in sources.json with loc)"""
import io
import os
import subprocess
import sys
import zipfile

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

LOC_ENTRY = "assets/Localizations/en.mpc"


PAYLOAD_MAGIC = b""   # LocalizationLanguage binary always starts with this (NullableStruct + members)


def strip_envelope(data):
    """Since 26.07.01 the XAPK wraps en.mpc in an 'MPE' envelope (14-byte header) that
    LocalizationLanguage.ImportBinary rejects ("starts with 4D"). Cut down to the payload."""
    if data[:3] == b"MPE":
        i = data.find(PAYLOAD_MAGIC, 0, 64)
        if i > 0:
            return data[i:]
    return data


def extract_mpc(apk_path, loc_kind):
    if loc_kind == "apk":
        with zipfile.ZipFile(apk_path) as z:
            return strip_envelope(z.read(LOC_ENTRY))
    if loc_kind and loc_kind.startswith("inner:"):
        with zipfile.ZipFile(apk_path) as z:
            with zipfile.ZipFile(io.BytesIO(z.read(loc_kind[6:]))) as iz:
                return strip_envelope(iz.read(LOC_ENTRY))
    raise ValueError(f"no localization in {apk_path} ({loc_kind})")


def build_harness():
    """Build DumpHarness into %TEMP% when missing or older than Program.cs (never into the app's bin: exe lock)."""
    exe = common.harness_exe()
    src = os.path.join(common.REPO, "_DumpHarness", "Program.cs")
    if os.path.exists(exe) and os.path.getmtime(exe) >= os.path.getmtime(src):
        return exe
    print("building DumpHarness ...")
    subprocess.run(["dotnet", "build", os.path.join(common.REPO, "_DumpHarness"), "-c", "Debug", "-o", os.path.dirname(exe)],
                   check=True, stdout=subprocess.DEVNULL)
    return exe


def dump_loc(exe, mpc_path, out_json):
    r = subprocess.run([exe, "--dump-loc", mpc_path, out_json], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        raise RuntimeError(f"--dump-loc failed ({r.returncode}): {r.stderr.strip()[-400:]}")


def main(only=None):
    inv = common.read_json(os.path.join(common.CODEX, "sources.json"))
    exe = build_harness()
    out_dir = os.path.join(common.CACHE, "loc")
    os.makedirs(out_dir, exist_ok=True)
    failures = []
    for ver, e in inv["versions"].items():
        if only and ver not in only:
            continue
        if not e["apk"] or e["loc"] in (None, "none"):
            print(f"{ver}: no localization source")
            continue
        out = os.path.join(out_dir, ver + ".json")
        if os.path.exists(out):
            print(f"{ver}: cached")
            continue
        mpc = os.path.join(out_dir, ver + ".en.mpc")
        with open(mpc, "wb") as f:
            f.write(extract_mpc(e["apk"], e["loc"]))
        try:
            dump_loc(exe, mpc, out)
            print(f"{ver}: {len(common.read_json(out))} translations")
        except RuntimeError as ex:
            failures.append((ver, str(ex)))
            print(f"{ver}: FAILED {ex}")
    if failures:
        print("FAILED versions:", [v for v, _ in failures])
    return failures


if __name__ == "__main__":
    main(set(sys.argv[1:]) or None)
