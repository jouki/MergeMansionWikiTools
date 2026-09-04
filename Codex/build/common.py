# -*- coding: utf-8 -*-
"""Shared helpers for the Dialogue Codex build scripts (stdlib only)."""
import json
import os
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

APKS = r"D:\_BACKUP_2.0\Adobe Photoshop - Savy\Merge Mansion\APKs"
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CODEX = os.path.join(REPO, "Codex")
CACHE = os.path.join(CODEX, "_cache")
VERSION_RE = re.compile(r"^\d\d\.\d\d\.\d\d$")


def read_json(path):
    """Read a dump JSON (UTF-8 with or without BOM); unwrap {"CreatedAt","Data"} app envelopes."""
    with open(path, encoding="utf-8-sig") as f:
        obj = json.load(f)
    if isinstance(obj, dict) and "Data" in obj and "CreatedAt" in obj:
        return obj["Data"]
    return obj


def write_json(path, obj, compact=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        if compact:
            json.dump(obj, f, ensure_ascii=False, separators=(",", ":"))
        else:
            json.dump(obj, f, ensure_ascii=False, indent=1)


def versions(root=APKS):
    """Chronologically sorted version folders (YY.MM.NN only)."""
    return sorted(v for v in os.listdir(root) if VERSION_RE.match(v) and os.path.isdir(os.path.join(root, v)))


def runs(pairs):
    """[(version, value)] chronological -> run-length [{"from": version, "value": value}].
    A new run starts only when the value differs from the previous run; None = absent."""
    out = []
    for ver, val in pairs:
        if not out or out[-1]["value"] != val:
            out.append({"from": ver, "value": val})
    return out


def value_at(run_list, version, versions_order):
    """Value of a run-length list at `version` (None when before the first run)."""
    cur = None
    for r in run_list:
        if versions_order.index(r["from"]) <= versions_order.index(version):
            cur = r["value"]
        else:
            break
    return cur


def harness_exe():
    """DumpHarness.exe built by the extract scripts into %TEMP% (see extract_loc.build_harness)."""
    return os.path.join(os.environ.get("TEMP", REPO), "mmwt_dumpharness", "DumpHarness.exe")
