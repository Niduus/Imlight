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
# Worlds played after every other one, in this order.
LAST_WORLDS = ["Darkmoor"]

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

# Fixes made for one quest that were never tried in game (from the project's notes). Code -> (why, should happen, version).
GIVER_FIX = ("The quest checker (2026-10-27) found this quest's giver wasn't placed anywhere in the world. Since v0.10.534 the quest "
             "names the placed NPC of the same name, so you can pick it up.",
             "The giver stands in the world with a quest marker over them, and talking to them offers the quest.", "0.10.534")
UNTESTED = {
    'SL-W404-754784803': ('The Arc 4 main quest check (2026-10-31) found its giver was an NPC that stands nowhere, so nobody could offer it and Selenopolis stopped here. Now Brother Helio in the Forgotten Tombs offers it.',
        'After the quest before it, Brother Helio in the Forgotten Tombs has a quest marker and offers this quest.', "0.10.681"),
    'SL-W404-754719267': ('The Arc 4 main quest check (2026-10-31) found its giver was an NPC that stands nowhere, so nobody could offer it and Selenopolis stopped here. Now Brother Helio in the Chamber of Amalthea offers it.',
        'After the quest before it, Brother Helio in the Chamber of Amalthea has a quest marker and offers this quest.', "0.10.681"),
    'SL-W404-754651683': ('The Arc 4 main quest check (2026-10-31) found its giver was an NPC that stands nowhere, so nobody could offer it and Selenopolis stopped here. Now Phoebe offers it.',
        'After the quest before it, Phoebe has a quest marker and offers this quest.', "0.10.681"),
    'SL-W404-754586147': ('The Arc 4 main quest check (2026-10-31) found its giver was an NPC that stands nowhere, so nobody could offer it and Selenopolis stopped here. Now Dione in the Chamber of Darkness offers it.',
        'After the quest before it, Dione in the Chamber of Darkness has a quest marker and offers this quest.', "0.10.681"),
    'WL-W404-448485130': ('The Arc 4 main quest check (2026-10-31) found its talk step named an Opal Bilburrow who stands nowhere. Now it is the Opal in the Juggernaut Crab Theater.',
        'Talking to Opal in the theater ticks the step.', "0.10.681"),
    'WL-W404-552418397': ('The Arc 4 main quest check (2026-10-31) found its talk step named a Judge Veg who stands nowhere. Now it is the Judge Veg in Conatus.',
        'Talking to Judge Veg in Conatus ticks the step.', "0.10.681"),
    'LM-W404-1197532866': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'LM-W404-1198253762': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'LM-W404-1198515906': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'LM-W404-2015057784': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'LM-W404-785409580': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'LM-W404-785540652': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'NV-W404-1028659802': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'NV-W404-1029182042': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'NV-W404-1029511770': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'NV-W404-1029577306': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'NV-W404-650038354': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'NV-W404-650040402': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-1154868716': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-1154999788': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-1366025812': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-1366156884': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-1780226535': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-1780292071': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-448491274': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'WL-W404-763017149': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'KM-KCITY-MAIN-009': ('The Arc 4 main quest check (2026-10-31) found a talk step named the NPC of that name from another place, so the arrow led one way and the NPC stood somewhere else. Now it is the one standing where the arrow points.',
        'Follow the arrow: the NPC is there and talking to them ticks the step.', "0.10.681"),
    'KM-KCITY-MAIN-011': ("The Arc 4 main quest check (2026-10-31) found a step pointed to Nana's Office, a place the game doesn't have. Now it points to Nana's HQ, where Greta stands.",
        "The arrow leads to Greta in Nana's HQ.", "0.10.681"),
    'KM-W404-1058385782': ('The Arc 4 main quest check (2026-10-31) found the Oogie Boogies in the Gym were other copies than the ones the steps count, so the fights never counted.',
        'Each Oogie Boogie fight ticks its step.', "0.10.681"),
    'KM-W404-933916414': ('The Arc 4 main quest check (2026-10-31) found the Ubear in the Power Room was another copy than the one the step counts.',
        'Beating the Ubear ticks the last step.', "0.10.681"),
    'KM-W404-537579593': ("The Arc 4 main quest check (2026-10-31) found the Hazmat Hamster step pointed to the Silo, the photo step wanted a Celestia Gobbler, the lever step levers from other worlds and the last fight creatures that aren't in the Hot House. Now: Hamster in the Hot House, photograph the Sweating Gobblers, use the Sweatbox (a guess: it's the room's only usable object), and Augustus, Charlie, Mikey or Veruca count.",
        'Every step of the Hot House ticks off and the quest ends.', "0.10.681"),
    'KM-W404-1078418835': ("The Arc 4 main quest check (2026-10-31) found Scarlet Shrimpernel waits in the hideout's second room, but the steps pointed to the first.",
        'The arrow leads to the second room and beating Shrimpernel ticks the step.', "0.10.681"),
    'NV-W404-1544180588': ("The Arc 4 main quest check (2026-10-31) found the Aeon of Atavus fight is against the Atavus creatures, which the step didn't count.",
        'Beating the Aeon of Atavus fight ends the quest.', "0.10.681"),
    'AR-PreLM-MAIN-001': ('The Arc 4 main quest check (2026-10-31) found collecting the Mosaic in the Backroom finished just by walking in. Now you click the Mosaic, as in the real game.',
        'Clicking the Mosaic ticks the step.', "0.10.681"),
    'AR-PreLM-MAIN-003': ("The Arc 4 main quest check (2026-10-31) found restoring Lemuria in the Old One's Office finished just by walking in. Now you use the orb holder, as in the real game.",
        'Using the orb holder ticks the step.', "0.10.681"),
    'KM-FARM-MAIN-004': ('The Arc 4 main quest check (2026-10-31) found its Photomance step could never finish: nothing counted photos of the thing to photograph. Since this version a photo of it counts.',
        'Press the photo key at the thing the step names: the step ticks.', "0.10.681"),
    'KM-FARM-MAIN-005': ('The Arc 4 main quest check (2026-10-31) found its Photomance step could never finish: nothing counted photos of the thing to photograph. Since this version a photo of it counts.',
        'Press the photo key at the thing the step names: the step ticks.', "0.10.681"),
    'KM-NIBB-MAIN-007': ('The Arc 4 main quest check (2026-10-31) found its Photomance step could never finish: nothing counted photos of the thing to photograph. Since this version a photo of it counts.',
        'Press the photo key at the thing the step names: the step ticks.', "0.10.681"),
    'KM-W404-1064382326': ('The Arc 4 main quest check (2026-10-31) found its Photomance step could never finish: nothing counted photos of the thing to photograph. Since this version a photo of it counts.',
        'Press the photo key at the thing the step names: the step ticks.', "0.10.681"),
    'KM-W404-537579529': ('The Arc 4 main quest check (2026-10-31) found its Photomance step could never finish: nothing counted photos of the thing to photograph. Since this version a photo of it counts.',
        'Press the photo key at the thing the step names: the step ticks.', "0.10.681"),
    'LM-W404-915715270': ('The Arc 4 main quest check (2026-10-31) found its Photomance step could never finish: nothing counted photos of the thing to photograph. Since this version a photo of it counts.',
        'Press the photo key at the thing the step names: the step ticks.', "0.10.681"),
    'KM-FARM-MAIN-001': ('The Arc 4 main quest check (2026-10-31) found the real game does one more thing when it starts (a world teleport entry, or shows or hides an Arcanum NPC) that ours skipped.',
        'It starts and plays as before.', "0.10.681"),
    'KM-GOBB-MAIN-001': ('The Arc 4 main quest check (2026-10-31) found the real game does one more thing when it starts (a world teleport entry, or shows or hides an Arcanum NPC) that ours skipped.',
        'It starts and plays as before.', "0.10.681"),
    'KM-NIBB-MAIN-001': ('The Arc 4 main quest check (2026-10-31) found the real game does one more thing when it starts (a world teleport entry, or shows or hides an Arcanum NPC) that ours skipped.',
        'It starts and plays as before.', "0.10.681"),
    'AR-PreLM-MAIN-004': ('The Arc 4 main quest check (2026-10-31) found the real game does one more thing when it starts (a world teleport entry, or shows or hides an Arcanum NPC) that ours skipped.',
        'It starts and plays as before.', "0.10.681"),
    'AR-PostLM-MAIN-001': ('The Arc 4 main quest check (2026-10-31) found the real game does one more thing when it starts (a world teleport entry, or shows or hides an Arcanum NPC) that ours skipped.',
        'It starts and plays as before.', "0.10.681"),
    "MS-MAIN-C07-001": ("Rematch: reaching the obelisk in the Jade Palace should spawn the Jade Oni (v0.10.285, PR #412). "
                        "Before, the Oni never came.",
                        "Walk up to the obelisk: the Jade Oni appears and the fight can start.", ""),
    "AZ-COMET-C01-001": ("The Xibalba dungeon quest. The real game has it but no recording of it exists, so it was rebuilt from the game's "
                         "own text and zone scripts (v0.10.594): Yacate, the three Obsidian Mirrors, the Frost, Fire and Thunder Webs, the "
                         "ghost Lords of Night, then Morganthe and Malistaire at the pillar. Speaker names were added in v0.10.607. "
                         "The order of the steps and who says each line are guesses, and it gives no rewards yet.",
                         "Entering Xibalba gives the quest by itself. Each step points to the right mirror, web or boss, each web goes away "
                         "when you smash, burn or freeze it, every line shows who says it, and Malistaire appears at the pillar for the last fight.",
                         "0.10.607"),
    "AZ-ZOCA-C15-002": ("Its 'Explore' step showed a raw code (WizQst9D0EA_00000009) in the quest book; the game dropped that line. "
                        "Fixed in v0.10.593. It is also the quest that leads into Xibalba.",
                        "The quest book shows a real place name for every step, never a code.", "0.10.593"),
    "WC-OLDE-MAIN-003A": ("Its Olde Town tower dialog played when you LEFT the tower instead of when you walked in (seen in your log). "
                          "Since v0.10.614 the server holds a zone's dialog until the game has finished loading it.",
                          "The dialog plays as you arrive in the tower, not when you leave.", "0.10.614"),
    "EM-INNER-MAIN-010": ("The quest checker found its 'Bat' NPC isn't in the game's zone files, so a step may have no one to talk to.",
                          "Every step has someone or something to click. If one doesn't, use Finish on that step and tell me.", ""),
    "DS-LIB-C02-001": ("The quest checker couldn't find the area one step points to, so the arrow may lead nowhere.",
                       "The arrow and marker lead to a real spot and the step finishes there.", ""),
}
for _code in ["DM-W404-891457977", "LM-W404-1851332871", "KR-MARK-C02-001", "MR-CATE-MAIN-007", "MR-ALKA-MAIN-005", "EM-AERO-MAIN-008", "EM-AERI-J-MAIN-008", "AV-WILD-C04-001",
              "DM-W404-1589247807", "DM-W404-1589245759", "DM-W404-1589243711", "DM-W404-1589241663", "DM-W404-1589237567"]:
    UNTESTED.setdefault(_code, GIVER_FIX)

# Quests that are not main story quests but are tested with the story, put right after the main quest given.
EXTRA_ROWS = {"AZ-COMET-C01-001": "AZ-ZOCA-C15-002"}

# Quests whose text pointed to lines the game no longer has, so the quest book showed a raw code (WizQst..._000000NN).
# Since v0.10.593 the server swaps them at boot (QuestTextKeys.ReplaceMissingKeys). Found by scanning every quest
# against the game's Locale/en-US text (Root.wad), 2026-10-28.
RAW_TEXT = {
    "AR-PreLM-MAIN-003": "a step's text", "DS-LIB3-C01-004": "a step's place", "KT-BAL-C06-001": "the name over a line",
    "WC-MISC-C01-001": "a dialog page", "WC-PreKR-C02-001": "a dialog page", "WC-PreKR-C03-001": "a dialog page",
    "WC-PreKR-C03-002": "a dialog page", "WC-PreKR-C04-001": "a dialog page", "WC-PreKR-C04-002": "a dialog page",
    "WC-PreKR-C05-001": "a dialog page", "WC-PreKR-C06-001": "two dialog pages", "WL-W404-595066559": "a step's text",
    "AZ-ZOCA-C15-002": "a step's text",
}

# Quests Spiral Tracker lists twice under two spellings.
SPELLED_TWICE = [{"prawntokingsfourth", "prawntokingsforth"}]

# Kinds of flag for fixes made from v0.10.590 on: the Manager's "Recent fixes" view shows them.
RECENT_KINDS = {"Text fixed", "Dialog on arrival", "Fight counts now"}


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
            # Spiral Tracker lists a quest once per area it passes through ("La Mort Et Lemuria" three times): one row.
            if any(norm(r["Name"]) == norm(m["Name"]) or {norm(r["Name"]), norm(m["Name"])} in SPELLED_TWICE for r in rows):
                continue
            q = match(m["Name"], world_name)
            if q is not None and q["Code"] in placed:
                if any(r["Code"] == q["Code"] for r in rows):
                    continue
            rows.append({"Tree": m, "Code": q["Code"] if q else "", "Name": m["Name"].strip(), "Area": m.get("Area", "")})
            if q:
                placed.add(q["Code"])
        worlds.append({"Name": w["Name"], "SideWorld": w["SideWorld"], "Key": w["Key"], "World": world_name, "Rows": rows})

    # Server main quests the tree doesn't list.
    extra_mains = [q for q in details["Quests"] if q["Main"] and q["Code"] not in placed]
    for code, after in EXTRA_ROWS.items():
        for w in worlds:
            spot = next((i for i, r in enumerate(w["Rows"]) if r["Code"] == after), None)
            if code in server and spot is not None and code not in placed:
                w["Rows"].insert(spot + 1, {"Tree": None, "Code": code, "Name": server[code]["Title"], "Area": w["Rows"][spot]["Area"], "Extra": True})
                placed.add(code)
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

            def flag(kind, why, check, recent=False):
                flags.append({"Kind": kind, "Why": why, "Check": check, **({"Recent": True} if recent or kind in RECENT_KINDS else {})})
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
                     "(v0.10.422), and the sigil only lets you in with this quest (v0.10.486). Since v0.10.590 a sigil you can't use yet stays dark, and "
                     "since v0.10.604 a sigil whose lock the server can never open (an unknown quest or step) counts as open.",
                     "The sigil lights up once you have this quest, takes you (and your team) inside, and you come back out where you went in.")

            for z in sorted(dz):
                top = z.split("/")[0]
                if top in ZONE_WORLDS and top not in opened_worlds:
                    opened_worlds.add(top)
                    flag("Opens a world", f"The first quest that goes to {ZONE_WORLDS[top]}: having it opens {ZONE_WORLDS[top]} on the Spiral Door for good (v0.10.478).",
                         f"Once you have it, the Spiral Door lists {ZONE_WORLDS[top]}.")

            if code in RAW_TEXT:
                flag("Text fixed", f"In the quest book, {RAW_TEXT[code]} showed a raw code (like WizQst9D0EA_00000009) because the game no longer "
                     "has that line. Since v0.10.593 the server puts the place name or the giver's last line there, or leaves the page out.",
                     "No step, place, name or dialog page shows a code; everything reads as normal words.")

            arrivals = [i for i, x in enumerate(rq.get("m_goals", []), 1) if x.get("m_goalType") == "GOAL_TYPE_WAYPOINT"
                        and any(d.get("m_dialogEntries") for d in ((x.get("m_dialogList") or {}).get("m_dialogs") or []) if isinstance(d, dict))]
            if arrivals:
                flag("Dialog on arrival", f"Step {', '.join(map(str, arrivals))} plays a dialog when you arrive somewhere. These could play late, even when "
                     "you left again; since v0.10.614 the server waits for the game to finish loading the area first.",
                     "The dialog plays right as you arrive, not later or when you leave.")

            if src == {"ours"} and any(x.get("m_goalType") == "GOAL_TYPE_BOUNTY"
                                       and any("-NPC" in a for a in x.get("m_npcAdjectives") or []) for x in rq.get("m_goals", [])):
                flag("Fight counts now", "A 'defeat' step named the talking NPC instead of the creatures you fight, so no battle counted. "
                     "Since v0.10.534 beating a creature with that NPC's name counts (found by the quest checker).",
                     "Winning the fight ticks the defeat step.")

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
            if m is None and not r.get("Extra"):
                flag("Not in the story list", "A main quest on the server that Spiral Tracker doesn't list; it's shown after the quest it needs.",
                     "It's offered after the quest above and fits the story.")

            if not q["GiverName"] and not q["StartsByItself"] and not r.get("Extra"):
                flag("No giver", "No NPC gives this quest on the server and it doesn't start by itself, so the story may stop here.",
                     "It's offered once the quest before is done. If not, press Start.")
            gone = [n for n in q["Needs"] if n not in server]
            if gone:
                flag("Needs a missing quest", "It needs " + ", ".join(gone) + ", which the server doesn't have, so it may never be offered.",
                     "It's offered anyway. If not, press Start.")

            if code in UNTESTED:
                why, check, since = UNTESTED[code]
                flag("Untested fix", why, check, recent=bool(since))

            do = []
            if r.get("Extra"):
                do.append("It starts by itself when you walk into " + (q["Goals"][0]["Place"].split(",")[-1] if q["Goals"] else "its area") + ".")
            elif q["StartsByItself"]:
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

    # Darkmoor's world comes after Selenopolis (the Spiral Tracker lists it after Khrysalis, as a side world).
    late = [w for w in out_worlds if w["Name"] in LAST_WORLDS]
    out_worlds = [w for w in out_worlds if w["Name"] not in LAST_WORLDS] + [dict(w, SideWorld=False) for w in late]

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
