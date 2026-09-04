# -*- coding: utf-8 -*-
"""Merge per-version localization + structure into Codex/strings.json and Codex/codex.json.
Run: python Codex/build/build_codex.py"""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

DEFAULT_HINT = "main story / tutorial / misc (client-side trigger)"
EVENT_LIBS = {
    "CollectibleBoards": ("CollectibleBoardEventId", "Collectible Board Event",
                          [("StartDialogueRef", "event start"), ("EnterBoardDialogue", "entering board"), ("EndDialogue", "event end")]),
    "Progressions": ("ProgressionEventId", "Mystery / Progression", [("IntroDialogue", "intro"), ("EndDialogue", "outro")]),
    "Leaderboards": ("LeaderboardEventId", "Leaderboard Event", [("EnterBoardDialogue", "entering board"), ("EndDialogue", "event end")]),
}
PHASES = [("AppearActions", "task appears"), ("CompletionActions", "task completed"), ("FinalizationActions", "task finalized")]
SLIDE_PREFIXES = ("_Slides_", "Slideshow", "SlideShow", "Cutscene")


def group_key(lid):
    return re.sub(r"_\d+$", "", lid)


def line_no(lid):
    m = re.search(r"_(\d+)$", lid)
    return int(m.group(1)) if m else 0


def story_defs_from_actions(obj, defs=None):
    """Walk any JSON; every TriggerDialogue yields storyId -> ordered DialogItems keys (first definition wins)."""
    defs = {} if defs is None else defs
    if isinstance(obj, dict):
        td = obj.get("TriggerDialogue")
        if isinstance(td, dict) and td.get("StoryDefinitionId"):
            defs.setdefault(td["StoryDefinitionId"], list((td.get("DialogItems") or {}).keys()))
        for v in obj.values():
            story_defs_from_actions(v, defs)
    elif isinstance(obj, list):
        for v in obj:
            story_defs_from_actions(v, defs)
    return defs


def _td_list(actions):
    for a in actions or []:
        td = (a or {}).get("TriggerDialogue")
        if isinstance(td, dict) and td.get("StoryDefinitionId"):
            yield td


def triggers_from_areas(areas):
    out = []
    for area in areas or []:
        if not isinstance(area, dict):
            continue
        for h in area.get("HotspotsRefs") or []:
            if not isinstance(h, dict):
                continue
            for key, phase in PHASES:
                for td in _td_list(h.get(key)):
                    sid = td["StoryDefinitionId"]
                    out.append((sid, {"kind": "area", "area": area.get("Name"), "areaId": area.get("AreaId"),
                                      "task": h.get("Description"), "hotspotId": h.get("Id"), "phase": phase}))
                    for td2 in _td_list(td.get("CompleteActions")):
                        out.append((td2["StoryDefinitionId"], {"kind": "chained", "after": sid, "area": area.get("Name"), "task": h.get("Description")}))
    return out


def triggers_from_events(events):
    out = []
    if not isinstance(events, dict):
        return out
    for lib, (idkey, label, moments) in EVENT_LIBS.items():
        for ev in events.get(lib) or []:
            if not isinstance(ev, dict):
                continue
            for field, moment in moments:
                v = ev.get(field)
                if not v:
                    continue
                sid = v.get("StoryDefinitionId") if isinstance(v, dict) else v
                if sid:
                    out.append((sid, {"kind": "event", "eventType": label, "event": ev.get("Name"), "eventId": ev.get(idkey), "moment": moment}))
    return out


def triggers_from_items(chains):
    out = []
    for ch in chains or []:
        if not isinstance(ch, dict):
            continue
        for part in ("PrimaryChain", "FallbackChain"):
            for slot in ch.get(part) or []:
                it = (slot or {}).get("Item") or {}
                for td in _td_list(it.get("OnDiscoveredActions")):
                    out.append((td["StoryDefinitionId"], {"kind": "itemDiscovered", "item": it.get("ItemType"), "itemName": it.get("Name"), "chain": ch.get("Name")}))
    return out


def triggers_from_mapping(dialogues):
    out = []
    for m in (dialogues or {}).get("CollectibleDialogueMapping") or []:
        cfg = m.get("ConfigKey")
        for it in m.get("ItemDialogues") or []:
            if it.get("StoryDefinitionId"):
                out.append((it["StoryDefinitionId"], {"kind": "item", "event": cfg, "items": it.get("ItemTypes") or [], "moment": "item discovered"}))
        for de in m.get("DecorationsDialogues") or []:
            if de.get("StoryDefinitionId"):
                out.append((de["StoryDefinitionId"], {"kind": "decoration", "event": cfg, "decoration": de.get("DecorationConfigKey"), "moment": "decoration placed"}))
    return out


def speaker_of(line):
    if line.get("LeftSpeaks"):
        return line.get("LeftCharacter"), line.get("LeftCharacterState")
    if line.get("RightSpeaks"):
        return line.get("RightCharacter"), line.get("RightCharacterState")
    return None, None


def classify_prefix(sid, prefixes):
    best = None
    for p, label in prefixes.items():
        if sid.startswith(p) and (best is None or len(p) > len(best[0])):
            best = (p, label)
    return best[1] if best else DEFAULT_HINT


def loc_text(loc, lid, loc_id):
    """Text of a dialog line in one localization: explicit LocalizationId, the line id itself
    (event dialogues), or the 'Dialogue_' prefixed id (area dialogues)."""
    for cand in (loc_id, lid, "Dialogue_" + lid):
        if cand and cand in loc:
            return loc[cand]
    return None


def load_version(ver):
    """Everything we have for one version: loc dict + structure dicts (None when missing)."""
    d = {"loc": None, "dialogues": None, "areas": None, "events": None, "chains": None}
    p = os.path.join(common.CACHE, "loc", ver + ".json")
    if os.path.exists(p):
        d["loc"] = common.read_json(p)
    sd = os.path.join(common.CACHE, "structure", ver)
    for name, key in (("dialogues.json", "dialogues"), ("areas.json", "areas"), ("events.json", "events"), ("chain_item_odds.json", "chains")):
        fp = os.path.join(sd, name)
        if os.path.exists(fp):
            try:
                d[key] = common.read_json(fp)
            except Exception as ex:      # a broken old dump must not kill the build
                print(f"  {ver}: cannot read {name}: {ex}")
    return d


def build():
    prefixes = common.read_json(os.path.join(os.path.dirname(__file__), "prefixes.json"))
    vers = [v for v in common.versions()
            if os.path.exists(os.path.join(common.CACHE, "loc", v + ".json")) or os.path.exists(os.path.join(common.CACHE, "structure", v))]
    per = {v: load_version(v) for v in vers}
    loc_vers = [v for v in vers if per[v]["loc"] is not None]

    # 1) strings.json — every loc key as runs over versions that have a localization
    keys = set()
    for v in loc_vers:
        keys.update(per[v]["loc"])
    strings = {k: common.runs([(v, per[v]["loc"].get(k)) for v in loc_vers]) for k in sorted(keys)}

    # 2) lines — structure from dialogues.json per version, text from loc (fallback: dialogues.json Text)
    line_meta = collections.defaultdict(dict)     # lid -> ver -> (speaker, state, left, right, locId, text)
    char_names = {}
    for v in vers:
        dl = per[v]["dialogues"]
        if not dl:
            continue
        char_names.update(dl.get("CharacterNames") or {})
        for d in dl.get("Dialogues") or []:
            sp, st = speaker_of(d)
            line_meta[d["DialogItemId"]][v] = (sp, st, d.get("LeftCharacter"), d.get("RightCharacter"), d.get("LocalizationId"), d.get("Text"))
    story_defs_by_ver = {}
    for v in vers:
        defs = {}
        story_defs_from_actions(per[v]["areas"], defs)
        story_defs_from_actions(per[v]["events"], defs)
        story_defs_from_actions(per[v]["chains"], defs)
        story_defs_by_ver[v] = defs
    struct_vers = collections.defaultdict(set)     # lid -> versions where the line is proven by structure
    for lid, meta in line_meta.items():
        struct_vers[lid].update(meta)
    for v, defs in story_defs_by_ver.items():
        for ids in defs.values():
            for lid in ids:
                struct_vers[lid].add(v)
    lines = {}
    for lid in struct_vers:
        meta = line_meta.get(lid, {})
        loc_ids = {m[4] for m in meta.values() if m[4]}
        loc_id = sorted(loc_ids)[0] if loc_ids else None
        text_pairs, seen = [], []
        for v in vers:
            loc = per[v]["loc"]
            has_text_source = loc is not None or per[v]["dialogues"] is not None
            if loc is not None:
                t = loc_text(loc, lid, loc_id)
            else:
                t = meta[v][5] if v in meta else None
            t = t if t else None
            # A line exists in V when V's dialogue structure lists it; for versions where we only have
            # localization (no dialogues.json), a surviving text is the best evidence we have.
            if v in struct_vers[lid] or (per[v]["dialogues"] is None and t is not None):
                seen.append(v)
            # No localization and no dialogues.json for V = no evidence about the text at all:
            # never record a run for it (a gap must not look like a rewrite).
            if has_text_source:
                text_pairs.append((v, t))
        lines[lid] = {
            "text": common.runs(text_pairs),
            "speaker": common.runs([(v, meta[v][0]) for v in vers if v in meta]),
            "state": common.runs([(v, meta[v][1]) for v in vers if v in meta]),
            "left": common.runs([(v, meta[v][2]) for v in vers if v in meta]),
            "right": common.runs([(v, meta[v][3]) for v in vers if v in meta]),
            "locId": loc_id or lid,
            "seen": {"first": seen[0], "last": seen[-1]} if seen else None,
        }

    # 3) stories — ordered ids per version (explicit defs, else naming convention) + triggers per version
    trig_by_ver = {}
    all_sids = set()
    for v in vers:
        t = triggers_from_areas(per[v]["areas"]) + triggers_from_events(per[v]["events"]) \
            + triggers_from_items(per[v]["chains"]) + triggers_from_mapping(per[v]["dialogues"])
        trig_by_ver[v] = t
        all_sids.update(sid for sid, _ in t)
        all_sids.update(story_defs_by_ver[v])
    groups = collections.defaultdict(list)
    for lid in lines:
        groups[group_key(lid)].append(lid)
    for g in groups.values():
        g.sort(key=line_no)

    def lines_by_convention(sid):
        for cand in (sid, re.sub(r"_Dialogue$", "", sid), sid + "_Dialogue"):
            if cand in groups:
                return groups[cand]
        return []

    stories, covered = {}, set()
    for sid in sorted(all_sids):
        trig_sids_here = {v: {s for s, _ in trig_by_ver[v]} for v in vers}
        order_pairs = []
        for v in vers:
            ids = story_defs_by_ver[v].get(sid)
            if ids is None and sid in trig_sids_here[v]:
                ids = lines_by_convention(sid)
            order_pairs.append((v, ids if ids else None))
        trig_runs = {}
        for v in vers:
            for s, t in trig_by_ver[v]:
                if s == sid:
                    trig_runs.setdefault(json.dumps(t, sort_keys=True, ensure_ascii=False), []).append(v)
        triggers = [dict(json.loads(k), **{"from": vs[0], "to": vs[-1]}) for k, vs in trig_runs.items()]
        seen = [v for v, ids in order_pairs if ids]
        for _, ids in order_pairs:
            covered.update(ids or [])
        stories[sid] = {"lines": common.runs(order_pairs), "triggers": triggers,
                        "seen": {"first": seen[0], "last": seen[-1]} if seen else None}
    for g, ids in groups.items():
        if g in stories or any(i in covered for i in ids):
            continue
        seen = [v for v in vers if any(v in struct_vers[i] for i in ids)]
        stories[g] = {"lines": [{"from": seen[0] if seen else vers[0], "value": ids}],
                      "triggers": [{"kind": "unknown", "hint": classify_prefix(g, prefixes)}],
                      "seen": {"first": seen[0], "last": seen[-1]} if seen else None}

    # 4) items / tasks / events text runs
    items, tasks, events_out = {}, {}, {}
    for v in vers:
        for ch in per[v]["chains"] or []:
            if not isinstance(ch, dict):
                continue
            for part in ("PrimaryChain", "FallbackChain"):
                for slot in ch.get(part) or []:
                    it = (slot or {}).get("Item") or {}
                    if it.get("ItemType"):
                        e = items.setdefault(it["ItemType"], {"chain": ch.get("Name"), "_n": [], "_d": []})
                        e["_n"].append((v, it.get("Name")))
                        e["_d"].append((v, it.get("Description")))
        for area in per[v]["areas"] or []:
            if not isinstance(area, dict):
                continue
            for h in area.get("HotspotsRefs") or []:
                if isinstance(h, dict) and h.get("Id"):
                    tasks.setdefault(h["Id"], {"area": area.get("Name"), "_d": []})["_d"].append((v, h.get("Description")))
        ev = per[v]["events"]
        if isinstance(ev, dict):
            for lib, (idkey, label, _) in EVENT_LIBS.items():
                for e in ev.get(lib) or []:
                    if isinstance(e, dict) and e.get(idkey):
                        eo = events_out.setdefault(e[idkey], {"type": label, "_n": [], "_d": []})
                        eo["_n"].append((v, e.get("Name")))
                        eo["_d"].append((v, e.get("Description")))
    for d in items.values():
        d["name"], d["description"] = common.runs(d.pop("_n")), common.runs(d.pop("_d"))
    for d in tasks.values():
        d["description"] = common.runs(d.pop("_d"))
    for d in events_out.values():
        d["name"], d["description"] = common.runs(d.pop("_n")), common.runs(d.pop("_d"))
    # SlideShow / cutscene texts are plain DialogItems in this game (e.g. CBE_..._Slides_02_Dialogue_01,
    # LDE_..._CutsceneDialogue1_Dialogue_01) and therefore already live in `lines`; keep here only
    # slide-looking localization keys that no dialog line references.
    referenced = {l["locId"] for l in lines.values()}
    slides = {k: strings[k] for k in strings if any(p in k for p in SLIDE_PREFIXES) and k not in referenced}

    # 5) characters (from the latest speaker of each line in each story's latest order)
    chars = collections.defaultdict(lambda: {"linesTotal": 0, "stories": set()})
    for sid, s in stories.items():
        for lid in s["lines"][-1]["value"] or []:
            sp = lines.get(lid, {}).get("speaker") or []
            if sp and sp[-1]["value"]:
                chars[sp[-1]["value"]]["linesTotal"] += 1
                chars[sp[-1]["value"]]["stories"].add(sid)
    characters = {c: {"name": char_names.get(c, c), "linesTotal": d["linesTotal"], "stories": sorted(d["stories"])} for c, d in chars.items()}

    gaps = {
        "unknownTriggerStories": sorted(s for s, d in stories.items() if d["triggers"] and d["triggers"][0]["kind"] == "unknown"),
        "referencedWithoutLines": sorted(s for s, d in stories.items() if not any(r["value"] for r in d["lines"])),
        "locMissing": sorted(l for l, d in lines.items() if not any(r["value"] for r in d["text"])),
    }
    codex = {"versions": vers, "characters": characters, "lines": lines, "stories": stories,
             "items": items, "tasks": tasks, "slides": slides, "events": events_out, "gaps": gaps}
    return strings, codex


def summary(strings, codex):
    cur = codex["versions"][-1]
    return {"versions": len(codex["versions"]), "strings": len(strings), "lines": len(codex["lines"]),
            "linesInCurrent": sum(1 for l in codex["lines"].values() if l["seen"] and l["seen"]["last"] == cur),
            "stories": len(codex["stories"]),
            "storiesInCurrent": sum(1 for s in codex["stories"].values() if s["seen"] and s["seen"]["last"] == cur),
            "unknownTrigger": len(codex["gaps"]["unknownTriggerStories"]),
            "referencedWithoutLines": len(codex["gaps"]["referencedWithoutLines"]),
            "locMissing": len(codex["gaps"]["locMissing"]), "characters": len(codex["characters"]),
            "items": len(codex["items"]), "tasks": len(codex["tasks"]), "events": len(codex["events"]), "slides": len(codex["slides"])}


def main():
    strings, codex = build()
    common.write_json(os.path.join(common.CODEX, "strings.json"), strings, compact=True)
    common.write_json(os.path.join(common.CODEX, "codex.json"), codex, compact=True)
    print(json.dumps(summary(strings, codex), indent=1))


if __name__ == "__main__":
    main()
