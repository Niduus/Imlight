#!/usr/bin/env python3
"""Builds src/Imlight.Setup/Assets/boss-places.json.gz for Ahziael's Boss Test tab: where each boss is fought.

Usage: python3 tools/boss-test/build_boss_places.py <wizard404 raw folder> <BossCheats folder> <out .json.gz>

The raw folder is the saved Wizard404 scrape (api-zone*.jsonl.gz, api-mob*.jsonl.gz). A zone's spawn list names the
creatures it spawns; a minion no zone spawns gets the places of the creature it comes with. Kept: every creature with a
cheat file, and every creature Wizard404 marks as a boss, so the tab can show places for cheats made in the app too.
"""
import collections
import gzip
import json
import os
import sys

WORLDS = {
    "WizardCity": "Wizard City", "Krokotopia": "Krokotopia", "Marleybone": "Marleybone", "MooShu": "MooShu",
    "DragonSpire": "Dragonspyre", "Celestia": "Celestia", "Grizzleheim": "Grizzleheim", "GrizzleheimLite": "Grizzleheim",
    "Wysteria": "Wysteria", "Zafaria": "Zafaria", "Avalon": "Avalon", "Azteca": "Azteca", "Aquila": "Aquila",
    "Khrysalis": "Khrysalis", "Polaris": "Polaris", "Mirage": "Mirage", "Empyrea": "Empyrea", "Karamelle": "Karamelle",
    "Lemuria": "Lemuria", "Novus": "Novus", "Wallaru": "Wallaru", "Ahmarra": "Ahmarra", "Arcanum": "Arcanum",
    "Darkmoor": "Darkmoor", "G14_DM": "Darkmoor", "G14_HS": "Dungeons", "G14_SB": "Dungeons",
    "Heroic_Dungeons_01": "Dungeons", "DD_DS_01": "Dungeons", "DD_PA_01": "Dungeons", "Raids": "Raids",
    "ThePhantomZoneWorld": "Phantom zone", "MonthlyEvents": "Events", "Holiday": "Events", "Battlegrounds": "Battlegrounds",
    "PetDerby": "Pet Derby",
}


def records(folder, prefix):
    for name in sorted(os.listdir(folder)):
        if name.startswith(prefix) and name.endswith(".jsonl.gz"):
            with gzip.open(os.path.join(folder, name), "rt", encoding="utf-8") as f:
                for line in f:
                    if line.strip():
                        yield json.loads(line)


def world_of(internal):
    if internal in WORLDS:
        return WORLDS[internal]
    if internal.startswith("Housing"):
        return "Gauntlets and houses"
    return internal


def main(raw, cheats, out):
    with_cheats = {int(f[:-5]) for f in os.listdir(cheats) if f.endswith(".json") and f[:-5].isdigit()}
    mobs = {}
    for r in records(raw, "api-mob"):
        pd = (r.get("data") or {}).get("parsed_data") or {}
        if pd and r["id"] not in mobs:
            mobs[r["id"]] = pd

    wanted = with_cheats | {i for i, pd in mobs.items() if pd.get("boss_mob")}
    places = collections.defaultdict(list)
    seen = set()
    for r in records(raw, "api-zone"):
        if r["id"] in seen:
            continue
        seen.add(r["id"])
        pd = ((r.get("data") or {}).get("zone") or {}).get("parsed_data") or {}
        if not pd or pd.get("retired") or pd.get("developer_only") or pd.get("world_internal_name") == "Test":
            continue
        place = {"Name": pd.get("display_name") or "", "Zone": pd.get("object_name") or "",
                 "World": world_of(pd.get("world_internal_name") or "")}
        for mob in set(pd.get("spawn_mob_ids") or []):
            if mob in wanted and place not in places[mob]:
                places[mob].append(place)

    # Minions no zone spawns: where the creatures they come with are.
    parents = collections.defaultdict(set)
    for i, pd in mobs.items():
        for m in pd.get("minions") or []:
            parents[m].add(i)

    bosses = {}
    for i in sorted(wanted):
        entry = {}
        own = sorted(places.get(i, []), key=lambda p: (p["World"], p["Name"]))
        if own:
            entry["Places"] = own[:6]
        else:
            ups = sorted(p for p in parents.get(i, ()) if places.get(p))
            if ups:
                entry["MinionOf"] = ups[:4]
                entry["Places"] = [p for up in ups[:2] for p in places[up][:3]]
        if i in mobs and mobs[i].get("display_name"):
            entry["Name"] = mobs[i]["display_name"]
        if entry.get("Places") or i in with_cheats:
            bosses[str(i)] = entry

    data = {"Source": "Wizard404's zone spawn lists (saved 2026-10-24)", "Bosses": bosses}
    with gzip.open(out, "wt", encoding="utf-8") as f:
        json.dump(data, f, separators=(",", ":"), ensure_ascii=False)
    found = sum(1 for i in with_cheats if bosses.get(str(i), {}).get("Places"))
    print(f"{len(bosses)} creatures; {found} of {len(with_cheats)} with cheats have a place -> {out}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
