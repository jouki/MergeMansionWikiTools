# -*- coding: utf-8 -*-
"""Dialogue timelines: where on the player's progress axis (0 = start, 1 = end) each story of an area / event fires,
per game version, with the evidence behind every position. Design: _CONTEXT/_plans/2026-09-05-dialogue-timelines-design.md
Run: python Codex/build/timelines.py   (render_md.py calls the same build + write)
Output: Codex/timelines.json, Codex/md/timelines/<scope>.md (+ INDEX.md)

Clocks:  areas  — hotspot unlock tree (UnlockingParentRefs) + Order -> exact topological rank
         events — Start/Enter/End dialogues fixed; decoration slot -> reward level (RequiredPoints / max);
                  item dialogues -> chain index in the event's chain list + item level in the chain (estimate)"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

PHASES = [(0.05, "intro"), (0.35, "early"), (0.65, "mid"), (0.95, "late"), (1.01, "outro")]
EVENT_LIBS = {"CollectibleBoards": "CollectibleBoardEventId", "Progressions": "ProgressionEventId", "Leaderboards": "LeaderboardEventId"}
LEVEL_FIELDS = ("LevelRefs", "FreeEventLevelRefs", "Track1EventLevelRefs", "Track2EventLevelRefs", "BonusEventLevelRefs", "RecurringLevelRefs")


def phase_of(pos):
    for limit, name in PHASES:
        if pos < limit:
            return name
    return "outro"


def latest(runs):
    for r in reversed(runs or []):
        if r["value"] not in (None, "", []):
            return r["value"]
    return None


# ── areas ──────────────────────────────────────────────────────────────────
def topo_ranks(hotspots):
    """Kahn's algorithm over UnlockingParentRefs, ties by (Order, config index) -> {hotspotId: rank 1..N}."""
    by_id = {h["Id"]: (i, h) for i, h in enumerate(hotspots) if isinstance(h, dict) and h.get("Id")}
    parents = {hid: [p for p in (h.get("UnlockingParentRefs") or []) if isinstance(p, str) and p in by_id] for hid, (_, h) in by_id.items()}
    done, ranks, rank = set(), {}, 0
    while len(done) < len(by_id):
        ready = [hid for hid in by_id if hid not in done and all(p in done for p in parents[hid])]
        if not ready:                                   # cycle / foreign parents: release the rest in config order
            ready = [hid for hid in by_id if hid not in done]
        ready.sort(key=lambda hid: (by_id[hid][1].get("Order") or 0, by_id[hid][0]))
        for hid in ready:
            rank += 1
            ranks[hid] = rank
            done.add(hid)
    return ranks, parents


def area_timeline(area):
    hotspots = [h for h in (area.get("HotspotsRefs") or []) if isinstance(h, dict)]
    if not hotspots:
        return []
    ranks, parents = topo_ranks(hotspots)
    n = max(ranks.values()) if ranks else 1
    desc = {h["Id"]: h.get("Description") for h in hotspots if h.get("Id")}
    entries = []
    for h in hotspots:
        hid = h.get("Id")
        if not hid:
            continue
        r = ranks[hid]
        after = ", ".join(f"'{desc.get(p) or p}'" for p in parents[hid][:3]) or "start of the area"
        for key, label, off in (("AppearActions", "task appears", -0.3), ("CompletionActions", "task completed", 0.0), ("FinalizationActions", "task finalized", 0.3)):
            for act in h.get(key) or []:
                td = (act or {}).get("TriggerDialogue")
                if not (isinstance(td, dict) and td.get("StoryDefinitionId")):
                    continue
                pos = min(1.0, max(0.0, (r + off) / n))
                entries.append({"story": td["StoryDefinitionId"], "position": round(pos, 3), "phase": phase_of(pos), "kind": "task",
                                "evidence": f"task {r}/{n} '{h.get('Description')}' {label}; after {after}"})
                for a2 in td.get("CompleteActions") or []:
                    td2 = (a2 or {}).get("TriggerDialogue")
                    if isinstance(td2, dict) and td2.get("StoryDefinitionId"):
                        entries.append({"story": td2["StoryDefinitionId"], "position": round(min(1.0, pos + 0.01), 3), "phase": phase_of(pos),
                                        "kind": "task", "evidence": f"right after {td['StoryDefinitionId']} (same task)"})
    entries.sort(key=lambda e: e["position"])
    return entries


# ── events ─────────────────────────────────────────────────────────────────
def event_levels(ev):
    levels = []
    for f in LEVEL_FIELDS:
        for l in ev.get(f) or []:
            if isinstance(l, dict) and l.get("RequiredPoints") is not None:
                levels.append(l)
    levels.sort(key=lambda l: l["RequiredPoints"])
    return levels


def decoration_levels(levels):
    """DecorationId -> (level index 1-based, RequiredPoints)."""
    out = {}
    for i, l in enumerate(levels, 1):
        for r in l.get("Rewards") or []:
            d = (r.get("RewardDecoration") or {}).get("DecorationRef") or {}
            if d.get("DecorationId"):
                out.setdefault(d["DecorationId"], (i, l["RequiredPoints"]))
    return out


def event_chains(chains, key):
    """Chains of one event in config order (multi-item chains only) -> [(chainName, [(ItemType, level)], maxLevel)]."""
    out = []
    for ch in chains or []:
        ck = str(ch.get("ConfigKey") or "")
        if not ck.startswith(key + "_"):
            continue
        items = [((s.get("Item") or {}).get("ItemType"), (s.get("Item") or {}).get("LevelNumber") or 1) for s in ch.get("PrimaryChain") or []]
        items = [(t, lv) for t, lv in items if t]
        if items and not re.search(r"(Entrance|Exit)$", ck):        # single-item objects (Clean Cell, Post Box) count too
            out.append((ch.get("Name") or ck, items, max(lv for _, lv in items), ck))
    return out


def infer_from_story_id(sid, key, ch):
    """CBE_TheGreatEscape_Tools1_Dialogue -> chain whose config key ends with 'Tools', level 1 (stories without a mapping row)."""
    m = re.match(re.escape(key) + r"_([A-Za-z]+?)(\d+)?_Dialogue$", sid) or re.match(re.escape(key) + r"_([A-Za-z]+?)(\d+)?$", sid)
    if not m:
        return None
    name, lv = m.group(1).lower(), int(m.group(2) or 1)
    for ci, (cname, items, maxlv, ck) in enumerate(ch):
        if ck.lower().endswith("_" + name) or cname.replace(" ", "").lower() == name:
            return ci, cname, min(lv, maxlv), maxlv
    return None


def event_timeline(ev, key, name, chains, story_triggers):
    """story_triggers: [(sid, trigger)] of the codex whose eventKey/eventId equals key (item / decoration / event kinds)."""
    entries, unplaced = [], []
    levels = event_levels(ev)
    max_pts = max((l["RequiredPoints"] for l in levels), default=0) or 1
    deco_lv = decoration_levels(levels)
    ch = event_chains(chains, key)
    item_pos = {}
    for ci, (cname, items, maxlv, _ck) in enumerate(ch):
        for itype, lv in items:
            item_pos[itype] = (0.05 + 0.9 * ((ci + lv / maxlv) / len(ch)), f"chain '{cname}' {ci + 1}/{len(ch)}, item level {lv}/{maxlv}")
    fixed = {}
    for field, pos, label in (("StartDialogueRef", 0.0, "event start"), ("IntroDialogue", 0.0, "event intro"), ("EnterBoardDialogue", 0.02, "entering the board"),
                              ("EndDialogue", 1.0, "event end")):
        v = ev.get(field)
        sid = v.get("StoryDefinitionId") if isinstance(v, dict) else v
        if sid:
            fixed[sid] = (pos, label)
    seen = set()
    for sid, t in story_triggers:
        if sid in seen:
            continue
        if sid in fixed:
            pos, label = fixed[sid]
            entries.append({"story": sid, "position": pos, "phase": phase_of(pos), "kind": "fixed", "evidence": label})
        elif t["kind"] == "decoration" and t.get("decoration") in deco_lv:
            i, pts = deco_lv[t["decoration"]]
            pos = 0.05 + 0.9 * (pts / max_pts)
            entries.append({"story": sid, "position": round(pos, 3), "phase": phase_of(pos), "kind": "decoration",
                            "evidence": f"decoration {t['decoration'].split('_')[-1]} = reward level {i}/{len(levels)} ({pts}/{max_pts} points)"})
        elif t["kind"] == "item" and any(k in item_pos for k in (t.get("itemKeys") or [])):
            k = next(k for k in t["itemKeys"] if k in item_pos)
            pos, ev_txt = item_pos[k]
            entries.append({"story": sid, "position": round(pos, 3), "phase": phase_of(pos), "kind": "item",
                            "evidence": f"item '{(t.get('items') or [k])[0]}': {ev_txt} (estimate)"})
        else:
            inf = infer_from_story_id(sid, key, ch)
            if inf:
                ci, cname, lv, maxlv = inf
                pos = 0.05 + 0.9 * ((ci + lv / maxlv) / len(ch))
                entries.append({"story": sid, "position": round(pos, 3), "phase": phase_of(pos), "kind": "item",
                                "evidence": f"chain '{cname}' {ci + 1}/{len(ch)}, level {lv}/{maxlv} — inferred from the story id (estimate)"})
            else:
                unplaced.append(sid)
        seen.add(sid)
    for sid, (pos, label) in fixed.items():           # start/end dialogues the codex has no trigger row for
        if sid not in seen:
            entries.append({"story": sid, "position": pos, "phase": phase_of(pos), "kind": "fixed", "evidence": label})
    entries.sort(key=lambda e: (e["position"], e["story"]))
    return entries, unplaced


# ── build ──────────────────────────────────────────────────────────────────
def build(codex):
    vers = codex["versions"]
    triggers_by_key = collections.defaultdict(list)        # eventKey/eventId -> [(sid, trigger)]
    for sid, st in codex["stories"].items():
        for t in st["triggers"]:
            k = t.get("eventKey") or t.get("eventId")
            if k:
                triggers_by_key[k].append((sid, t))
    scopes = {}
    for v in vers:
        sd = os.path.join(common.CACHE, "structure", v)
        areas = _read(os.path.join(sd, "areas.json"))
        events = _read(os.path.join(sd, "events.json"))
        chains = _read(os.path.join(sd, "chain_item_odds.json"))
        for area in areas or []:
            if not (isinstance(area, dict) and area.get("Name")):
                continue
            tl = area_timeline(area)
            if tl:
                name = _area_name(codex, area)
                sc = scopes.setdefault("area:" + name, {"name": name, "kind": "area", "versions": {}, "unplaced": {}})
                sc["versions"][v] = tl
        if isinstance(events, dict):
            for lib, idkey in EVENT_LIBS.items():
                for ev in events.get(lib) or []:
                    if not (isinstance(ev, dict) and ev.get(idkey)):
                        continue
                    key = ev[idkey]
                    trig = triggers_by_key.get(key) or triggers_by_key.get(ev.get("NameLocId")) or triggers_by_key.get(key + "_Name") or []
                    name = next((t.get("event") for _, t in trig if t.get("event")), None) or ev.get("Name") or key
                    tl, unplaced = event_timeline(ev, key, name, chains, trig)
                    if tl or unplaced:
                        sc = scopes.setdefault("event:" + key, {"name": name, "kind": "event", "versions": {}, "unplaced": {}})
                        sc["versions"][v] = tl
                        sc["unplaced"][v] = unplaced
    for sc in scopes.values():
        sc["changes"] = _changes(sc["versions"])
    return scopes


def _area_name(codex, area):
    aid = area.get("AreaId")
    for st in codex["stories"].values():
        for t in st["triggers"]:
            if t.get("areaId") == aid and t.get("area"):
                return t["area"]
    return (area.get("Name") or aid).strip()


def _changes(versions):
    out = []
    keys = sorted(versions)
    for a, b in zip(keys, keys[1:]):
        pa = {e["story"]: e["position"] for e in versions[a]}
        pb = {e["story"]: e["position"] for e in versions[b]}
        added, removed = sorted(set(pb) - set(pa)), sorted(set(pa) - set(pb))
        moved = [{"story": s, "from": pa[s], "to": pb[s]} for s in sorted(set(pa) & set(pb)) if abs(pa[s] - pb[s]) >= 0.05]
        if added or removed or moved:
            out.append({"from": a, "to": b, "added": added, "removed": removed, "moved": moved})
    return out


def _read(p):
    try:
        return common.read_json(p) if os.path.exists(p) else None
    except Exception:
        return None


# ── render ─────────────────────────────────────────────────────────────────
def slug(name):
    return re.sub(r"[^A-Za-z0-9]+", "-", name or "unnamed").strip("-") or "unnamed"


def story_lines(codex, sid, n=2):
    st = codex["stories"].get(sid)
    if not st:
        return []
    out = []
    for lid in (latest(st["lines"]) or []):
        l = codex["lines"].get(lid) or {}
        t = latest(l.get("text") or [])
        if t:
            sp = latest(l.get("speaker") or [])
            out.append(f"{codex['characters'].get(sp, {}).get('name', sp) if sp else '—'}: {t}")
        if len(out) >= n:
            break
    return out


def write(scopes, codex, md_root):
    common.write_json(os.path.join(common.CODEX, "timelines.json"), scopes, compact=True)
    tdir = os.path.join(md_root, "timelines")
    os.makedirs(tdir, exist_ok=True)
    index = ["# Dialogue timelines", "", "Position 0 = start of the area/event, 1 = end. Areas are exact (task unlock tree); event positions are "
             "estimates from reward levels (decorations) and chain/item levels (item dialogues). Evidence on every line. One section per game version "
             "the structure was dumped in; 'Changes' lists what moved between versions.", "", "## Areas"]
    for key, sc in sorted(scopes.items(), key=lambda kv: (kv[1]["kind"], kv[1]["name"])):
        vers = sorted(sc["versions"])
        rel = f"timelines/{sc['kind']}-{slug(sc['name'] if sc['kind'] == 'area' else key.split(':', 1)[1])}.md"
        body = [f"# {sc['name']} — dialogue timeline ({sc['kind']})", f"`{key}` · versions {vers[0]}–{vers[-1]} ({len(vers)})", ""]
        cur = vers[-1]
        body.append(f"## Order in {cur}")
        for e in sc["versions"][cur]:
            lines = story_lines(codex, e["story"])
            body.append(f"- **{int(round(e['position'] * 100)):3d} %** · {e['phase']:5s} · `{e['story']}` — {e['evidence']}")
            for l in lines:
                body.append(f"    - {l}")
        if sc.get("unplaced", {}).get(cur):
            body.append(f"\nNot placeable in {cur} ({len(sc['unplaced'][cur])}): " + ", ".join(f"`{s}`" for s in sc["unplaced"][cur][:20]))
        if sc["changes"]:
            body.append("\n## Changes across versions")
            for c in sc["changes"]:
                body.append(f"- **{c['from']} → {c['to']}**: " + "; ".join(x for x in (
                    f"added {len(c['added'])} ({', '.join('`' + s + '`' for s in c['added'][:6])}{'…' if len(c['added']) > 6 else ''})" if c["added"] else "",
                    f"removed {len(c['removed'])} ({', '.join('`' + s + '`' for s in c['removed'][:6])}{'…' if len(c['removed']) > 6 else ''})" if c["removed"] else "",
                    f"moved {len(c['moved'])}" if c["moved"] else "") if x))
        with open(os.path.join(md_root, rel), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(body) + "\n")
        if sc["kind"] == "event" and index[-1] == "## Areas" or (sc["kind"] == "event" and "## Events" not in index):
            index.append("\n## Events")
        n_cur = len(sc["versions"][cur])
        index.append(f"- [{sc['name']}]({rel.split('/', 1)[1]}) — {n_cur} placed stories in {cur}, {len(vers)} versions" +
                     (f", {len(sc['changes'])} change sets" if sc["changes"] else ""))
    with open(os.path.join(tdir, "INDEX.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(index) + "\n")
    return len(scopes)


if __name__ == "__main__":
    codex = common.read_json(os.path.join(common.CODEX, "codex.json"))
    scopes = build(codex)
    n = write(scopes, codex, os.path.join(common.CODEX, "md"))
    kinds = collections.Counter(s["kind"] for s in scopes.values())
    placed = sum(len(s["versions"][max(s["versions"])]) for s in scopes.values())
    print(f"timelines: {n} scopes {dict(kinds)}, {placed} placed stories in latest versions, "
          f"{sum(len(s['changes']) for s in scopes.values())} change sets")
