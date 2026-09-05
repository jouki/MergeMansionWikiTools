# -*- coding: utf-8 -*-
"""Area progression order from the wiki: Module:Datatable/Areas/Mapping rows `["Name"] = {orderingIndex = N, ...}`.
Run: python Codex/build/fetch_area_order.py  ->  Codex/build/area_order.json  {name: orderingIndex}
Read-only, anonymous API call (the module is public); the app's AreaOrderingService maintains the module itself."""
import json
import os
import re
import sys
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
import common  # noqa: E402

API = "https://merge-mansion.fandom.com/api.php"
TITLE = "Module:Datatable/Areas/Mapping"
ROW_RE = re.compile(r'^\s*\["([^"]+)"\]\s*=\s*\{[^}]*?orderingIndex\s*=\s*([0-9.]+)', re.M)   # live rows only (commented ones start with --)
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "area_order.json")


def fetch_module():
    q = urllib.parse.urlencode({"action": "query", "prop": "revisions", "titles": TITLE, "rvprop": "content|ids",
                                "rvslots": "main", "format": "json", "formatversion": "2"})
    req = urllib.request.Request(API + "?" + q, headers={"User-Agent": "MMWT-Codex/1.0 (jouki)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        d = json.loads(r.read().decode("utf-8"))
    rev = d["query"]["pages"][0]["revisions"][0]
    return rev["revid"], rev["slots"]["main"]["content"]


def parse(content):
    return {name: float(idx) for name, idx in ROW_RE.findall(content)}


if __name__ == "__main__":
    revid, content = fetch_module()
    order = parse(content)
    common.write_json(OUT, {"module": TITLE, "revid": revid, "order": order})
    print(f"{TITLE} rev {revid}: {len(order)} areas -> {OUT}")
    for n, i in sorted(order.items(), key=lambda kv: kv[1])[:5]:
        print(f"  {i:>5} {n}")
