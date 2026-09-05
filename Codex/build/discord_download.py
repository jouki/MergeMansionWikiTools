# -*- coding: utf-8 -*-
"""Standalone, resumable downloader of every image attachment of an inventoried Discord channel.
Run (separate console, keeps running on its own):
    python Codex/build/discord_download.py <channelId> [--workers 6]
Input : Codex/_cache/discord/<channelId>/messages/<threadId>.json  (from discord_inventory.py --messages)
Output: Codex/_cache/discord/<channelId>/images/<threadId>/<messageId>_<n>_<filename>
        Codex/_cache/discord/<channelId>/images_index.json   (thread, message, author, timestamp, file, size)
        Codex/_cache/discord/<channelId>/download.log
Attachment URLs are signed and expire (~24 h): when one is refused the thread's messages are re-fetched
through the bot API and the download retried with the fresh URL."""
import concurrent.futures
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402
import discord_inventory as di  # noqa: E402

IMAGE_EXT = (".png", ".jpg", ".jpeg", ".webp", ".gif")
lock = threading.Lock()
stats = {"done": 0, "skipped": 0, "failed": 0, "bytes": 0, "refreshed": 0}
fresh_urls = {}          # (threadId, messageId, filename) -> refreshed url
refreshed_threads = set()


def log(base, line):
    with lock:
        with open(os.path.join(base, "download.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("%H:%M:%S ") + line + "\n")


def safe_name(name):
    return "".join(c if c.isalnum() or c in "._-" else "_" for c in (name or "file"))[:120]


def jobs_for(base):
    mdir = os.path.join(base, "messages")
    out = []
    for fn in sorted(os.listdir(mdir)):
        tid = fn[:-5]
        for m in common.read_json(os.path.join(mdir, fn)):
            for i, a in enumerate(m.get("attachments") or []):
                if (a.get("filename") or "").lower().endswith(IMAGE_EXT) and a.get("url"):
                    dest = os.path.join(base, "images", tid, f"{m['id']}_{i}_{safe_name(a['filename'])}")
                    out.append({"thread": tid, "message": m["id"], "author": m.get("author"), "timestamp": m.get("timestamp"),
                                "index": i, "filename": a["filename"], "url": a["url"], "size": a.get("size") or 0, "file": dest})
    return out


def refresh(tok, job, base):
    """Re-fetch the thread's messages (signed URLs expired) — once per thread per run."""
    key = (job["thread"], job["message"], job["filename"])
    with lock:
        need = job["thread"] not in refreshed_threads
        if need:
            refreshed_threads.add(job["thread"])
    if need:
        msgs = di.all_messages(tok, job["thread"])
        common.write_json(os.path.join(base, "messages", job["thread"] + ".json"), msgs)
        with lock:
            for m in msgs:
                for a in m.get("attachments") or []:
                    fresh_urls[(job["thread"], m["id"], a.get("filename"))] = a.get("url")
            stats["refreshed"] += 1
    with lock:
        return fresh_urls.get(key)


def download(tok, job, base):
    dest = job["file"]
    if os.path.exists(dest) and (job["size"] == 0 or os.path.getsize(dest) == job["size"]):
        with lock:
            stats["skipped"] += 1
        return
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    url = job["url"]
    for attempt in range(6):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "MMWT-Codex/1.0"})
            with urllib.request.urlopen(req, timeout=120) as r, open(dest + ".part", "wb") as f:
                while True:
                    chunk = r.read(1 << 16)
                    if not chunk:
                        break
                    f.write(chunk)
            os.replace(dest + ".part", dest)
            with lock:
                stats["done"] += 1
                stats["bytes"] += os.path.getsize(dest)
            return
        except urllib.error.HTTPError as e:
            if e.code in (403, 404) and attempt == 0:
                new = refresh(tok, job, base)
                if new:
                    url = new
                    continue
                log(base, f"GONE {job['thread']} {job['message']} {job['filename']} ({e.code})")
                break
            if e.code == 429:
                time.sleep(5 + attempt * 5)
                continue
            log(base, f"HTTP {e.code} {job['thread']} {job['message']} {job['filename']} attempt {attempt}")
            time.sleep(2 + attempt * 3)
        except Exception as ex:   # network hiccups: retry
            log(base, f"ERR {type(ex).__name__} {job['thread']} {job['message']} {job['filename']} attempt {attempt}")
            time.sleep(2 + attempt * 3)
    with lock:
        stats["failed"] += 1
    if os.path.exists(dest + ".part"):
        os.remove(dest + ".part")


def main(channel_id, workers):
    base = os.path.join(common.CACHE, "discord", channel_id)
    tok = di.token()
    jobs = jobs_for(base)
    total = len(jobs)
    log(base, f"START {total} images, {workers} workers")
    print(f"{total} images to check/download -> {os.path.join(base, 'images')}")
    started = time.time()
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as ex:
        futures = [ex.submit(download, tok, j, base) for j in jobs]
        for n, _ in enumerate(concurrent.futures.as_completed(futures), 1):
            if n % 100 == 0 or n == total:
                el = time.time() - started
                msg = (f"{n}/{total} done={stats['done']} skipped={stats['skipped']} failed={stats['failed']} "
                       f"refreshed={stats['refreshed']} {stats['bytes'] / 1e9:.2f} GB {el / 60:.1f} min")
                print(msg, flush=True)
                log(base, msg)
    index = [{k: j[k] for k in ("thread", "message", "author", "timestamp", "index", "filename", "file", "size")} for j in jobs
             if os.path.exists(j["file"])]
    common.write_json(os.path.join(base, "images_index.json"), index, compact=True)
    log(base, f"END done={stats['done']} skipped={stats['skipped']} failed={stats['failed']} indexed={len(index)}")
    print("index:", len(index), "files")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    w = int(sys.argv[sys.argv.index("--workers") + 1]) if "--workers" in sys.argv else 6
    main(args[0], w)
