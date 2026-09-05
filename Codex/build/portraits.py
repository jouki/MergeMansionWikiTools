# -*- coding: utf-8 -*-
"""Character portraits per emotion state for the viewer: crop them from the exported atlas PNGs.
Run: python Codex/build/portraits.py   -> Codex/_cache/portraits/<CharacterId>__<State>.png (+ index.json)
Source: APKs/<newest>/image_atlas_data.json (sprite rects) + APKs/<newest>/Export - PNGs/**/<textureName>.png.
Sprite naming in the game: <Character><State> (MaddieWorried), <Character>_<State> (McLeod_Doubtful); Default = <Character>Default
or the bare character name. Nothing is stored in git (derived from the user's asset export)."""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

try:
    from PIL import Image
except ImportError:                                     # PIL 12 is installed on this machine; keep the message helpful anyway
    Image = None

SIZE = 160
STATE_FALLBACKS = {"NoChange": "Default", None: "Default", "": "Default"}


def newest_version_dir():
    return os.path.join(common.APKS, common.versions()[-1])


def png_index(export_root):
    """textureName -> path of the exported PNG (first match wins; assembled/skin variants are skipped)."""
    out = {}
    for root, _dirs, files in os.walk(export_root):
        for f in files:
            if f.lower().endswith(".png"):
                out.setdefault(os.path.splitext(f)[0], os.path.join(root, f))
    return out


def sprite_candidates(cid, name, state):
    base_ids = [cid, name.replace(" ", ""), name.replace(" ", "").replace(".", "")]
    if cid == "AntiqueDealer":
        base_ids += ["Julius"]
    if cid == "Grandma":
        base_ids += ["Ursula", "GrandmaUrsula"]
    if cid == "Voyance":
        base_ids += ["LadyVoyance"]
    out = []
    for b in dict.fromkeys(base_ids):
        if state == "Default":
            out += [f"{b}Default", f"{b}_Default", f"{b}-Default", b]
        out += [f"{b}{state}", f"{b}_{state}", f"{b}-{state}"]
    return out


def crop(png_path, sp):
    im = Image.open(png_path).convert("RGBA")
    w, h = im.size
    x, y, rw, rh = sp["rectX"], sp["rectY"], sp["rectWidth"], sp["rectHeight"]
    if rw <= 0 or rh <= 0:
        return None
    # Unity sprite rects are bottom-left based; the exported PNG is top-left based
    box = (x, h - (y + rh), x + rw, h - y)
    if sp.get("rotated"):
        box = (x, h - (y + rw), x + rh, h - y)
    region = im.crop(box)
    if sp.get("rotated"):
        region = region.rotate(90, expand=True)
    region.thumbnail((SIZE, SIZE))
    return region


def main():
    if Image is None:
        print("PIL missing: pip install pillow")
        return 1
    codex = common.read_json(os.path.join(common.CODEX, "codex.json"))
    # newest version first; older versions with an atlas + PNG export fill in characters that left the game (Jailbreak cast)
    sprites, pngs = {}, {}
    sources = []
    for v in reversed(common.versions()):
        vd = os.path.join(common.APKS, v)
        atlas_p = os.path.join(vd, "image_atlas_data.json")
        exp = next((os.path.join(vd, d) for d in ("Export - PNGs", "Export", "Export Bundles - PNGs") if os.path.isdir(os.path.join(vd, d))), None)
        if os.path.exists(atlas_p) and exp:
            sources.append(v)
            for sp in common.read_json(atlas_p).get("sprites") or []:
                sprites.setdefault(sp["name"], sp)
            for k, p in png_index(exp).items():
                pngs.setdefault(k, p)
    print("asset sources (newest first):", sources)
    states = set()
    for l in codex["lines"].values():
        for r in l.get("state") or []:
            if r["value"]:
                states.add(r["value"])
    states = sorted(s for s in states if s not in ("NoChange", "None", "Empty")) or ["Default"]
    if "Default" not in states:
        states.append("Default")
    out_dir = os.path.join(common.CACHE, "portraits")
    os.makedirs(out_dir, exist_ok=True)
    index, missing_png = {}, set()
    for cid, c in codex["characters"].items():
        for state in states:
            for cand in sprite_candidates(cid, c["name"], state):
                sp = sprites.get(cand)
                if not sp:
                    continue
                png = pngs.get(sp["textureName"]) or pngs.get(sp["name"])
                if not png:
                    missing_png.add(sp["textureName"])
                    continue
                dst = os.path.join(out_dir, f"{cid}__{state}.png")
                if not os.path.exists(dst):
                    img = crop(png, sp)
                    if img is None:
                        continue
                    img.save(dst, optimize=True)
                index.setdefault(cid, {})[state] = os.path.basename(dst)
                break
    common.write_json(os.path.join(out_dir, "index.json"), {"size": SIZE, "states": states, "portraits": index})
    have = sum(len(v) for v in index.values())
    no_default = [cid for cid in codex["characters"] if "Default" not in index.get(cid, {})]
    print(f"portraits: {have} files for {len(index)}/{len(codex['characters'])} characters, states {states}")
    print("characters without a Default portrait:", no_default)
    if missing_png:
        print("sprites whose texture PNG is not exported:", sorted(missing_png)[:15], "…" if len(missing_png) > 15 else "")
    return 0


if __name__ == "__main__":
    sys.exit(main())
