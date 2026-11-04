#!/usr/bin/env python3
"""Builds src/Imlight.Setup/Assets/creature-places.json.gz for Ahziael's Wiki tab: where every creature is fought,
its name-plate title, and which creatures come with it.

Usage: python3 -I tools/wiki/build_creature_places.py <wizard404 raw folder> <out .json.gz>

Same source and place rules as tools/boss-test/build_boss_places.py (a zone's spawn list; a minion no zone spawns gets
the places of the creature it comes with), but for every creature, not only the bosses.
"""
import collections
import gzip
import importlib.util
import json
import os
import sys

_here = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("bp", os.path.join(_here, "..", "boss-test", "build_boss_places.py"))
bp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bp)


def main(raw, out):
    mobs = {}
    for r in bp.records(raw, "api-mob"):
        pd = (r.get("data") or {}).get("parsed_data") or {}
        if pd and r["id"] not in mobs:
            mobs[r["id"]] = pd

    places = collections.defaultdict(list)
    seen = set()
    for r in bp.records(raw, "api-zone"):
        if r["id"] in seen:
            continue
        seen.add(r["id"])
        pd = ((r.get("data") or {}).get("zone") or {}).get("parsed_data") or {}
        if not pd or pd.get("retired") or pd.get("developer_only") or pd.get("world_internal_name") == "Test":
            continue
        place = {"Name": pd.get("display_name") or "", "Zone": pd.get("object_name") or "",
                 "World": bp.world_of(pd.get("world_internal_name") or "")}
        for mob in set(pd.get("spawn_mob_ids") or []):
            if place not in places[mob]:
                places[mob].append(place)

    parents = collections.defaultdict(set)
    for i, pd in mobs.items():
        for m in pd.get("minions") or []:
            parents[m].add(i)

    creatures = {}
    for i in sorted(set(mobs) | set(places)):
        pd = mobs.get(i, {})
        entry = {}
        own = sorted(places.get(i, []), key=lambda p: (p["World"], p["Name"]))
        if own:
            entry["Places"] = own[:8]
        ups = sorted(parents.get(i, ()))
        if ups:
            entry["MinionOf"] = ups[:6]
            if not own:
                entry["Places"] = [p for up in ups[:2] for p in places.get(up, [])[:3]]
        if pd.get("display_name"):
            entry["Name"] = pd["display_name"]
        if pd.get("mob_type"):
            entry["Title"] = pd["mob_type"]
        minions = [m for m in (pd.get("minions") or []) if isinstance(m, int)]
        if minions:
            entry["Minions"] = sorted(set(minions))[:8]
        if entry:
            creatures[str(i)] = entry

    data = {"Source": "Wizard404's zone spawn lists (saved 2026-10-24)", "Bosses": creatures}
    with gzip.open(out, "wt", encoding="utf-8") as f:
        json.dump(data, f, separators=(",", ":"), ensure_ascii=False)
    placed = sum(1 for e in creatures.values() if e.get("Places"))
    print(f"{len(creatures)} creatures, {placed} with a place -> {out}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(*sys.argv[1:])
