"""
Builds src/Imlight.Setup/Assets/quest-testing.json.gz, the Manager's Quest Testing tab data (Quests page).

Every main story quest in the order it is played (the Quest Tree's Spiral Tracker order), joined with the server's own
quests, and for each one what may be wrong with it on this server (why it is flagged), what to do to test it and what
should happen. Niduus, 2026-10-27: "Update my quest testing section in Ahziael for ALL Main quest that may have issues
to test with descriptions of why they were flagged, what's the expected outcome. Make it from Point A to B".

    python3 tools/quest-testing/build_quest_testing.py <id-lookup folder> <spiraldb folder> <Quest reward fixes.md> <out .json.gz>

<id-lookup folder> is the server's id-lookup folder (quest-details.json and quest-guide.txt, written as it starts);
<spiraldb folder> a clone of github.com/Revive101/spiraldb; the reward list is imlight/quests/Quest reward fixes.md in
the project files (made with PR #617). Everything else is read from this repository.
"""
import collections
import glob
import gzip
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXTRA = os.path.join(ROOT, "src", "Imlight.Director", "spiraldb-extra")
TREE = os.path.join(ROOT, "src", "Imlight.Setup", "Assets", "quest-tree.json.gz")

# The worlds by quest code, as the Manager's Quests tab has them (ManagerWindow.Quests.cs s_questWorlds).
CODE_WORLDS = {
    "WC": "Wizard City", "KT": "Krokotopia", "MB": "Marleybone", "MS": "Mooshu", "DS": "Dragonspyre", "GH": "Grizzleheim",
    "CL": "Celestia", "WY": "Wysteria", "ZF": "Zafaria", "AV": "Avalon", "AZ": "Azteca", "KR": "Khrysalis",
    "KH": "Khrysalis", "PL": "Polaris", "MR": "Mirage", "EM": "Empyrea", "KM": "Karamelle", "LM": "Lemuria",
    "NV": "Novus", "WL": "Wallaru", "SL": "Selenopolis", "DM": "Darkmoor", "AQ": "Aquila", "WT": "Wintertusk",
}
# Zone folders (first part of a zone name) to world names.
ZONE_WORLDS = {
    "WizardCity": "Wizard City", "Krokotopia": "Krokotopia", "Marleybone": "Marleybone", "MooShu": "Mooshu",
    "DragonSpire": "Dragonspyre", "Grizzleheim": "Grizzleheim", "Celestia": "Celestia", "Wysteria": "Wysteria",
    "Zafaria": "Zafaria", "Avalon": "Avalon", "Azteca": "Azteca", "Aquila": "Aquila", "Khrysalis": "Khrysalis",
    "Polaris": "Polaris", "Mirage": "Mirage", "Empyrea": "Empyrea", "Karamelle": "Karamelle", "Lemuria": "Lemuria",
    "Novus": "Novus", "Wallaru": "Wallaru", "Darkmoor": "Darkmoor", "Selenopolis": "Selenopolis",
}
# Spiral Door worlds open from the start (WorldAccess.s_alwaysOpen).
ALWAYS_OPEN = {"WizardCity", "Grizzleheim", "Wysteria"}

# Quest results the server has no handler for (src/Imlight.CoreLib/Game/Results/Handlers), in plain words.
UNHANDLED = {
    "ResReInteract": "talks to the NPC again by itself after this step (the server can't, so you may have to click the NPC again)",
    "ResAddMissionDoor": "adds a mission door (the server can't add it, so a door may stay shut)",
    "ResRemoveMissionDoor": "removes a mission door (the server can't, so an old door may stay)",
    "ResMarkZoneNoWarn": "lets you leave the area without the 'are you sure' warning (the server can't, so the warning may show)",
    "ResAddTreasureSpell": "gives a treasure card (the server can't give it this way)",
    "ResAddTrainingPoints": "gives a training point (the server can't give it this way)",
    "ResAddEncounterXP": "gives battle XP (the server can't give it this way)",
    "ResSetGardeningLevel": "sets your gardening rank (the server can't)",
}

# Fixes made for one quest that were never tried in game (from the project's notes).
UNTESTED = {
    "MS-MAIN-C07-001": ("Untested fix", "Rematch: reaching the obelisk in the Jade Palace should spawn the Jade Oni (v0.10.285, PR #412). "
                        "Before, the Oni never came.",
                        "Walk up to the obelisk: the Jade Oni appears and the fight can start."),
}


def load_json(path):
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    text = re.sub(r"(?m)^\s*//.*$", "", text)
    text = re.sub(r",(\s*[\]}])", r"\1", text)
    return json.loads(text)


def comments_and_entries(path):
    """QuestGoalEvents.json entries, each with the comment line above it."""
    out = []
    comment = ""
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            s = line.strip()
            if s.startswith("//"):
                comment = s[2:].strip()
            elif s.startswith("{"):
                try:
                    entry = json.loads(s.rstrip(","))
                except json.JSONDecodeError:
                    continue
                out.append((comment, entry))
    return out


def raw_quests(spiraldb):
    quests, source = {}, collections.defaultdict(set)
    for folder, tag in [(os.path.join(spiraldb, "QuestTemplates"), "game"), (os.path.join(EXTRA, "QuestTemplates"), "ours")]:
        for path in glob.glob(os.path.join(folder, "*.json")):
            try:
                data = load_json(path)
            except (json.JSONDecodeError, UnicodeDecodeError):
                continue
            for q in data if isinstance(data, list) else [data]:
                name = q.get("m_questName") if isinstance(q, dict) else None
                if name:
                    quests[name] = q
                    source[name].add(tag)
    return quests, source


def walk_types(obj, found):
    if isinstance(obj, dict):
        t = obj.get("$type", "")
        if t:
            found.add(t.split(",")[0].split(".")[-1])
        for v in obj.values():
            walk_types(v, found)
    elif isinstance(obj, list):
        for v in obj:
            walk_types(v, found)


def norm(text):
    text = (text or "").lower().replace("’", "'").replace("…", "...")
    return re.sub(r"[^a-z0-9]+", "", text)


def world_of(code, zone=""):
    prefix = re.split(r"[-_]", code)[0].upper()
    if prefix in CODE_WORLDS:
        return CODE_WORLDS[prefix]
    return ZONE_WORLDS.get(zone.split("/")[0], zone.split("/")[0] or "Other")


def reward_fixes(path):
    """Code -> (section, change) from the PR #617 list."""
    out = {}
    section = ""
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("## "):
                section = re.sub(r"\s*\(\d+\)\s*$", "", line[3:].strip())
            m = re.match(r"- .*? \(([^()]+)\): (.+)$", line.strip())
            if m and section and not section.startswith("Not changed"):
                out.setdefault(m.group(1), []).append(m.group(2))
    return out


def gold(r):
    return f"{r['GoldMin']:,}" if r["GoldMin"] == r["GoldMax"] else f"{r['GoldMin']:,} to {r['GoldMax']:,}"


def main(lookup, spiraldb, rewards_md, out_path):
    details = load_json(os.path.join(lookup, "quest-details.json"))
    for q in details["Quests"]:
        q["Title"] = q["Title"].strip()
    server = {q["Code"]: q for q in details["Quests"]}
    guide = {}
    with open(os.path.join(lookup, "quest-guide.txt"), encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) > 7:
                guide[p[0]] = {"Giver": p[5], "Zone": p[6], "From": p[7]}
    raw, source = raw_quests(spiraldb)
    tree = json.load(gzip.open(TREE, "rt", encoding="utf-8"))
    fixes = reward_fixes(rewards_md)
    spells = collections.defaultdict(list)
    for s in load_json(os.path.join(EXTRA, "QuestSpellRewards.json")):
        spells[s["quest"]].append(f"{s.get('name') or s['spell']}" + (f" ({s['school']} wizards)" if s.get("school") else ""))
    events = collections.defaultdict(list)
    for comment, e in comments_and_entries(os.path.join(EXTRA, "QuestGoalEvents.json")):
        events[e["quest"]].append((comment, e))
    dungeon_zones = set()
    for path in glob.glob(os.path.join(EXTRA, "DungeonTeleports", "*.json")):
        for zone in load_json(path):
            for t in zone.get("Teleports", []):
                dest = (t.get("Teleport") or {}).get("m_destinationZone")
                if dest:
                    dungeon_zones.add(dest)

    # The quests that follow each quest on the server (they need it).
    followers = collections.defaultdict(list)
    for q in details["Quests"]:
        for need in q["Needs"]:
            followers[need].append(q["Code"])

    by_title = collections.defaultdict(list)
    for q in details["Quests"]:
        by_title[norm(q["Title"])].append(q)

    tree_names = {(norm(m["Name"]), "Wizard City" if w["Key"] == "catacombs" else w["Name"]) for w in tree["Worlds"] for m in w["Main"]}

    def match(name, world_name):
        found = by_title.get(norm(name), [])
        if not found:
            return None
        def wo(q):
            return world_of(q["Code"], guide.get(q["Code"], {}).get("Zone", ""))
        best = sorted(found, key=lambda q: (wo(q) != world_name, not q["Main"], q["Code"]))[0]
        if wo(best) != world_name and (norm(name), wo(best)) in tree_names:
            return None
        return best

    # Story order: each world's main quests as the tree has them, with the server's main quests that the tree lacks
    # put right after the quest they need.
    worlds = []
    placed = set()
    opened_worlds = set(ALWAYS_OPEN)
    for w in tree["Worlds"]:
        world_name = "Wizard City" if w["Key"] == "catacombs" else w["Name"]
        rows = []
        for m in w["Main"]:
            q = match(m["Name"], world_name)
            if q is not None and q["Code"] in placed:
                q = None if any(r["Code"] == q["Code"] for r in rows) else q
            rows.append({"Tree": m, "Code": q["Code"] if q else "", "Name": m["Name"].strip(), "Area": m.get("Area", "")})
            if q:
                placed.add(q["Code"])
        worlds.append({"Name": w["Name"], "SideWorld": w["SideWorld"], "Key": w["Key"], "World": world_name, "Rows": rows})

    # Server main quests the tree doesn't list.
    extra_mains = [q for q in details["Quests"] if q["Main"] and q["Code"] not in placed]
    pending = list(extra_mains)
    for _ in range(6):
        left = []
        for q in pending:
            spot = None
            for wi, w in enumerate(worlds):
                for ri, r in enumerate(w["Rows"]):
                    if r["Code"] and r["Code"] in q["Needs"]:
                        spot = (wi, ri)
            if spot is None:
                left.append(q)
                continue
            wi, ri = spot
            rows = worlds[wi]["Rows"]
            # The same quest once per school ("To Ravenwood!"): one row, with each school's copy.
            twin = next((x for x in rows if x["Code"] and norm(x["Name"]) == norm(q["Title"])
                         and abs(rows.index(x) - ri) <= 8), None)
            if twin is not None:
                twin.setdefault("Copies", []).append(q["Code"])
                placed.add(q["Code"])
                continue
            worlds[wi]["Rows"].insert(ri + 1, {"Tree": None, "Code": q["Code"], "Name": q["Title"], "Area": worlds[wi]["Rows"][ri]["Area"]})
            placed.add(q["Code"])
        pending = left
    for q in sorted(pending, key=lambda q: q["Code"]):
        world_name = world_of(q["Code"], guide.get(q["Code"], {}).get("Zone", ""))
        target = next((w for w in worlds if w["World"] == world_name and not w["SideWorld"]), None) \
            or next((w for w in worlds if w["World"] == world_name), None)
        if target is None:
            target = {"Name": world_name, "SideWorld": False, "Key": norm(world_name), "World": world_name, "Rows": []}
            worlds.append(target)
        target["Rows"].append({"Tree": None, "Code": q["Code"], "Name": q["Title"], "Area": "Not in the story list"})
        placed.add(q["Code"])

    counts = collections.Counter()
    out_worlds = []
    for w in worlds:
        quests = []
        for number, r in enumerate(w["Rows"], 1):
            code = r["Code"]
            m = r["Tree"]
            wiki = (m or {}).get("Wiki")
            flags = []

            def flag(kind, why, check):
                flags.append({"Kind": kind, "Why": why, "Check": check})
                counts[kind] += 1

            if not code:
                later = next((x for x in w["Rows"][number:] if x["Code"]), None)
                flag("Not on the server", "The real game has this quest, but the server doesn't, so it can't be played here and the story may stop.",
                     f"Skip it: check the next quest{(' (' + later['Name'] + ')') if later else ''} is still offered. If not, press Start on that one.")
                quests.append({"Number": number, "Name": r["Name"], "Area": r["Area"], "Code": "", "Flags": flags,
                               "Do": [], "Expect": ["Can't be played on this server yet."]})
                continue

            copies = r.get("Copies", [])
            if copies:
                flag("One per school", "The server has a copy of this quest for each school: " + ", ".join([code] + copies)
                     + ". You only get your own school's.", "Your school's copy is the one offered.")
            q = server[code]
            rq = raw.get(code, {})
            src = source.get(code, set())
            g = guide.get(code, {})
            goals = q["Goals"]

            if src == {"ours"}:
                flag("Rebuilt by us", "Not a recording of the real game: its steps, NPCs, places and rewards were rebuilt from Wizard404's copy, so a step may "
                     "point to the wrong NPC or place, or never finish.",
                     "Each step's arrow and marker lead somewhere real, every step finishes, and the dialog makes sense.")
            elif src == {"game", "ours"}:
                flag("Changed by us", "Recorded from the real game, but we replaced it with our own copy to fix it.",
                     "Every step finishes and the quest ends normally.")

            if code in fixes:
                flag("Rewards changed", "Rewards changed to match the real game in v0.10.477: " + "; ".join(fixes[code]) + ".",
                     "")
            taught = [sp for c in [code] + copies for sp in spells.get(c, [])]
            if taught:
                flag("Teaches a spell", "Since v0.10.463 it teaches " + ", ".join(taught) + " like the real game.",
                     "When it ends the spell is in your spellbook.")

            for comment, e in events.get(code, []):
                goal_names = [x.get("m_goalName") for x in rq.get("m_goals", [])]
                step = ""
                if e.get("goal") in goal_names:
                    step = f"step {goal_names.index(e['goal']) + 1} {'starts' if e['when'] == 'start' else 'is done'}"
                else:
                    step = f"the quest {'starts' if e['when'] == 'start' else 'ends'}"
                what = comment.rstrip(".") if comment and not comment.startswith(code) else "a zone script waits for it"
                flag("Spawns or opens something", f"When {step}, the server now sends the area's signal ({', '.join(e['events'])}) the real game sends: {what}. "
                     "Added v0.10.285 to v0.10.290, never tried in game.",
                     f"When {step}, the boss, door or scene the area waits for appears.")

            dz = {x.get("m_destinationZone") for x in rq.get("m_goals", []) if x.get("m_destinationZone")}
            dungeon = sorted(z for z in dz if z in dungeon_zones)
            if dungeon:
                flag("Dungeon", "Goes into " + ", ".join(z.split("/")[-1] for z in dungeon) + ". Where its sigil or door leads came from the game files "
                     "(v0.10.422), and the sigil only lets you in with this quest (v0.10.486).",
                     "The sigil or door takes you (and your team) inside, and you come back out where you went in.")

            for z in sorted(dz):
                top = z.split("/")[0]
                if top in ZONE_WORLDS and top not in opened_worlds:
                    opened_worlds.add(top)
                    flag("Opens a world", f"The first quest that goes to {ZONE_WORLDS[top]}: having it opens {ZONE_WORLDS[top]} on the Spiral Door for good (v0.10.478).",
                         f"Once you have it, the Spiral Door lists {ZONE_WORLDS[top]}.")

            found = set()
            walk_types(rq, found)
            missing = [UNHANDLED[t] for t in sorted(found) if t in UNHANDLED]
            if missing:
                flag("Step the server can't do", "In the real game this quest " + "; and ".join(missing) + ".",
                     "The quest still carries on to the end.")
            if any(x["Kind"] == "Other" for x in goals):
                flag("Step the server can't do", "Has a 'reach a rank' step the server has no handler for, so it may never tick.",
                     "The step ticks once you are the level it asks for. If not, use Finish.")

            if src == {"ours"} and any(x["Kind"] == "Collect" for x in goals):
                flag("Click an object", "A rebuilt 'collect' or 'use' step: the thing to click was matched by its tag name, not recorded from the game.",
                     "The object glows or can be clicked, and each click counts.")

            if wiki and wiki.get("Goals") and len(wiki["Goals"]) != len(goals):
                flag("Steps differ", f"The server has {len(goals)} steps; the real game (Wizard404) has {len(wiki['Goals'])}.",
                     "The quest still makes sense and ends where the real one does.")
            if m is not None and not wiki:
                flag("No real-game copy", "Wizard404 has no copy of this quest to check the server's steps and rewards against.",
                     "The steps and rewards look right.")
            if m is None:
                flag("Not in the story list", "A main quest on the server that Spiral Tracker doesn't list; it's shown after the quest it needs.",
                     "It's offered after the quest above and fits the story.")

            if not q["GiverName"] and not q["StartsByItself"]:
                flag("No giver", "No NPC gives this quest on the server and it doesn't start by itself, so the story may stop here.",
                     "It's offered once the quest before is done. If not, press Start.")
            gone = [n for n in q["Needs"] if n not in server]
            if gone:
                flag("Needs a missing quest", "It needs " + ", ".join(gone) + ", which the server doesn't have, so it may never be offered.",
                     "It's offered anyway. If not, press Start.")

            if code in UNTESTED:
                flag(*UNTESTED[code])

            do = []
            if q["StartsByItself"]:
                do.append("It starts by itself when the quest before it is done.")
            elif q["GiverName"]:
                do.append(f"Get it from {q['GiverName']}.")
            for i, x in enumerate(goals, 1):
                text = x["Text"] or x["Kind"]
                place = x["Place"].replace(",", ", ")
                if x["Place"]:
                    text = text.replace(x["Place"], place)
                if place and x["Place"].split(",")[-1].lower() not in text.lower():
                    text += f" ({place})"
                do.append(f"{i}. {text}")

            rw = q["Rewards"]
            parts = [f"{rw['Xp']:,} XP", f"{gold(rw)} gold"]
            if rw["TrainingPoints"]:
                parts.append(f"{rw['TrainingPoints']} training point{'s' if rw['TrainingPoints'] > 1 else ''}")
            parts += rw["Items"]
            parts += [f"the spell {s}" for s in taught]
            expect = ["Every step ticks off in the quest book and the quest ends.", "You get " + ", ".join(parts) + "."]
            nxt = [server[c] for c in followers.get(code, []) if server[c]["Main"]]
            if nxt:
                expect.append("Then: " + "; ".join(f"{n['Title']}" + (f" from {n['GiverName']}" if n["GiverName"] else " starts by itself" if n["StartsByItself"] else "")
                                                    for n in nxt[:4]) + ".")
            for f_ in flags:
                if f_["Check"] and f_["Check"] not in expect:
                    expect.append(f_["Check"])

            quests.append({"Number": number, "Name": q["Title"] or r["Name"], "Area": r["Area"], "Code": code, "Copies": copies, "Level": q["Level"],
                           "Giver": q["GiverName"], "Zone": g.get("Zone", ""), "Flags": flags, "Do": do, "Expect": expect})
        # A world the server has none of (Catacombs, Selenopolis) can't be played at all; leave it out.
        if any(x["Code"] for x in quests):
            out_worlds.append({"Name": w["Name"], "SideWorld": w["SideWorld"], "Quests": quests})

    out = {"Source": "Spiral Tracker order, the server's quests (v" + open(os.path.join(lookup, "..", "ahziael-version.txt")).read().strip()
           + ") and this repository's quest fixes" if os.path.exists(os.path.join(lookup, "..", "ahziael-version.txt")) else "",
           "Worlds": out_worlds}
    with gzip.open(out_path, "wt", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))
    total = sum(len(w["Quests"]) for w in out_worlds)
    flagged = sum(1 for w in out_worlds for q in w["Quests"] if q["Flags"])
    print(f"{total} main quests, {flagged} flagged")
    for k, v in counts.most_common():
        print(f"  {v:5} {k}")


if __name__ == "__main__":
    main(*sys.argv[1:5])
