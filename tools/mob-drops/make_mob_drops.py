"""
Gives every creature Wizard404 lists drops for its drop tables (spiraldb-extra/NpcDropTable + DropTables/w404-*).

Before this only the gauntlet creatures and a few bosses had tables (838 creatures); every other creature dropped gold
and nothing else. Wizard404's mob pages name each of the game's loot tables a creature uses, what is in each one and its
gold, but not how often an item drops. So, like the gauntlet tables before:
- one gold table per game table ("W404 <table>": its gold, at Wizard404's drop rate),
- one table per game table, kind of drop and creature rank ("W404 <table> Gear (Elite)"): one item picked when it
  rolls, how often from DropOdds.json (Odds "Elite/Gear"), which the server reads live.
Treasure cards are named by their spell name ("Blademaster TC"): Wizard404 numbers them by a hash, not the game's id.

Creatures and tables that already have a file are kept as they are (Central bosses, hand-made ones).

Run: python3 -I make_mob_drops.py <wizard404-data/raw> <wizard404-complete/raw> <spiraldb-extra>
"""
import collections, glob, gzip, json, os, re, sys

data_raw, complete_raw, extra = sys.argv[1], sys.argv[2], sys.argv[3]
tables_dir = os.path.join(extra, "DropTables")
npc_dir = os.path.join(extra, "NpcDropTable")

GEAR = {"Hat", "Robe", "Shoes", "Ring", "Athame", "Weapon", "Amulet", "Deck"}
RANKS = {"Boss": "Boss", "Elite": "Elite", "Normal": "Normal", "Easy": "Normal"}
# The order a creature's tables are listed in, as the gauntlet files have them.
KIND_ORDER = ["Gear", "Pet", "Mount", "Jewel", "TreasureCard", "Reagent", "Snack", "Seed", "Housing", "Elixir"]


# Each table's own RollChance only counts while DropOdds.json has no entry for its Odds; it starts equal to it.
with open(os.path.join(extra, "DropOdds.json"), encoding="utf-8") as f:
    ODDS = json.loads(re.sub(r"^\s*//.*$", "", f.read(), flags=re.M))["Odds"]


def lines(pattern):
    for path in sorted(glob.glob(pattern)):
        for line in gzip.open(path, "rt", encoding="utf-8"):
            yield json.loads(line)


def kind_of(entry):
    item_type, sub = entry.get("item_type"), entry.get("sub_type")
    if item_type == "gear":
        if sub in GEAR:
            return "Gear"
        return sub if sub in ("Jewel", "Pet", "Mount", "Elixir") else None
    if item_type == "spell":
        return "TreasureCard" if sub == "TreasureCard" else None
    if item_type == "housing":
        return "Seed" if sub == "Seed" else "Housing"
    if item_type == "reagent":
        return "Reagent"
    if item_type == "snack":
        return "Snack"
    return None


def slug(text):
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", text).strip("_")


tc_names = {}
for record in lines(os.path.join(complete_raw, "api-spell-*.jsonl.gz")):
    parsed = record["data"].get("parsed_data") or {}
    if parsed.get("object_name"):
        tc_names[record["id"]] = parsed["object_name"]

# The tables already there, by name, and which odds each rolls at.
existing = {}
for path in glob.glob(os.path.join(tables_dir, "*.json")):
    try:
        with open(path, encoding="utf-8") as f:
            table = json.load(f)
    except (ValueError, OSError):
        continue
    if table.get("Name"):
        existing[table["Name"].lower()] = table.get("Odds")

mobs = {}
for record in lines(os.path.join(data_raw, "data-mob*.jsonl.gz")):
    config = (record.get("json") or {}).get("loot-drops-config mob-drops-config")
    if config and record["id"] not in mobs:
        mobs[record["id"]] = (record, config[0])

written_tables, written_mobs, kept_mobs, empty = 0, 0, 0, 0
stats = collections.Counter()


def write_table(name, body):
    global written_tables
    if name.lower() in existing:
        return
    existing[name.lower()] = body.get("Odds")
    path = os.path.join(tables_dir, f"w404-{slug(name[5:])}.json")
    n = 2
    while os.path.exists(path):
        path = os.path.join(tables_dir, f"w404-{slug(name[5:])}-{n}.json")
        n += 1
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps({"Name": name, **body}, ensure_ascii=False, indent=2) + "\n")
    written_tables += 1


def item_tables(source, entries, rank, what):
    """The kind tables of one game table (or a creature's own list) for a creature of this rank; returns their names."""
    by_kind = collections.defaultdict(list)
    seen = set()
    for entry in entries:
        kind = kind_of(entry)
        tid = entry.get("template_id")
        if kind is None or tid is None or tid in seen:
            continue
        seen.add(tid)
        if kind == "TreasureCard":
            spell = tc_names.get(tid)
            if not spell:
                stats["tc without a name"] += 1
                continue
            by_kind[kind].append({"ItemId": spell, "ItemName": entry.get("display_name") or ""})
        else:
            by_kind[kind].append({"ItemId": str(tid), "ItemName": entry.get("display_name") or ""})

    names = []
    for kind in KIND_ORDER:
        items = by_kind.get(kind)
        if not items:
            continue
        odds = f"{rank}/{kind}"
        plain = f"W404 {source} {kind}"
        # A gauntlet table of the same game table and the same rank is the same table.
        if existing.get(plain.lower(), "") == odds:
            names.append(plain)
            continue
        name = f"{plain} ({rank})"
        write_table(name, {
            "Description": f"Wizard404's drop list of {what}: the {kind.lower()} drops ({len(items)}), one picked when it "
                           f"rolls. How often: DropOdds.json {odds}.",
            "Odds": odds,
            "RollChance": round(ODDS.get(rank, {}).get(kind, 10) / 100, 4),
            "EachItem": False,
            "Items": items,
        })
        names.append(name)
    return names


for mob_id, (record, config) in sorted(mobs.items()):
    npc_path = os.path.join(npc_dir, f"{mob_id}.json")
    if os.path.exists(npc_path):
        kept_mobs += 1
        continue

    rank = RANKS.get((record.get("mob") or {}).get("type"), "Normal")
    catalog = config.get("lootEntryCatalog") or []
    by_id = {e.get("template_id"): e for e in catalog}
    names = []
    for group in config.get("lootTableGroups") or []:
        if not group.get("includes_current_owner", True) or not group.get("table_name"):
            continue
        source = group["table_name"]
        gold = (group.get("gold_entries") or [None])[0]
        if gold and (gold.get("max_amount") or 0) > 0:
            name = f"W404 {source}"
            write_table(name, {
                "Description": f"Gold of the game's table {source}, from Wizard404.",
                "RollChance": max(0.0, min(1.0, gold.get("drop_rate") or 1.0)),
                "MinGold": int(gold.get("min_amount") or 0),
                "MaxGold": int(gold.get("max_amount") or 0),
                "EachItem": False,
                "Items": [],
            })
            names.append(name)
        entries = [by_id[t] for t in group.get("template_ids") or [] if t in by_id]
        names += item_tables(source, entries, rank, f"the game's table {source}")

    # Drops Wizard404 lists for the creature itself, outside any of its tables.
    ungrouped = [catalog[i] for i in config.get("ungroupedLootEntries") or [] if isinstance(i, int) and i < len(catalog)]
    if ungrouped:
        names += item_tables(f"Mob-{mob_id}", ungrouped, rank, f"{record.get('name') or mob_id}'s own drops")

    names = list(dict.fromkeys(names))
    if not names:
        empty += 1
        continue
    with open(npc_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps({"TemplateID": mob_id, "DropTableNames": names}, ensure_ascii=False, indent=2) + "\n")
    written_mobs += 1

print(f"creatures: {written_mobs} new, {kept_mobs} kept, {empty} with nothing to drop; tables written: {written_tables}")
for key, value in stats.items():
    print(f"  {key}: {value}")
