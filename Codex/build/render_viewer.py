# -*- coding: utf-8 -*-
"""codex.json -> Codex/_cache/viewer.html (single-file browser; gitignored). Run: python Codex/build/render_viewer.py"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402
import render_md  # noqa: E402
import reruns  # noqa: E402


def adapt(codex):
    cur = codex["versions"][-1]
    chars = codex["characters"]
    stories, removed = [], []
    for sid, s in codex["stories"].items():
        ids = render_md.latest(s["lines"]) or []
        lines = []
        cur_side = {"L": None, "R": None}       # NoChange on a side = the character from the previous line of the story
        for lid in ids:
            l = codex["lines"].get(lid) or {}
            sp = render_md.latest(l.get("speaker") or [])
            st = render_md.latest(l.get("state") or [])
            texts = [r for r in (l.get("text") or []) if r["value"] not in (None, "")]
            # each rewording is classified: cosmetic (typo/punctuation/markup, >= 90 % similar) or rewritten
            changes = [{"version": q["from"], "from": p["value"], "to": q["value"], "kind": reruns.classify_change(p["value"], q["value"])}
                       for p, q in zip(texts[:-1], texts[1:]) if reruns.norm(p["value"]) != reruns.norm(q["value"])]
            raw_l, raw_r = render_md.latest(l.get("left") or []), render_md.latest(l.get("right") or [])
            left = cur_side["L"] if raw_l == "NoChange" else (None if raw_l in ("None", "Empty", None) else raw_l)
            right = cur_side["R"] if raw_r == "NoChange" else (None if raw_r in ("None", "Empty", None) else raw_r)
            cur_side["L"], cur_side["R"] = left, right
            lines.append({"id": lid, "speaker": sp, "speakerName": chars.get(sp, {}).get("name", sp) if sp else None,
                          "state": st if st and st not in ("Default", "NoChange") else None, "text": render_md.latest(l.get("text") or []) or "",
                          "left": left, "right": right, "side": "L" if sp and sp == left else ("R" if sp and sp == right else None),
                          "changes": changes, "firstSeen": (l.get("seen") or {}).get("first")})
        if s.get("seen") and s["seen"]["last"] != cur:
            trig = [dict(t) for t in s["triggers"]]                 # keep triggers: area/event filter must still find removed stories
            for t in trig:
                if t["kind"] == "itemDiscovered":
                    t.update({"kind": "item", "event": t.get("chain"), "items": [t.get("itemName") or t.get("item")]})
            removed.append({"id": sid, "lastSeen": s["seen"]["last"], "lines": lines, "triggers": trig})   # speakers kept too
        else:
            trig = [dict(t) for t in s["triggers"]] or [{"kind": "unknown", "hint": "?"}]
            for t in trig:
                if t["kind"] == "itemDiscovered":
                    t.update({"kind": "item", "event": t.get("chain"), "items": [t.get("itemName") or t.get("item")]})
            stories.append({"id": sid, "triggers": trig, "lines": lines, "firstSeen": (s.get("seen") or {}).get("first")})
    # stories with no area/event: expose them as an "Unassigned: <id prefix>" group so a human can place them
    for s in stories + removed:
        if not any(t.get("area") or t.get("event") for t in s["triggers"]):
            m = re.match(r"^([A-Za-z]+?)(?:_|\d|$)", s["id"])
            s["unassigned"] = m.group(1) if m else s["id"]
    # chronology: position (0..1) + evidence from the newest timeline of the story's area / event
    tl_path = os.path.join(common.CODEX, "timelines.json")
    if os.path.exists(tl_path):
        pos = {}
        for key, sc in common.read_json(tl_path).items():
            vers = sorted(sc["versions"])
            for e in sc["versions"][vers[-1]]:
                pos.setdefault(e["story"], {"pos": e["position"], "phase": e["phase"], "evidence": e["evidence"], "scope": sc["name"], "version": vers[-1]})
        for s in stories + removed:
            if s["id"] in pos:
                s["chrono"] = pos[s["id"]]
    n_lines = sum(1 for l in codex["lines"].values() if l["seen"] and l["seen"]["last"] == cur)
    summary = {"version": cur, "historyVersions": codex["versions"], "lines": n_lines, "stories": len(stories),
               "characters": len(chars), "unmatchedGroups": len(codex["gaps"]["unknownTriggerStories"]), "removedStories": len(removed)}
    characters = sorted(({"id": c, "name": d["name"], "lines": d["linesTotal"], "stories": len(d["stories"])} for c, d in chars.items()),
                        key=lambda x: -x["lines"])
    order_path = os.path.join(os.path.dirname(__file__), "area_order.json")
    area_order = common.read_json(order_path)["order"] if os.path.exists(order_path) else {}
    # portraits cropped by portraits.py live next to the viewer (Codex/_cache/portraits/), referenced by relative path
    pidx_path = os.path.join(common.CACHE, "portraits", "index.json")
    portraits = common.read_json(pidx_path)["portraits"] if os.path.exists(pidx_path) else {}
    return {"summary": summary, "characters": characters, "characterNames": {c: d["name"] for c, d in chars.items()},
            "stories": stories, "removedStories": removed, "areaOrder": area_order, "portraits": portraits}


def main():
    codex = common.read_json(os.path.join(common.CODEX, "codex.json"))
    data = json.dumps(adapt(codex), ensure_ascii=False, separators=(",", ":")).replace("</script", "<\\/script")
    with open(os.path.join(os.path.dirname(__file__), "viewer_template.html"), encoding="utf-8") as f:
        tpl = f.read()
    out = os.path.join(common.CACHE, "viewer.html")
    os.makedirs(common.CACHE, exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(tpl.replace("__DATA__", data))
    print("viewer:", out, os.path.getsize(out), "bytes")


if __name__ == "__main__":
    main()
