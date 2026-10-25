"""
Writes spiraldb-extra/WoodenChests/WoodenChests.json, what the world's wooden chests give (WoodenChestComponent.cs).

- Chests: every wooden chest template on Wizard404 (api-scene-object, display name "Wooden Chest") with the game's
  own loot table names for it and the world its name starts with.
- TreasureCardTables: the game tables the older chests name (TC-Commons, TC-Uncommons, TC-CommonsBosses, ...), as Wizard404
  lists them on mob pages. The newer chests name WoodenChestLoot-* tables, which Wizard404 does not list; those chests
  take the world's treasure cards instead.
- Worlds: each world's basic drops, the reagents, pet snacks and treasure cards its creatures drop (Wizard404 zones:
  spawn_mob_ids, mob pages: lootEntryCatalog). Weight = how many of the world's creatures drop it; rare ones are left out.

Run: python3 -I make_wooden_chests.py <wizard404-data/raw> <wizard404-complete/raw> <spiraldb-extra/WoodenChests>
"""
import collections, glob, gzip, json, os, re, sys

data_raw, complete_raw, out = sys.argv[1], sys.argv[2], sys.argv[3]

# Worlds in Spiral order, with the tier that sets their gold (0 = Wizard City). Prefix = the start of a chest's name.
WORLDS = [
    ("WizardCity", 0, ["WC"]), ("Krokotopia", 1, ["KT"]), ("Marleybone", 2, ["MB"]), ("MooShu", 3, ["MS"]),
    ("Wysteria", 3, ["PA"]), ("DragonSpire", 4, ["DS"]), ("Grizzleheim", 4, ["GH", "GH2"]), ("Celestia", 5, ["CL"]),
    ("Zafaria", 6, ["ZF"]), ("Avalon", 7, ["AV"]), ("Aquila", 7, ["AQ"]), ("Azteca", 8, ["AZ", "Raid"]),
    ("Khrysalis", 9, ["KR"]), ("Darkmoor", 9, ["DM"]), ("Polaris", 10, ["PL"]), ("Mirage", 11, ["MR"]),
    ("Arcanum", 11, ["AR"]), ("Empyrea", 12, ["EM"]), ("Karamelle", 13, ["KM"]), ("Lemuria", 14, ["LM"]),
    ("Novus", 15, ["NV"]), ("Wallaru", 16, ["WL"]), ("Ahmarra", 17, ["AH"]),
]
# Zones whose world name is not one of the above, and worlds Wizard404 has no drops for.
FALLBACKS = {"G14_DM": "Darkmoor", "G14_HS": "Celestia", "G14_SB": "Krokotopia", "Wallaru": "Novus", "Ahmarra": "Novus", "Raids": "Azteca"}
KINDS = {"Reagent": "Reagents", "Snack": "Snacks", "TreasureCard": "TreasureCards"}
MOST = 30

def lines(pattern):
    for path in sorted(glob.glob(pattern)):
        for line in gzip.open(path, "rt", encoding="utf-8"):
            yield json.loads(line)

# Treasure card names: Wizard404 numbers a card by the hash of its name; the server finds it by that name.
tc_names = {}
for record in lines(os.path.join(complete_raw, "api-spell-*.jsonl.gz")):
    parsed = record["data"].get("parsed_data") or {}
    if parsed.get("object_name"):
        tc_names[record["id"]] = parsed["object_name"]

chests = {}
for record in lines(os.path.join(complete_raw, "api-scene-object-*.jsonl.gz")):
    parsed = record["data"].get("parsed_data") or {}
    if parsed.get("display_name") != "Wooden Chest":
        continue
    name = parsed.get("object_name") or ""
    prefix = re.split(r"[-_]", name)[0]
    world = next((w for w, _, prefixes in WORLDS if prefix in prefixes), "")
    if prefix == "KT" and name.endswith("-003"):
        world = "Wallaru"
    chests[str(record["id"])] = {"Name": name, "World": world, "Tables": parsed.get("loot_tables") or []}

wanted_tables = {t for chest in chests.values() for t in chest["Tables"] if t.startswith("TC-")}
mob_world = collections.defaultdict(set)
for record in lines(os.path.join(data_raw, "api-zone*.jsonl.gz")):
    parsed = record["data"]["zone"].get("parsed_data") or {}
    for mob in parsed.get("spawn_mob_ids") or []:
        mob_world[mob].add(parsed.get("world_internal_name") or "")

loot = {}
tc_tables = {}
for record in lines(os.path.join(data_raw, "data-*.jsonl.gz")):
    for config in (record.get("json") or {}).get("loot-drops-config mob-drops-config") or []:
        if record.get("kind") == "mob":
            loot.setdefault(int(record["id"]), config.get("lootEntryCatalog") or [])
        for group in config.get("lootTableGroups") or []:
            table = group.get("table_name")
            if table in wanted_tables and table not in tc_tables and group.get("entries"):
                tc_tables[table] = [tc_names[e["template_id"]] for e in group["entries"] if e["template_id"] in tc_names]

worlds = []
for world, tier, _ in WORLDS:
    mobs = [m for m, ws in mob_world.items() if world in ws and loot.get(m)]
    entry = {"World": world, "Tier": tier, "Creatures": len(mobs)}
    for sub_type, key in KINDS.items():
        counts = collections.Counter()
        names = {}
        for mob in mobs:
            for item in {e["template_id"]: e for e in loot[mob] if e.get("sub_type") == sub_type}.values():
                counts[item["template_id"]] += 1
                names[item["template_id"]] = item["display_name"]
        floor = max(2, len(mobs) * 0.05)
        picked = [(t, n) for t, n in counts.most_common() if n >= floor][:MOST]
        if sub_type == "TreasureCard":
            entry[key] = [{"Id": tc_names[t], "Name": names[t], "Weight": n} for t, n in picked if t in tc_names]
        else:
            entry[key] = [{"Id": t, "Name": names[t], "Weight": n} for t, n in picked]
    worlds.append(entry)

os.makedirs(out, exist_ok=True)
with open(os.path.join(out, "WoodenChests.json"), "w", encoding="utf-8") as f:
    json.dump({
        "Description": "What the world's wooden chests give besides gold. Made by tools/wooden-chests/make_wooden_chests.py "
                       "from Wizard404. How often each kind drops and how much gold: Imlight.ini [WoodenChests].",
        "Chests": dict(sorted(chests.items(), key=lambda c: c[1]["Name"])),
        "TreasureCardTables": dict(sorted(tc_tables.items())),
        "Fallbacks": FALLBACKS,
        "Worlds": worlds,
    }, f, indent=1, ensure_ascii=False)
    f.write("\n")

for w in worlds:
    print(w["World"], w["Creatures"], {k: len(w[k]) for k in KINDS.values()})
print(len(chests), "chests;", {t: len(v) for t, v in tc_tables.items()})
