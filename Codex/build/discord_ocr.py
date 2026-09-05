# -*- coding: utf-8 -*-
"""Turn raw OCR of Discord screenshots into dialogue lines with provenance.
Run: python Codex/build/discord_ocr.py <channelId>
Input : _cache/discord/<channelId>/ocr.jsonl (OcrHarness), images_index.json (downloader), threads.json (inventory)
Output: Codex/discord_dialogues.json  — per thread: ordered dialogue screenshots {speaker, text, message, timestamp, file}
        _cache/discord/<channelId>/ocr_classified.json — every image with its class (dialogue / tasks / other) for auditing

Game dialogue layout (phone screenshots, any resolution): a name plate (1–3 capitalised words) sits just above
a speech box with one to four lines of sentence text, both in the lower part of the screen. Everything is expressed
relative to the image height so 1125x2436 and 1170x780 crops behave alike."""
import collections
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

UI_WORDS = {"skip", "continue", "show", "tasks", "play", "claim", "ok", "close", "next", "collect", "go", "start", "area complete"}
UI_PREFIX = ("contin", "skip", "tap to", "area complete")          # OCR reads "Continue" as "Continuo" etc.
NAME_RE = re.compile(r"^[A-Z][A-Za-z'’.\-]{1,20}( [A-Z][A-Za-z'’.\-]{1,20}){0,2}$")


# Name plates show the in-game display name; the codex keys characters by DialogCharacterType.
SPEAKER_ALIASES = {"Ursula": "Grandma", "Grandma Ursula": "Grandma", "Julius": "AntiqueDealer", "Antique Dealer": "AntiqueDealer"}
# UI headings that also sit above a sentence (popups, shop, area lock) — never a speaker.
UI_HEADINGS = {"area locked", "locked", "daily deals", "get", "album", "speed up", "sell", "shop", "notes", "toolbox",
               "tasks", "settings", "inventory", "rewards", "offer", "special offer", "level up", "new area", "info",
               "area complete", "collection", "storage", "garage", "energy", "coins", "gems", "buy", "watch ad"}


def is_ui(text):
    t = text.strip().lower()
    return t in UI_WORDS or t.startswith(UI_PREFIX) or bool(re.fullmatch(r"[\d/:%.,\s]+", t))


def is_sentence(text):
    t = text.strip()
    return len(t) >= 8 and " " in t and re.search(r"[a-z]{2,}", t) is not None


# Speakers seen on plates that are not (or no longer) DialogCharacterType entries in the codex:
# the unnamed Butler of the 2022–2023 mansion story, event guest Fiona DuVal, and talking pets.
EXTRA_SPEAKERS = {"Butler", "Fiona DuVal", "Sir Fluffsalot", "Gizmo"}


def clean_text(text):
    """Drop the fragment of the Continue/Skip button that OCR glues to the last text line ("...chance Cont")."""
    t = re.sub(r"\s+(Co|Con|Cont|Conti|Contin|Continu|Cor|Sk|Ski|Skip)\s*[»>)]*$", "", text.strip())
    return t.strip()


def canonical_speaker(name):
    return SPEAKER_ALIASES.get(name.strip(), name.strip())


def load_ocr(base):
    out = {}
    p = os.path.join(base, "ocr.jsonl")
    with open(p, encoding="utf-8") as f:
        for line in f:
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            out[r["file"]] = r
    return out


def classify(rec, cfg, known_names=frozenset()):
    """Return (kind, speaker, text). kind: dialogue | tasks | other.
    Layout-agnostic: works for full screenshots (name plate ~0.77 h, text ~0.85 h, Continue ~0.93 h)
    and for cropped dialogue strips (name at the top, text below). A candidate is a short capitalised
    name line followed, within `name_gap` × its own height, by sentence-like lines; the lowest candidate
    on the screen wins (HUD numbers and task titles sit above the dialogue box)."""
    if "lines" not in rec or not rec["lines"]:
        return "other", None, None
    texts = [l["t"].strip() for l in rec["lines"]]
    low = " ".join(texts).lower()
    if re.search(r"\b\d+/\d+\b", low) and ("show" in low or "tasks" in low):
        return "tasks", None, None
    lines = sorted(rec["lines"], key=lambda l: l["y"])
    best = None
    for i, l in enumerate(lines):
        t = l["t"].strip()
        if is_ui(t) or t.lower() in UI_HEADINGS or not NAME_RE.match(t):
            continue
        known = canonical_speaker(t) in known_names or t in known_names
        body = []
        for m in lines[i + 1:]:
            mt = m["t"].strip()
            if is_ui(mt) or m["y"] - l["y"] > cfg["name_gap"] * max(l["h"], 1):
                break
            if NAME_RE.match(mt) and not body and not known:   # another name plate directly below: not a dialogue box
                break
            body.append(mt)
        joined = clean_text(" ".join(body))
        # a known character may say something very short ("Boo.", "Deal!") but a bare UI word ("Refresh") is not
        # a line; an unknown plate must carry a real sentence and is only a candidate for manual review.
        short_ok = known and (" " in joined or re.search(r"[.!?…]$", joined)) and re.search(r"[A-Za-z]{2,}", joined)
        ok = short_ok or (is_sentence(joined) and len(joined) >= cfg["min_text"])
        if ok:
            score = (known, l["y"])
            if best is None or score > best[0]:
                best = (score, canonical_speaker(t), joined, known)
    if best:
        return ("dialogue" if best[3] else "maybe"), best[1], best[2]
    return "other", None, None


def main(channel_id):
    base = os.path.join(common.CACHE, "discord", channel_id)
    cfg = {"name_gap": 10, "min_text": 12}
    codex_path = os.path.join(common.CODEX, "codex.json")
    known = set()
    if os.path.exists(codex_path):
        known |= EXTRA_SPEAKERS
        for cid, c in common.read_json(codex_path)["characters"].items():
            known.add(cid)
            known.add(c["name"])
    ocr = load_ocr(base)
    index = common.read_json(os.path.join(base, "images_index.json"))
    threads = {t["id"]: t for t in common.read_json(os.path.join(base, "threads.json"))["threads"]}
    by_thread = collections.defaultdict(list)
    audit = []
    for it in index:
        rel = it["thread"] + "/" + os.path.basename(it["file"])
        rec = ocr.get(rel)
        if not rec:
            continue
        kind, speaker, text = classify(rec, cfg, known)
        audit.append({"file": rel, "kind": kind, "speaker": speaker, "text": text})
        if kind == "dialogue":
            by_thread[it["thread"]].append({"speaker": speaker, "text": text, "message": it["message"],
                                            "timestamp": it["timestamp"], "author": it["author"], "file": rel})
    dropped = 0
    for tid, lst in by_thread.items():
        lst.sort(key=lambda x: (x["timestamp"] or "", x["file"]))
        # several players post the same screenshot: keep the first occurrence of a (speaker, text) pair per thread
        seen, unique = set(), []
        for x in lst:
            key = (x["speaker"], re.sub(r"[^a-z0-9]+", " ", x["text"].lower()).strip())
            if key in seen:
                dropped += 1
                continue
            seen.add(key)
            unique.append(x)
        by_thread[tid] = unique
    # unknown name plates that recur with sentence text are probably real (event-only) characters → review list
    maybe = collections.Counter(a["speaker"] for a in audit if a["kind"] == "maybe")
    result = {"channel": channel_id,
              "threads": [{"id": tid, "name": threads.get(tid, {}).get("name"), "lines": lines} for tid, lines in sorted(by_thread.items())],
              "unknownSpeakersToReview": [{"name": n, "count": c} for n, c in maybe.most_common() if c >= 3]}
    common.write_json(os.path.join(common.CODEX, "discord_dialogues.json"), result)
    common.write_json(os.path.join(base, "ocr_classified.json"), audit, compact=True)
    kinds = collections.Counter(a["kind"] for a in audit)
    print(f"images classified: {len(audit)} {dict(kinds)}; threads with dialogue: {len(by_thread)}; duplicates dropped: {dropped}; "
          f"unknown speakers (>=3): {len(result['unknownSpeakersToReview'])}")


if __name__ == "__main__":
    main(sys.argv[1])
