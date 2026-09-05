# -*- coding: utf-8 -*-
"""Event reruns: which events ran again, and whether their dialogues stayed identical, got cosmetic fixes
(typos, punctuation, formatting) or were rewritten. Run: python Codex/build/reruns.py
Input : Codex/codex.json (+ Codex/build/rerun_notes.json, hand-written notes per family)
Output: Codex/reruns.json, Codex/md/misc/reruns.md

Family = event key without its run tag (CBE_MaddieInParis2025 / CBE_MaddieInParis -> MaddieInParis;
CBE_TheGreatEscape / CBE_TheGreatEscape_02; CBE_AmeliaBoulton2024 / 2024B). Stories of two runs are paired by
their id with the run's event key removed (the "suffix", e.g. _Intro_Dialogue). Per pair the verdict is
  identical  - same line texts
  cosmetic   - same number of lines and every line >= COSMETIC_RATIO similar (difflib) -> not a new version
  rewritten  - anything else (diff listed)
The same rule classifies text changes of one line across game versions (used by the renderers)."""
import collections
import difflib
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

COSMETIC_RATIO = 0.90      # per-line similarity at/above which a change is a typo/punctuation fix, not a new version
PAIR_MIN = 0.50            # whole-story similarity below which two stories of consecutive runs are unrelated
TYPE_PREFIX = re.compile(r"^(SP|CBE|LDE|SE|LS|LC|DE|MMM|MME|PE|LBE|SLBE|SBE|MBE|CSE|DTOB|EP1|AAR)_")
RUN_TAG = re.compile(r"(20\d\d[A-Z]?|_0\d|_v\d|B)$")


def norm(text):
    t = (text or "").replace("’", "'").replace("“", '"').replace("”", '"').replace("…", "...")
    t = re.sub(r"<[^>]+>", "", t)                  # <i>...</i> markup
    return re.sub(r"\s+", " ", t).strip()


def similarity(a, b):
    a, b = norm(a), norm(b)
    if a == b:
        return 1.0
    return difflib.SequenceMatcher(None, a, b).ratio()


def classify_change(old, new):
    """cosmetic | rewritten for one line's text change (identical after normalisation counts as cosmetic)."""
    return "cosmetic" if similarity(old, new) >= COSMETIC_RATIO else "rewritten"


def family_of(event_key):
    """('MaddieInParis', '2025') for CBE_MaddieInParis2025; run tag '' when none."""
    core = TYPE_PREFIX.sub("", event_key)
    m = RUN_TAG.search(core)
    if m and len(core) - len(m.group(0)) >= 3:
        return core[: m.start()], m.group(0).lstrip("_")
    return core, ""


def latest(runs):
    """Last NON-EMPTY value: content removed from the game vanishes from newer localizations, so the last run of a
    removed line is None while its real text sits one run earlier."""
    for r in reversed(runs or []):
        if r["value"] not in (None, "", []):
            return r["value"]
    return None


def story_texts(codex, sid):
    ids = latest(codex["stories"][sid]["lines"]) or []
    out = []
    for lid in ids:
        line = codex["lines"].get(lid) or {}
        text = latest(line.get("text") or [])
        if text:
            out.append((latest(line.get("speaker") or []), text))
    return out


def compare(codex, sid_a, sid_b):
    ta, tb = story_texts(codex, sid_a), story_texts(codex, sid_b)
    if [t for _, t in ta] == [t for _, t in tb]:
        return "identical", 1.0, []
    if len(ta) == len(tb):
        sims = [similarity(x[1], y[1]) for x, y in zip(ta, tb)]
        if all(s >= COSMETIC_RATIO for s in sims):
            return "cosmetic", min(sims), [{"i": i, "a": x[1], "b": y[1]} for i, (x, y) in enumerate(zip(ta, tb)) if x[1] != y[1]]
    # rewritten: align with difflib on line texts
    diff = []
    sm = difflib.SequenceMatcher(None, [norm(t) for _, t in ta], [norm(t) for _, t in tb])
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            continue
        diff.append({"op": tag, "a": [f"{s or '—'}: {t}" for s, t in ta[i1:i2]], "b": [f"{s or '—'}: {t}" for s, t in tb[j1:j2]]})
    ratio = sm.ratio()
    return "rewritten", ratio, diff


def split_sid(sid):
    """'CBE_MaddieInParis2025_Intro_Dialogue' -> family 'CBE_MaddieInParis', run 'CBE_MaddieInParis2025', suffix 'Intro_Dialogue'.
    The event type stays part of the family: LC_Autumn (fishing) and LS_Autumn (birding) are different events."""
    if not TYPE_PREFIX.match(sid):
        return None
    tokens = sid.split("_")
    if len(tokens) < 3:
        return None
    base, _tag = family_of(tokens[0] + "_" + tokens[1])
    return tokens[0] + "_" + base, tokens[0] + "_" + tokens[1], "_".join(tokens[2:])


def build(codex):
    families = collections.defaultdict(lambda: collections.defaultdict(dict))   # family -> run -> {suffix: sid}
    for sid in codex["stories"]:
        parts = split_sid(sid)
        if parts:
            fam, run, suf = parts
            families[fam][run][suf] = sid
    notes_path = os.path.join(os.path.dirname(__file__), "rerun_notes.json")
    notes = common.read_json(notes_path) if os.path.exists(notes_path) else {}

    out = []
    for base, runs in sorted(families.items()):
        if len(runs) < 2:
            continue
        keys = sorted(runs, key=lambda k: (min((codex["stories"][s].get("seen") or {}).get("first", "") for s in runs[k].values()), k))
        run_rows = []
        for k in keys:
            sids = list(runs[k].values())
            seen = [codex["stories"][s].get("seen") for s in sids if codex["stories"][s].get("seen")]
            name = next((t.get("event") for s in sids for t in codex["stories"][s]["triggers"] if t.get("event")), None)
            run_rows.append({"eventKey": k, "name": name, "stories": len(sids),
                             "first": min(x["first"] for x in seen) if seen else None, "last": max(x["last"] for x in seen) if seen else None})
        # Pair stories of consecutive runs by CONTENT, not by id suffix: reruns renumber item slots
        # (ArcDeTriomphe_05 of 2025 and of 2026 are unrelated texts). Greedy best-match on the joined text;
        # a best match below PAIR_MIN is "new" content, not a rewrite.
        pairs = []
        for a, b in zip(keys, keys[1:]):
            ids_a, ids_b = list(runs[a].values()), list(runs[b].values())
            text = {s: norm(" ".join(t for _, t in story_texts(codex, s))) for s in ids_a + ids_b}
            cand = []
            for sa in ids_a:
                for sb in ids_b:
                    if text[sa] and text[sb]:
                        cand.append((difflib.SequenceMatcher(None, text[sa], text[sb]).ratio(), sa, sb))
            cand.sort(reverse=True)
            used_a, used_b = set(), set()
            for ratio, sa, sb in cand:
                if ratio < PAIR_MIN or sa in used_a or sb in used_b:
                    continue
                used_a.add(sa)
                used_b.add(sb)
                verdict, r2, diff = compare(codex, sa, sb)
                pairs.append({"a": sa, "b": sb, "verdict": verdict, "similarity": round(max(ratio, r2), 3), "diff": diff})
            for sa in ids_a:
                if sa not in used_a:
                    pairs.append({"a": sa, "b": None, "verdict": "dropped in " + b, "similarity": None, "diff": []})
            for sb in ids_b:
                if sb not in used_b:
                    pairs.append({"a": None, "b": sb, "verdict": "new in " + b, "similarity": None, "diff": []})
        counts = collections.Counter(p["verdict"] if p["verdict"] in ("identical", "cosmetic", "rewritten") else p["verdict"].split(" ")[0] for p in pairs)
        verdict = "rewritten" if counts["rewritten"] or counts["new"] else ("cosmetic" if counts["cosmetic"] else ("identical" if counts["identical"] else "unpaired"))
        out.append({"family": base, "verdict": verdict, "counts": dict(counts), "runs": run_rows, "pairs": pairs, "note": notes.get(base)})
    return out


def render_md(fams):
    md = ["# Event reruns", "",
          f"Families of events that ran more than once. Verdict per story pair: identical / cosmetic (every line ≥ {int(COSMETIC_RATIO * 100)} % similar — typo, punctuation, markup; not a new version) / rewritten. Notes are hand-written (`build/rerun_notes.json`).", ""]
    for verdict in ("rewritten", "cosmetic", "identical", "unpaired"):
        group = [f for f in fams if f["verdict"] == verdict]
        if not group:
            continue
        md.append(f"## {verdict.capitalize()} ({len(group)} families)")
        for f in group:
            runs = " → ".join(f"{r['eventKey']} ({r['name'] or '?'}, {r['first']}–{r['last']}, {r['stories']} stories)" for r in f["runs"])
            md.append(f"\n### {f['family']}\n{runs}  ")
            md.append("Pairs: " + ", ".join(f"{k} {v}" for k, v in f["counts"].items()))
            if f.get("note"):
                md.append(f"\n> **Note:** {f['note']}")
            for p in f["pairs"]:
                if p["verdict"] == "rewritten":
                    md.append(f"\n**`{p['a']}` → `{p['b']}`** — rewritten (similarity {p['similarity']})")
                    for d in p["diff"][:12]:
                        md.append(f"- {d['op']}:")
                        for x in d["a"]:
                            md.append(f"  - ~~{x}~~")
                        for x in d["b"]:
                            md.append(f"  - {x}")
                elif p["verdict"] == "cosmetic" and p["diff"]:
                    md.append(f"\n**`{p['a']}` → `{p['b']}`** — cosmetic: " + "; ".join(f"“{norm(d['a'])}” → “{norm(d['b'])}”" for d in p["diff"][:6]))
            new = [p["b"] for p in f["pairs"] if p["verdict"].startswith("new")]
            dropped = [p["a"] for p in f["pairs"] if p["verdict"].startswith("dropped")]
            if new:
                md.append(f"\nNew stories in the later run ({len(new)}): " + ", ".join(f"`{s}`" for s in new[:15]) + (" …" if len(new) > 15 else ""))
            if dropped:
                md.append(f"\nStories not carried over ({len(dropped)}): " + ", ".join(f"`{s}`" for s in dropped[:15]) + (" …" if len(dropped) > 15 else ""))
        md.append("")
    return "\n".join(md)


def main():
    codex = common.read_json(os.path.join(common.CODEX, "codex.json"))
    fams = build(codex)
    common.write_json(os.path.join(common.CODEX, "reruns.json"), fams)
    p = os.path.join(common.CODEX, "md", "misc", "reruns.md")
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(render_md(fams))
    c = collections.Counter(f["verdict"] for f in fams)
    print(f"families with >1 run: {len(fams)} {dict(c)}")
    for f in fams:
        print(f"  {f['verdict']:9s} {f['family']:28s} runs={[r['eventKey'] for r in f['runs']]} pairs={f['counts']}")


if __name__ == "__main__":
    main()
