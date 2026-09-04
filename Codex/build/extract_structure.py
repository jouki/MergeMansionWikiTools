# -*- coding: utf-8 -*-
"""Per game version: structural dumps (dialogues/areas/events/chain_item_odds) into Codex/_cache/structure/<ver>/.
Priority per version: existing Dump folder with dialogues.json (copied) > config archive (harness dump)
> embedded SharedGameConfig.mpa (spike S1) > old Dump folder with areas/events only.
Run: python Codex/build/extract_structure.py [ver ...]"""
import io
import os
import re
import shutil
import subprocess
import sys
import zipfile

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402
import extract_loc  # noqa: E402

WANTED = ["dialogues.json", "areas.json", "events.json", "chain_item_odds.json"]
CFG_ENTRY = "assets/SharedGameConfig.mpa"
# ContentHash.ParseString accepts only 16hex-16hex names; the harness derives the language hash from the file name.
LANG_FILE_NAME = "0000000000000000-0000000000000000"


def pick_dump(dumps):
    if not dumps:
        return None
    plain = [d for d in dumps if os.path.basename(d["folder"]).lower() == "dump"]
    if plain:
        return plain[0]["folder"]

    def num(d):
        m = re.search(r"dump\s*(\d+)$", os.path.basename(d["folder"]).lower())
        return int(m.group(1)) if m else -1

    numbered = [d for d in dumps if num(d) >= 0]
    return max(numbered, key=num)["folder"] if numbered else dumps[0]["folder"]


def newest_config(archive_dirs):
    """Config archive dirs hold one file each (content-addressed); take the newest file by mtime."""
    files = []
    for d in archive_dirs:
        files += [os.path.join(d, f) for f in os.listdir(d)] if os.path.isdir(d) else [d]
    return max(files, key=os.path.getmtime) if files else None


def embedded_config(apk_path):
    with zipfile.ZipFile(apk_path) as z:
        if CFG_ENTRY in z.namelist():
            return z.read(CFG_ENTRY)
        for n in z.namelist():
            if n.endswith(".apk"):
                with zipfile.ZipFile(io.BytesIO(z.read(n))) as iz:
                    if CFG_ENTRY in iz.namelist():
                        return iz.read(CFG_ENTRY)
    return None


def dump_from_config(exe, config_path, lang_mpc, out_dir):
    """Run the existing --dump-full-patched (no patches) over one config; returns (ok, log tail)."""
    lang_arg = ""
    if lang_mpc and os.path.exists(lang_mpc):
        lang_arg = os.path.join(out_dir, LANG_FILE_NAME)
        shutil.copyfile(lang_mpc, lang_arg)
    r = subprocess.run([exe, "--dump-full-patched", config_path, "", lang_arg, out_dir],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    ok = r.returncode == 0 and os.path.exists(os.path.join(out_dir, "dialogues.json"))
    if lang_arg and os.path.exists(lang_arg):
        os.remove(lang_arg)
    return ok, (r.stdout + r.stderr)[-1500:]


def copy_dump(folder, out):
    for f in WANTED:
        if os.path.exists(os.path.join(folder, f)):
            shutil.copy2(os.path.join(folder, f), os.path.join(out, f))


def main(only=None):
    inv = common.read_json(os.path.join(common.CODEX, "sources.json"))
    exe = extract_loc.build_harness()
    report = {}
    for ver, e in inv["versions"].items():
        if only and ver not in only:
            continue
        out = os.path.join(common.CACHE, "structure", ver)
        if os.path.exists(os.path.join(out, "source.json")):
            print(f"{ver}: cached")
            continue
        os.makedirs(out, exist_ok=True)
        lang = os.path.join(common.CACHE, "loc", ver + ".en.mpc")
        lang = lang if os.path.exists(lang) else None
        src = None
        folder = pick_dump(e["dumps"])
        if folder and os.path.exists(os.path.join(folder, "dialogues.json")):
            copy_dump(folder, out)
            src = {"kind": "dump", "path": folder}
        elif e["configArchives"]:
            cfg = newest_config(e["configArchives"])
            ok, log = dump_from_config(exe, cfg, lang, out)
            src = {"kind": "configArchive", "path": cfg, "ok": ok, "log": log}
        elif e["embeddedConfig"] and e["apk"]:
            mpa = os.path.join(out, "SharedGameConfig.mpa")
            with open(mpa, "wb") as f:
                f.write(embedded_config(e["apk"]))
            ok, log = dump_from_config(exe, mpa, lang, out)
            src = {"kind": "embeddedConfig", "path": e["apk"], "ok": ok, "log": log}
        if src is None and folder:            # old dumper output: areas/events only
            copy_dump(folder, out)
            src = {"kind": "dump", "path": folder}
        if src is not None and src["kind"] != "dump" and folder:
            # Today's dumper imports old configs only partially (Areas/MergeChains come back null for
            # pre-26.07 schemas) — complement whatever the old dump folder offers, never overwrite.
            missing = [f for f in WANTED if not os.path.exists(os.path.join(out, f))]
            for f in missing:
                if os.path.exists(os.path.join(folder, f)):
                    shutil.copy2(os.path.join(folder, f), os.path.join(out, f))
            src["complementDump"] = folder
        if src is None:
            src = {"kind": "none"}
        common.write_json(os.path.join(out, "source.json"), src)
        have = [f for f in WANTED if os.path.exists(os.path.join(out, f))]
        report[ver] = (src["kind"], src.get("ok"), have)
        print(f"{ver}: {src['kind']} ok={src.get('ok')} files={have}")
    return report


if __name__ == "__main__":
    main(set(sys.argv[1:]) or None)
