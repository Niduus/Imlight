"""
Builds src/Imlight.Setup/Assets/quest-tree.json.gz, the Manager's Quest Tree tab data (Database page).

Every world's main story in the order it is played (Spiral Tracker, spiraltracker.com) with what each quest asks and
gives (Wizard404's quest data, matched by name), and the side quest chains each main quest opens. The Manager joins it
with the server's own quests (quest-details.json) when it shows, so it can say which quests the server has.

    python3 tools/quest-tree/build_quest_tree.py <spiraltracker.json> <folder with Wizard404 api-quest*.jsonl.gz> <out .json.gz>

The inputs live in the project files: imlight/spiraltracker/spiraltracker.json and imlight/wizard404-data/raw/.
"""
import collections
import glob
import gzip
import html
import json
import os
import re
import sys

# Story order; the side worlds sit where they are usually played.
WORLDS = [
    ("wizardcity", "Wizard City", False), ("catacombs", "Wizard City Catacombs", True), ("krokotopia", "Krokotopia", False),
    ("wysteria", "Wysteria", True), ("marleybone", "Marleybone", False), ("mooshu", "Mooshu", False),
    ("dragonspyre", "Dragonspyre", False), ("grizzleheim", "Grizzleheim", True), ("celestia", "Celestia", False),
    ("wintertusk", "Wintertusk", True), ("zafaria", "Zafaria", False), ("avalon", "Avalon", False),
    ("azteca", "Azteca", False), ("aquila", "Aquila", True), ("khrysalis", "Khrysalis", False),
    ("darkmoor", "Darkmoor", True), ("polaris", "Polaris", False), ("mirage", "Mirage", False),
    ("empyrea", "Empyrea", False), ("karamelle", "Karamelle", False), ("lemuria", "Lemuria", False),
    ("novus", "Novus", False), ("wallaru", "Wallaru", False), ("selenopolis", "Selenopolis", False),
]
# Wizard404's world names for each.
W404_WORLD = {"wizardcity": "WizardCity", "catacombs": "WizardCity"}


def number(value):
    """A whole number from a count or level the pages give as a number or as text ("3", "10+")."""
    if isinstance(value, (int, float)):
        return int(value)
    digits = re.match(r"\d+", str(value or "").strip())
    return int(digits.group()) if digits else 0


def norm(text):
    text = html.unescape(text or "").lower().replace("…", "...").replace("’", "'")
    return re.sub(r"[^a-z0-9]+", "", text)


def plain(text):
    text = re.sub(r"<br\s*/?>", " ", text or "")
    text = re.sub(r"<[^>]+>", "", text)
    return re.sub(r"\s+", " ", html.unescape(text)).strip()


def w404_quests(folder):
    quests = {}
    for path in sorted(glob.glob(os.path.join(folder, "api-quest*.jsonl.gz"))):
        with gzip.open(path, "rt", encoding="utf-8") as f:
            for line in f:
                q = json.loads(line)["data"]["quest"]["parsed_data"]
                quests[q["template_id"]] = q
    return list(quests.values())


def persona_name(g):
    shown = (g.get("persona_display_name") or "").strip()
    code = (g.get("persona") or "").strip()
    return shown or ("" if code.endswith("_Persona") or code.endswith("_persona") else code)


def goal_text(g):
    title = (g.get("goal_title") or "").strip()
    what = persona_name(g) or (g.get("tally_text") or g.get("tally_text_extra") or "").strip()
    count = g.get("bounty_total") or g.get("goal_total") or 0
    text = title
    if what and what.lower() != title.lower():
        text += f" {count} {what}" if count and count > 1 else f" {what}"
    place = (g.get("destination_zone_display_name") or (g.get("location_name") or "").split("|")[-1]).strip()
    if place and place.lower() not in text.lower():
        text += f" (in {place})"
    return text.strip()


def details(q):
    goals = sorted(q.get("goals") or [], key=lambda g: g.get("order") or 0)
    # The NPC who offers it: the speaker of its offer lines (the first ones, or the "quest_info" stage).
    names = {g.get("persona"): persona_name(g) for g in goals if g.get("persona")}
    lines = q.get("dialog_list") or []
    offer = next((d for d in lines if d.get("stage") == "quest_info" and d.get("persona")), None) \
        or next((d for d in lines if not d.get("stage") and d.get("persona")), None)
    giver = ""
    if offer:
        giver = names.get(offer["persona"]) or ("" if offer["persona"].lower().endswith("_persona") else offer["persona"])
    goals = sorted(q.get("goals") or [], key=lambda g: g.get("order") or 0)
    xp = gold = tp = 0
    items, spells, other = [], [], []
    for r in q.get("end_results") or []:
        kind = r.get("result_type", "")
        if kind == "class ResAddMagicXP":
            xp += r.get("experience") or 0
        elif kind == "class ResAddGold":
            gold += r.get("gold") or 0
        elif kind == "class ResItemLoot" and r.get("item_template_id"):
            items.append(r["item_template_id"])
        elif kind == "class ResAddSpell" and r.get("spell_name"):
            spells.append(r["spell_name"])
        elif kind == "class ResAddTrainingPoints":
            tp += 1
        elif kind == "class ResAddBadge":
            other.append("A badge")
        elif kind == "class ResAddTreasureSpell":
            other.append("A treasure card")
        elif kind == "class ResAddCraftingSlot":
            other.append("A crafting slot")
        elif kind == "class ResSpellement":
            other.append("Spellements")
        elif kind == "class ResAddElixir":
            other.append("An elixir")
    out = {"Id": q["template_id"], "Giver": giver, "Goals": [goal_text(g) for g in goals]}
    if (q.get("quest_level") or 0) > 1:
        out["Level"] = q["quest_level"]
    if xp: out["Xp"] = xp
    if gold: out["Gold"] = gold
    if tp: out["Tp"] = tp
    if items: out["Items"] = items
    if spells: out["Spells"] = spells
    if other: out["Other"] = sorted(set(other))
    return out


def main(st_path, w404_folder, out_path):
    st = json.load(open(st_path, encoding="utf-8"))
    w404 = w404_quests(w404_folder)
    by_name = collections.defaultdict(list)
    for q in w404:
        by_name[norm(q.get("quest_title") or q.get("display_name"))].append(q)

    def match(name, world_key, main):
        found = by_name.get(norm(name), [])
        if not found:
            return None
        world = W404_WORLD.get(world_key, world_key)
        same = [q for q in found if any(norm(w) == norm(world) for w in q.get("associated_worlds") or [])]
        pool = same or found
        pool = sorted(pool, key=lambda q: (bool(q.get("mainline")) != main, -len(q.get("goals") or [])))
        return pool[0]

    matched = total = 0
    worlds = []
    for key, name, side_world in WORLDS:
        mains = []
        for q in st["main"].get(key, []):
            ner = q.get("nerothon") or {}
            steps = []
            for s in q.get("steps") or []:
                step = {"Kind": s.get("type", "")}
                text = plain(s.get("description_short") or s.get("description"))
                if text:
                    step["Text"] = text
                if number(s.get("count")) > 1:
                    step["Count"] = number(s["count"])
                flags = [f for f in ("instance", "solo", "optional", "double") if s.get(f)]
                if flags:
                    step["Flags"] = flags
                steps.append(step)
            row = {"Name": plain(q["name"]), "Area": q.get("area") or "", "Steps": steps,
                   "Xp": number(q.get("xp")), "Gold": number(q.get("gold")),
                   "Prev": ner.get("previousQuests") or [], "Next": ner.get("nextQuests") or []}
            if q.get("instance"): row["Instance"] = True
            if q.get("solo"): row["Solo"] = True
            if number(ner.get("level")): row["Level"] = number(ner["level"])
            if ner.get("location") and ner["location"] != row["Area"]: row["Location"] = ner["location"]
            found = match(row["Name"], key, True)
            total += 1
            if found:
                matched += 1
                row["Wiki"] = details(found)
            mains.append(row)

        def chain(c, area):
            row = {"Name": plain(c["name"]), "Area": c.get("area") or area, "Xp": number(c.get("xp")), "Gold": number(c.get("gold"))}
            if c.get("unlockedBy"): row["UnlockedBy"] = c["unlockedBy"]
            if number(c.get("level")): row["Level"] = number(c["level"])
            found = match(row["Name"], key, False)
            if found:
                row["Wiki"] = details(found)
            kids = [chain(k, row["Area"]) for k in c.get("children") or []]
            if kids: row["Next"] = kids
            return row

        sides = [chain(c, a["area"]) for a in st["side"].get(key, []) for c in a.get("chains") or []]
        worlds.append({"Key": key, "Name": name, "SideWorld": side_world, "Main": mains, "Side": sides})

    data = json.dumps({"Source": "Spiral Tracker order, Wizard404 quest details", "Worlds": worlds},
                      separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    with open(out_path, "wb") as f:
        f.write(gzip.compress(data, compresslevel=9, mtime=0))
    print(f"{total} main quests, {matched} with Wizard404 details; {len(data):,} bytes, {os.path.getsize(out_path):,} gzipped")


if __name__ == "__main__":
    main(*sys.argv[1:4])
