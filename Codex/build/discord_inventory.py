# -*- coding: utf-8 -*-
"""Read-only inventory of every thread in one Discord channel (forum or text) via the bot API.
Run: python Codex/build/discord_inventory.py <channelId> [--messages]
Writes Codex/_cache/discord/<channelId>/threads.json (+ messages/<threadId>.json with --messages).
Token: DiscordBotToken from the app's settings.json (never stored in the repo)."""
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

API = "https://discord.com/api/v10"
SETTINGS = os.path.join(common.REPO, "bin", "Debug", "net9.0-windows10.0.19041.0", "win-x64", "settings.json")
IMAGE_EXT = (".png", ".jpg", ".jpeg", ".webp", ".gif")


def token():
    """Same resolution as the app (SettingsPage): settings.json DiscordBotToken when set, otherwise
    AppSettings.DefaultDiscordBotToken from Models/AppSettings.cs (segments are stored reversed there).
    The value is only held in memory for the API calls; never written anywhere."""
    with open(SETTINGS, encoding="utf-8-sig") as f:
        t = json.load(f).get("DiscordBotToken") or ""
    if t:
        return t
    import re
    with open(os.path.join(common.REPO, "Models", "AppSettings.cs"), encoding="utf-8-sig") as f:
        src = f.read()
    m = re.search(r"DefaultDiscordBotToken\s*\{.*?new\[\]\s*\{([^}]*)\}", src, re.S)
    parts = re.findall(r'"([^"]+)"', m.group(1))
    return ".".join(p[::-1] for p in parts)


def get(tok, path, params=None):
    url = API + path + ("?" + urllib.parse.urlencode(params) if params else "")
    for attempt in range(8):
        req = urllib.request.Request(url, headers={"Authorization": "Bot " + tok, "User-Agent": "MMWT-Codex/1.0"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 429:
                body = json.loads(e.read().decode("utf-8") or "{}")
                wait = float(body.get("retry_after", 1.0)) + 0.2
                time.sleep(wait)
                continue
            if e.code in (403, 404):
                return None
            raise
    raise RuntimeError("rate limited too long: " + path)


def thread_row(t):
    md = t.get("thread_metadata") or {}
    return {"id": t["id"], "name": t.get("name"), "parentId": t.get("parent_id"),
            "created": md.get("create_timestamp") or t.get("id"), "archived": md.get("archived"),
            "archiveTimestamp": md.get("archive_timestamp"), "messageCount": t.get("message_count"),
            "totalMessageSent": t.get("total_message_sent"), "appliedTags": t.get("applied_tags") or []}


def list_threads(tok, channel):
    threads = {}
    # archived public threads, paginated backwards by archive timestamp
    before = None
    while True:
        params = {"limit": 100}
        if before:
            params["before"] = before
        page = get(tok, f"/channels/{channel['id']}/threads/archived/public", params)
        if not page or not page.get("threads"):
            break
        for t in page["threads"]:
            threads[t["id"]] = thread_row(t)
        before = page["threads"][-1]["thread_metadata"]["archive_timestamp"]
        print(f"  archived: {len(threads)}", end="\r")
        if not page.get("has_more"):
            break
    # active threads of the whole guild, filtered to this channel
    active = get(tok, f"/guilds/{channel['guild_id']}/threads/active") or {}
    for t in active.get("threads") or []:
        if t.get("parent_id") == channel["id"]:
            threads[t["id"]] = thread_row(t)
    print(f"  threads total: {len(threads)}")
    return threads


def first_message(tok, thread_id):
    msgs = get(tok, f"/channels/{thread_id}/messages", {"limit": 1, "after": "0"})
    if not msgs:
        return None
    m = msgs[0]
    return {"author": (m.get("author") or {}).get("username"), "timestamp": m.get("timestamp"),
            "content": (m.get("content") or "")[:500],
            "attachments": [a.get("filename") for a in m.get("attachments") or []]}


def all_messages(tok, thread_id):
    out, before = [], None
    while True:
        params = {"limit": 100}
        if before:
            params["before"] = before
        page = get(tok, f"/channels/{thread_id}/messages", params)
        if not page:
            break
        for m in page:
            out.append({"id": m["id"], "author": (m.get("author") or {}).get("username"), "timestamp": m.get("timestamp"),
                        "content": m.get("content") or "",
                        "attachments": [{"filename": a.get("filename"), "url": a.get("url"), "size": a.get("size"),
                                         "width": a.get("width"), "height": a.get("height")} for a in m.get("attachments") or []]})
        if len(page) < 100:
            break
        before = page[-1]["id"]
    out.sort(key=lambda m: m["timestamp"])
    return out


def main(channel_id, with_messages=False):
    tok = token()
    out_dir = os.path.join(common.CACHE, "discord", channel_id)
    os.makedirs(out_dir, exist_ok=True)
    channel = get(tok, f"/channels/{channel_id}")
    if not channel:
        print("channel not readable (403/404) - is the bot in the server with View Channel + Read Message History?")
        return 1
    print(f"channel: {channel.get('name')} type={channel.get('type')} guild={channel.get('guild_id')}")
    threads = list_threads(tok, channel)
    for i, (tid, row) in enumerate(sorted(threads.items(), key=lambda kv: kv[1]["created"] or "")):
        row["firstMessage"] = first_message(tok, tid)
        if with_messages:
            msgs = all_messages(tok, tid)
            common.write_json(os.path.join(out_dir, "messages", tid + ".json"), msgs)
            row["messagesFetched"] = len(msgs)
            row["imageAttachments"] = sum(1 for m in msgs for a in m["attachments"] if (a["filename"] or "").lower().endswith(IMAGE_EXT))
        if i % 20 == 0:
            print(f"  {i + 1}/{len(threads)} {row['name'][:60]}")
    common.write_json(os.path.join(out_dir, "threads.json"), {"channel": {"id": channel_id, "name": channel.get("name"), "type": channel.get("type")},
                                                             "threads": sorted(threads.values(), key=lambda r: r["created"] or "")})
    print("written", os.path.join(out_dir, "threads.json"))
    return 0


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    sys.exit(main(args[0], "--messages" in sys.argv))
