r"""
Download quest data from Wizard404's public JSON API (https://wizard404.com/api/v1), for the worlds the server has no
quest files for. Run it yourself; it saves the raw API answers, one file per quest, and convert.py turns them into
server quest files.

Follows the site's API rules (llms.txt and the OpenAPI notes):
  * documented /api/v1 endpoints only, no HTML pages;
  * one request at a time, at most one a second (their limit is 50 per 10 s and 120 per minute);
  * on 429 or 503 it waits for Retry-After and tries again a few times, then stops;
  * an optional developer key goes in the WIZARD404_API_KEY environment variable, never on the command line;
  * already-saved quests are skipped, so a stopped run picks up where it left off.

Usage (from this folder):
  python download.py                          all default worlds
  python download.py --worlds Darkmoor Novus  only these worlds
  python download.py --names AV-WYRD-C08-001  single quests by their code (uses the quest search)
  python download.py --names-file missing-codes.txt   every code in that file (one a line)
Output: raw\<template id>.json and raw\_lists\<world>_<page>.json
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://wizard404.com"
DEFAULT_WORLDS = ["Darkmoor", "Lemuria", "Novus", "Wallaru", "Ahmarra", "Selenopolis",
                  "WizardCity", "Krokotopia", "Empyrea", "Arcanum", "Karamelle", "Avalon"]
# Quests other quests need that the server lacks (2026-11-27).
DEFAULT_NAMES = ["AV-WYRD-C08-001", "WC-ST04-C01-001", "WC-ST05-C01-001", "WC-TUG-C01-001A", "WC-ST03-C01-006",
                 "WC-ST04-C01-005", "WC-ST05-C01-006", "WC-MAIN-C01-001", "WC-MAIN-C01-007"]
PAUSE = 1.0
PER_PAGE = 96
HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")

_last = 0.0


def get(path, params=None, tries=5):
    """GET one API path and return the parsed JSON, or None on 404."""
    global _last
    url = BASE + path + ("?" + urllib.parse.urlencode(params) if params else "")
    headers = {"Accept": "application/json", "User-Agent": "quest-data-download/1.0 (python urllib)"}
    key = os.environ.get("WIZARD404_API_KEY")
    if key:
        headers["Authorization"] = "Bearer " + key
    for attempt in range(tries):
        wait = PAUSE - (time.monotonic() - _last)
        if wait > 0:
            time.sleep(wait)
        _last = time.monotonic()
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            if e.code in (429, 503):
                retry = e.headers.get("Retry-After")
                delay = float(retry) if retry and retry.replace(".", "").isdigit() else 30.0 * (attempt + 1)
                print(f"  {e.code}: waiting {delay:.0f}s (Retry-After)", flush=True)
                time.sleep(delay)
                continue
            if e.code in (401, 403):
                sys.exit(f"Access refused ({e.code}) for {url}. Stopping: the site's rules say not to work around this.")
            raise
        except urllib.error.URLError as e:
            print(f"  network error {e.reason}; retrying in 15s", flush=True)
            time.sleep(15)
    sys.exit(f"Gave up on {url} after {tries} tries.")


def find_quest_rows(payload):
    """The list payload's quest rows: data.items (each with templateId), or else the largest list of id-carrying dicts."""
    items = (payload.get("data") or {}).get("items") if isinstance(payload, dict) else None
    if isinstance(items, list):
        return [x for x in items if isinstance(x, dict)]
    best = []

    def walk(node):
        nonlocal best
        if isinstance(node, list):
            rows = [x for x in node if isinstance(x, dict) and ("templateId" in x or "template_id" in x)]
            if len(rows) > len(best):
                best = rows
            for x in node:
                walk(x)
        elif isinstance(node, dict):
            for v in node.values():
                walk(v)

    walk(payload)
    return best


def has_next(payload, page, rows):
    def walk(node):
        if isinstance(node, dict):
            if "has_next" in node:
                return bool(node["has_next"])
            for v in node.values():
                r = walk(v)
                if r is not None:
                    return r
        return None

    r = walk(payload)
    return r if r is not None else len(rows) >= PER_PAGE


def row_id(row):
    return row.get("templateId") or row.get("template_id")


def save_quest(tid):
    out = os.path.join(RAW, f"{tid}.json")
    if os.path.exists(out):
        return False
    data = get(f"/api/v1/quests/{tid}")
    if data is None:
        print(f"  quest {tid}: not found")
        return False
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    quest = (data.get("data") or {}).get("quest") or {}
    name = (quest.get("parsed_data") or {}).get("quest_name")
    print(f"  saved {tid} {name}", flush=True)
    return True


def list_world(world):
    ids, page = [], 1
    while True:
        params = {"world": world, "page": page, "per_page": PER_PAGE,
                  "exclude_adjective": "unreleased", "developer_only": "false", "retired": "false"}
        payload = get("/api/v1/quests", params)
        if payload is None:
            break
        with open(os.path.join(RAW, "_lists", f"{world}_{page}.json"), "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False)
        rows = find_quest_rows(payload)
        ids += [row_id(r) for r in rows if row_id(r)]
        if not rows or not has_next(payload, page, rows):
            break
        page += 1
    return ids


def search_name(name):
    """Saves the quest whose code is exactly this name: the search's first few hits are opened until one matches
    (a hit can also be a quest that only names this one in its requirements)."""
    payload = get("/api/v1/quests/search", {"q": name})
    rows = find_quest_rows(payload or {})
    for tid in [row_id(r) for r in rows if row_id(r)][:5]:
        out = os.path.join(RAW, f"{tid}.json")
        if os.path.exists(out):
            with open(out, encoding="utf-8") as f:
                data = json.load(f)
        else:
            data = get(f"/api/v1/quests/{tid}")
        quest = ((data or {}).get("data") or {}).get("quest") or {}
        if (quest.get("parsed_data") or {}).get("quest_name") == name:
            if not os.path.exists(out):
                with open(out, "w", encoding="utf-8") as f:
                    json.dump(data, f, ensure_ascii=False)
                print(f"  saved {tid} {name}", flush=True)
            return 1
    print(f"  {name}: not found on Wizard404", flush=True)
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--worlds", nargs="*", default=None)
    ap.add_argument("--names", nargs="*", default=None)
    ap.add_argument("--names-file", default=None, help="a text file with one quest code a line (# lines skipped)")
    a = ap.parse_args()
    if a.names_file:
        with open(a.names_file, encoding="utf-8") as fh:
            listed = [x.strip() for x in fh if x.strip() and not x.lstrip().startswith("#")]
        a.names = (a.names or []) + listed
    worlds = a.worlds if a.worlds is not None else (DEFAULT_WORLDS if a.names is None else [])
    names = a.names if a.names is not None else (DEFAULT_NAMES if a.worlds is None else [])
    os.makedirs(os.path.join(RAW, "_lists"), exist_ok=True)

    saved = 0
    for world in worlds:
        print(f"== {world}", flush=True)
        ids = list_world(world)
        print(f"  {len(ids)} quests listed", flush=True)
        for tid in ids:
            saved += save_quest(tid)
    not_found = []
    for n, name in enumerate(names, 1):
        print(f"== search {name} ({n}/{len(names)})", flush=True)
        found = search_name(name)
        saved += found
        if not found:
            not_found.append(name)
    if not_found:
        with open(os.path.join(HERE, "not-on-wizard404.txt"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(not_found) + "\n")
        print(f"{len(not_found)} codes are not on Wizard404: listed in not-on-wizard404.txt")
    print(f"Done: {saved} new quest files in {RAW}")


if __name__ == "__main__":
    main()
