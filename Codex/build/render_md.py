# -*- coding: utf-8 -*-
"""codex.json -> Codex/md/ (readable encyclopedia). Run: python Codex/build/render_md.py"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402


def slug(name):
    return re.sub(r"[^A-Za-z0-9]+", "-", name or "unnamed").strip("-") or "unnamed"


def latest(run_list):
    return run_list[-1]["value"] if run_list else None


def story_title(sid, story):
    t = story["triggers"][0] if story["triggers"] else {"kind": "unknown"}
    if t["kind"] == "area":
        return f'{t.get("area")}: {t.get("task") or sid}'
    if t["kind"] == "chained":
        return f'{t.get("area")}: {t.get("task") or sid} (follows {t.get("after")})'
    if t["kind"] == "event":
        return f'{t.get("event") or t.get("eventId")} — {t.get("moment")}'
    if t["kind"] == "item":
        return f'{t.get("event")} — item discovered: {", ".join(t.get("items") or []) or sid}'
    if t["kind"] == "itemDiscovered":
        return f'{t.get("itemName")} ({t.get("item")}) discovered'
    if t["kind"] == "decoration":
        return f'{t.get("event")} — decoration {t.get("decoration") or sid}'
    return sid.replace("_", " ")


def trigger_line(t):
    parts = [t["kind"]] + [f"{k}={v}" for k, v in t.items() if k not in ("kind", "from", "to") and v not in (None, [], "")]
    return f'- trigger: {", ".join(str(p) for p in parts)} (versions {t.get("from")}–{t.get("to")})'


def story_md(sid, story, codex):
    out = [f"### {story_title(sid, story)}",
           f"`{sid}`" + (f' · seen {story["seen"]["first"]}–{story["seen"]["last"]}' if story.get("seen") else "")]
    out += [trigger_line(t) for t in story["triggers"]]
    out.append("")
    ids = latest(story["lines"]) or []
    if not ids:
        out.append("_Referenced by the game, but no lines matched this id._")
    for lid in ids:
        l = codex["lines"].get(lid)
        if not l:
            out.append(f"- `{lid}` (no data)")
            continue
        text = latest(l["text"])
        if not text:
            continue                       # silent beat (camera / animation)
        sp, st = latest(l["speaker"]), latest(l["state"])
        who = (codex["characters"].get(sp, {}).get("name", sp) if sp else "—").upper()
        line = f"**{who}**" + (f" ({st})" if st and st not in ("Default", "NoChange") else "") + f": {text}"
        prev = [r for r in l["text"] if r["value"] not in (None, "")]
        if len(prev) > 1:
            hist = "; ".join(f'~~{p["value"]}~~ (until {q["from"]})' for p, q in zip(prev[:-1], prev[1:]))
            line += f"  \n  _earlier:_ {hist}"
        out.append(line)
    out.append("")
    return "\n".join(out)


def main():
    codex = common.read_json(os.path.join(common.CODEX, "codex.json"))
    md_root = os.path.join(common.CODEX, "md")
    by_area, by_event, unknown, removed = collections.defaultdict(list), collections.defaultdict(list), [], []
    cur = codex["versions"][-1]
    for sid, s in sorted(codex["stories"].items()):
        if s.get("seen") and s["seen"]["last"] != cur:
            removed.append(sid)
            continue
        t = s["triggers"][0] if s["triggers"] else {"kind": "unknown"}
        if t["kind"] in ("area", "chained"):
            by_area[t.get("area") or "Unknown area"].append(sid)
        elif t["kind"] in ("event", "item", "decoration"):
            by_event[t.get("event") or "Unknown event"].append(sid)
        elif t["kind"] == "itemDiscovered":
            by_event["Item discovery dialogues"].append(sid)
        else:
            unknown.append(sid)

    def write(rel, title, sids, intro=""):
        p = os.path.join(md_root, rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        body = [f"# {title}", intro, ""] + [story_md(s, codex["stories"][s], codex) for s in sids]
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(body))
        return rel

    order_path = os.path.join(os.path.dirname(__file__), "area_order.json")
    area_order = common.read_json(order_path)["order"] if os.path.exists(order_path) else {}
    index = ["# Dialogue Codex — index", f"Versions: {', '.join(codex['versions'])}", "",
             "## Areas (progression order = wiki orderingIndex)"]
    for area, sids in sorted(by_area.items(), key=lambda kv: (area_order.get(kv[0], 10 ** 6), kv[0])):
        idx = area_order.get(area)
        prefix = f"#{int(idx)} " if idx is not None else ""
        index.append(f"- {prefix}[{area}](areas/{slug(area)}.md) ({len(sids)} stories)")
        write(f"areas/{slug(area)}.md", area, sids)
    index.append("\n## Events")
    for ev, sids in sorted(by_event.items()):
        index.append(f"- [{ev}](events/{slug(ev)}.md) ({len(sids)} stories)")
        write(f"events/{slug(ev)}.md", ev, sids)
    index.append("\n## Characters")
    for cid, c in sorted(codex["characters"].items(), key=lambda kv: -kv[1]["linesTotal"]):
        sids = [s for s in c["stories"] if s in codex["stories"]]
        index.append(f"- [{c['name']}](characters/{slug(c['name'])}.md) ({c['linesTotal']} lines, {len(sids)} stories)")
        write(f"characters/{slug(c['name'])}.md", c["name"], sids, f"Every story in which {c['name']} speaks.")
    index.append("\n## Misc")
    index.append(f"- [Stories without a known trigger](misc/unknown-trigger.md) ({len(unknown)})")
    write("misc/unknown-trigger.md", "Stories without a known trigger", unknown, "Trigger hints are prefix guesses — see build/prefixes.json.")
    index.append(f"- [Removed from the game](misc/removed.md) ({len(removed)})")
    write("misc/removed.md", "Stories removed from the game", removed, "Present in an older version, absent from the current one.")
    # Discord screenshots (OCR) — lower-trust source, kept apart from game-data stories
    dd_path = os.path.join(common.CODEX, "discord_dialogues.json")
    if os.path.exists(dd_path):
        dd = common.read_json(dd_path)
        index.append("\n## Discord screenshots (OCR, lower trust)")
        for th in dd["threads"]:
            if not th["lines"]:
                continue
            name = th.get("name") or th["id"]
            rel = f"discord/{slug(name)}.md"
            p = os.path.join(md_root, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            body = [f"# {name}", f"Discord thread `{th['id']}`, {len(th['lines'])} dialogue screenshots in message order. "
                    "Text is Windows OCR of player screenshots: speaker plates are reliable, wording may carry OCR slips.", ""]
            last_day = None
            for ln in th["lines"]:
                day = (ln.get("timestamp") or "")[:10]
                if day != last_day:
                    body.append(f"\n### {day} (posted by {ln.get('author')})\n")
                    last_day = day
                who = codex["characters"].get(ln["speaker"], {}).get("name", ln["speaker"])
                body.append(f"**{str(who).upper()}**: {ln['text']}")
            with open(p, "w", encoding="utf-8", newline="\n") as f:
                f.write("\n".join(body) + "\n")
            index.append(f"- [{name}]({rel}) ({len(th['lines'])} lines)")
        if dd.get("unknownSpeakersToReview"):
            index.append("- Unknown speaker plates to review: " + ", ".join(f"{u['name']} ({u['count']})" for u in dd["unknownSpeakersToReview"][:30]))
    with open(os.path.join(md_root, "INDEX.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(index) + "\n")
    print(f"md: {len(by_area)} areas, {len(by_event)} events, {len(codex['characters'])} characters, {len(unknown)} unknown, {len(removed)} removed")


if __name__ == "__main__":
    main()
