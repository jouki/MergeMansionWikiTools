# -*- coding: utf-8 -*-
"""Inventory of game versions in APKS: which sources each folder offers.
Run: python Codex/build/inventory.py  -> Codex/sources.json + missing months on stdout."""
import io
import os
import re
import sys
import zipfile

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

CFG_RE = re.compile(r"^[0-9A-F]{16}-[0-9A-F]{16}$", re.I)
LOC_ENTRY = "assets/Localizations/en.mpc"
CFG_ENTRY = "assets/SharedGameConfig.mpa"


def probe_apk(path):
    """Return (loc, embeddedConfig): loc = 'apk' | 'inner:<entry>' | 'none'."""
    with zipfile.ZipFile(path) as z:
        names = set(z.namelist())
        emb = CFG_ENTRY in names
        if LOC_ENTRY in names:
            return "apk", emb
        for n in sorted(names):
            if n.endswith(".apk"):
                with zipfile.ZipFile(io.BytesIO(z.read(n))) as iz:
                    inner = set(iz.namelist())
                    if LOC_ENTRY in inner:
                        return "inner:" + n, emb or CFG_ENTRY in inner
        return "none", emb


def scan(root=common.APKS):
    out = {}
    for v in common.versions(root):
        p = os.path.join(root, v)
        files = sorted(os.listdir(p))
        apks = [f for f in files if f.lower().endswith((".apk", ".xapk"))]
        entry = {"apk": None, "loc": None, "embeddedConfig": False, "configArchives": [], "dumps": []}
        if apks:
            entry["apk"] = os.path.join(p, apks[0])
            try:
                entry["loc"], entry["embeddedConfig"] = probe_apk(entry["apk"])
            except zipfile.BadZipFile:
                entry["loc"] = "none"
        # Metaplay config archives are content-addressed FILES (same naming as _DATA/C/<hash>); accept dirs too.
        entry["configArchives"] = [os.path.join(p, f) for f in files if CFG_RE.match(f)]
        for f in files:
            dp = os.path.join(p, f)
            if os.path.isdir(dp) and f.lower().startswith("dump"):
                js = sorted(x for x in os.listdir(dp) if x.endswith(".json"))
                if js:
                    entry["dumps"].append({"folder": dp, "files": js})
        out[v] = entry
    return {"versions": out, "missingMonths": missing_months(list(out))}


def missing_months(vers):
    """Months between the first and the last version with no build in the folder."""
    if not vers:
        return []

    def ym(v):
        return 2000 + int(v[:2]), int(v[3:5])

    have = {ym(v) for v in vers}
    (y, m), (ly, lm) = ym(min(vers)), ym(max(vers))
    missing = []
    while (y, m) < (ly, lm):
        m += 1
        if m == 13:
            y, m = y + 1, 1
        if (y, m) not in have and (y, m) < (ly, lm):
            missing.append(f"{y}-{m:02d}")
    return missing


if __name__ == "__main__":
    inv = scan()
    common.write_json(os.path.join(common.CODEX, "sources.json"), inv)
    print(f"versions: {len(inv['versions'])}")
    for v, e in inv["versions"].items():
        print(f"  {v}: loc={e['loc']} embCfg={e['embeddedConfig']} cfg={len(e['configArchives'])} dumps={[d['files'] for d in e['dumps']]}")
    print("missing months:", ", ".join(inv["missingMonths"]))
