"""
Makes data/quest-index.csv, one row per quest file of data/spiraldb/QuestTemplates: its world, code, title, file and
whether it is a new quest or replaces SpiralDB's quest of the same code. GitHub shows the CSV as a searchable table.
It also prints the per-world counts for the table in data/README.md.

Run from the repo root: python3 -I tools/data-index/make_quest_index.py [<a SpiralDB checkout>]
Without a SpiralDB checkout every quest is listed as "new".
"""
import collections
import csv
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
QUESTS = os.path.join(ROOT, "data", "spiraldb", "QuestTemplates")
OUT = os.path.join(ROOT, "data", "quest-index.csv")

# The worlds by quest code (the first part of it), as the Quests tab has them (ManagerWindow.Quests.cs s_questWorlds).
CODE_WORLDS = {
    "WC": "Wizard City", "KT": "Krokotopia", "MB": "Marleybone", "MS": "Mooshu", "DS": "Dragonspyre",
    "GH": "Grizzleheim", "CL": "Celestia", "PA": "Wysteria", "WY": "Wysteria", "ZF": "Zafaria", "AV": "Avalon",
    "AZ": "Azteca", "AQ": "Aquila", "KR": "Khrysalis", "KH": "Khrysalis", "PL": "Polaris", "AR": "Arcanum",
    "MR": "Mirage", "EM": "Empyrea", "KM": "Karamelle", "LM": "Lemuria", "NV": "Novus", "WL": "Wallaru",
    "SL": "Selenopolis", "DM": "Darkmoor", "WT": "Wintertusk", "ZA": "Zafaria",
}
WORLD_ORDER = ["Wizard City", "Krokotopia", "Marleybone", "Mooshu", "Dragonspyre", "Grizzleheim", "Celestia",
               "Wysteria", "Wintertusk", "Zafaria", "Avalon", "Azteca", "Aquila", "Khrysalis", "Polaris", "Arcanum",
               "Mirage", "Empyrea", "Karamelle", "Lemuria", "Novus", "Wallaru", "Selenopolis", "Darkmoor", "Other"]


def load(path):
    # SpiralDB's JSON has trailing commas.
    with open(path, encoding="utf-8-sig") as f:
        return json.loads(re.sub(r",(\s*[}\]])", r"\1", f.read()))


def spiraldb_codes(checkout):
    codes = set()
    folder = os.path.join(checkout, "QuestTemplates")
    for name in os.listdir(folder):
        if name.endswith(".json"):
            try:
                codes.add(load(os.path.join(folder, name))["m_questName"].lower())
            except Exception:
                pass
    return codes


def main():
    theirs = spiraldb_codes(sys.argv[1]) if len(sys.argv) > 1 else set()
    rows = []
    for name in sorted(os.listdir(QUESTS)):
        if not name.endswith(".json"):
            continue
        quest = load(os.path.join(QUESTS, name))
        code = quest.get("m_questName") or ""
        world = CODE_WORLDS.get(code.split("-")[0].upper(), "Other")
        kind = "replaces SpiralDB's" if code.lower() in theirs else "new"
        rows.append([world, code, quest.get("m_questTitle") or "", kind, "spiraldb/QuestTemplates/" + name])
    rows.sort(key=lambda r: (WORLD_ORDER.index(r[0]), r[1].lower()))
    with open(OUT, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["World", "Quest code", "Title", "Kind", "File"])
        writer.writerows(rows)

    counts = collections.OrderedDict((w, [0, 0]) for w in WORLD_ORDER)
    for r in rows:
        counts[r[0]][0 if r[3] == "new" else 1] += 1
    print("| World | New quests | Replace SpiralDB's |")
    print("|---|---:|---:|")
    for world, (new, fixed) in counts.items():
        if new or fixed:
            print(f"| {world} | {new} | {fixed} |")
    print(f"| **All** | **{sum(c[0] for c in counts.values())}** | **{sum(c[1] for c in counts.values())}** |")


if __name__ == "__main__":
    main()
