r"""
Turn the quest data download.py saved (raw\*.json, Wizard404 API answers) into server quest files, in the same shape as
the Khrysalis quests rebuilt from Wizard404 (spiraldb-extra\QuestTemplates, DropTables).

Writes to out\ only, never into the server:
  out\QuestTemplates\questtemplates_<QUEST>.json
  out\DropTables\droptables_<quest>.json      gold, experience and item rewards
  out\report.txt                              what could not be filled in, per quest and step
Quests the server already has are skipped (pass --overwrite to rebuild them too).

Usage: python convert.py [--server <ImlightManager\Server folder>] [--overwrite]
"""
import argparse
import collections
import glob
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")
OUT = os.path.join(HERE, "out")
DEFAULT_SERVER = os.path.normpath(os.path.join(HERE, "..", "..", "..", "Server"))
T = "Imcodec.ObjectProperty.TypeCache.{0}, Imcodec.ObjectProperty"

GOAL_CLASS = {
    "GOAL_TYPE_PERSONA": "PersonaGoalTemplate",
    "GOAL_TYPE_WAYPOINT": "WaypointGoalTemplate",
    "GOAL_TYPE_BOUNTY": "BountyGoalTemplate",
    "GOAL_TYPE_BOUNTYCOLLECT": "BountyGoalTemplate",
    "GOAL_TYPE_COLLECTAFTERBOUNTY": "BountyGoalTemplate",
    "GOAL_TYPE_USAGE": "ScavengeGoalTemplate",
    "GOAL_TYPE_SCAVENGE": "ScavengeGoalTemplate",
}
# Keys a goal may carry with the creatures or objects it is about (unknown until real downloads are seen).
ADJECTIVE_KEYS = ("npc_adjectives", "adjectives", "mob_adjectives", "item_adjectives", "target_adjectives")
TAG_KEYS = ("client_tags", "goal_client_tags", "object_tags")


def quest_of(payload):
    q = (payload.get("data") or {}).get("quest") or payload.get("quest") or payload
    return q.get("parsed_data") or q, q


def text(v):
    return v if isinstance(v, str) else ""


def listish(v):
    """A list of names from a list or a comma/semicolon separated string."""
    if isinstance(v, str):
        v = re.split(r"[,;]", v)
    return [x.strip() for x in (v or []) if isinstance(x, str) and x.strip()]


class Npcs:
    """
    NPC persona IDs by display name. Newer Wizard404 records name a speaker or talk target by its display name only
    ("Captain Argento"), and one name can belong to several NPC objects (NV-PUERT-NPC13, NV-PUERT-NPC13_b, ...): the
    server's id-lookup\\ids.csv lists them all. The pick, in order: one in the quest's own area (NV-PUERT for NV-PUERT-MAIN-019), one in its world (NV), an object some quest already
    uses as a persona, then the plain object over a variant.
    """

    def __init__(self, server, raw_personas):
        self.by_name = collections.defaultdict(list)
        self.template = {}
        path = os.path.join(server, "id-lookup", "ids.csv")
        if os.path.exists(path):
            import csv
            with open(path, encoding="utf-8", newline="") as fh:
                for row in csv.DictReader(fh):
                    if row.get("Kind") != "Npc" or not row.get("Name"):
                        continue
                    obj = os.path.splitext(os.path.basename(row.get("Path") or ""))[0]
                    if obj:
                        self.by_name[row["Name"].strip()].append(obj)
                        self.template.setdefault(obj, int(row["Id"]))
        self.used = collections.Counter(p[:-len("_Persona")] for p in raw_personas if p.endswith("_Persona"))
        for f in glob.glob(os.path.join(server, "spiraldb-*", "QuestTemplates", "*.json")):
            try:
                with open(f, encoding="utf-8") as fh:
                    found = re.findall(r'"m_personaName"\s*:\s*"([^"]+)_Persona"', fh.read())
            except OSError:
                continue
            self.used.update(found)
        self.loaded = bool(self.template)

    def resolve(self, display, quest_code):
        """(persona ID, template ID, other candidates) for a display name; ("", 0, []) when no NPC has that name."""
        objs = list(dict.fromkeys(self.by_name.get((display or "").strip(), [])))
        if not objs:
            return "", 0, []
        parts = (quest_code or "").split("-")
        area = "-".join(parts[:2]).upper() if len(parts) >= 2 else ""
        world = parts[0].upper() if parts else ""

        def score(o):
            up = o.upper()
            return (bool(area) and up.startswith(area), bool(world) and up.startswith(world), self.used.get(o, 0) > 0,
                    not re.search(r"[-_][A-Za-z]$", o), self.used.get(o, 0), -len(o))

        objs.sort(key=score, reverse=True)
        return objs[0] + "_Persona", self.template.get(objs[0], 0), objs[1:]


NPCS = None
QUEST_CODE = ""
ZONE_BY_ID = {}
GUESSED_PERSONAS = collections.Counter()


def persona_of(d):
    """A dialog line's persona ID: persona_id, persona when it is one (older records), or the NPC its display name
    belongs to (newer records)."""
    p = text(d.get("persona_id")) or text(d.get("persona"))
    if p.endswith("_Persona"):
        return p
    if p and NPCS is not None:
        persona, _, others = NPCS.resolve(p, QUEST_CODE)
        if others:
            GUESSED_PERSONAS[(p, persona)] += 1
        return persona
    return ""


def actor_of(d):
    """The speaker's template ID: the line's own, or the one its display name resolved to."""
    if d.get("actor_template_id"):
        return int(d["actor_template_id"])
    p = text(d.get("persona"))
    if p and not p.endswith("_Persona") and NPCS is not None:
        return NPCS.resolve(p, QUEST_CODE)[1]
    return 0


def dialog_entry(d):
    return {
        "$type": T.format("NPCDialogEntry"),
        "m_personaName": persona_of(d),
        "m_nameOverride": text(d.get("name_override")) or ("" if persona_of(d) else text(d.get("persona") or d.get("display_name"))),
        "m_nameSTKey": None,
        "m_guiDisplay": "",
        "m_maxTimeSeconds": 0,
        "m_invisible": False,
        "m_requirements": None,
        "m_dialog": text(d.get("dialog")),
        "m_picture": text(d.get("picture")),
        "m_soundFile": text(d.get("sound_file")),
        "m_action": "",
        "m_dialogEvent": "",
        "m_actorTemplateID": actor_of(d),
        "m_cameraName": "",
    }


def dialog_list(tagged):
    if not any(tagged.values()):
        return None
    return {"$type": T.format("ActorDialogList"),
            "m_dialogs": [{"m_dialogTag": tag, "m_dialogEntries": [dialog_entry(d) for d in entries]}
                          for tag, entries in tagged.items() if entries]}


def stage_tag(stage, goal_index):
    """(where, tag): where is 'quest', 'goal' or 'skip'. Unknown stages return None and are reported.
    'ambient' lines are NPC chatter heard nearby (the zone's own data plays them), not quest dialog."""
    s = (stage or "").lower()
    if "ambient" in s:
        return "skip", None
    if "info" in s or "prep" in s or "offer" in s:
        return "quest", "Prep"
    if "underway" in s or "progress" in s:
        return "quest", "Underway"
    if "complet" in s or "goal" in s or "finish" in s:
        return ("goal", "Completion") if goal_index is not None else ("quest", "Complete")
    return None


def place_unstaged(dialogs, goals, quest_dialogs, goal_dialogs):
    """
    Older records list a quest's lines in game order with no stage. The first run of lines by one speaker is the giver's:
    its first line is the reminder shown while the quest is underway, the rest the offer. Each later run goes to the
    next talk step whose NPC speaks it, in step order (or to the last one placed when none matches).
    """
    talk = [(i, persona_of(g)) for i, g in enumerate(goals) if g.get("goal_type_enum") == "GOAL_TYPE_PERSONA"]
    runs = []
    for d in dialogs:
        if runs and persona_of(runs[-1][0]) == persona_of(d):
            runs[-1].append(d)
        else:
            runs.append([d])
    offer = runs[0] if runs else []
    step = 0
    for run in runs[1:]:
        who = persona_of(run[0])
        match = next((s for s in range(step, len(talk)) if talk[s][1] == who), None)
        if match is not None:
            step = match
        if talk:
            goal_dialogs[talk[min(step, len(talk) - 1)][0]].extend(run)
            if match is not None:
                step = match + 1
    if offer:
        if len(offer) > 1:
            quest_dialogs["Underway"].append(offer[0])
            offer = offer[1:]
        quest_dialogs["Prep"].extend(offer)


def prev_by_code(name, names):
    """The quest one step before this one by its code (KR-KOND-C08-002 -> KR-KOND-C08-001), when it exists."""
    m = re.match(r"^(.*?)(\d+)([A-Z]?)$", name)
    if not m:
        return None
    prefix, num, letter = m.group(1), m.group(2), m.group(3)
    if letter:
        cand = prefix + num
        if cand in names:
            return cand
    n = int(num)
    if n <= 1:
        return None
    cand = prefix + str(n - 1).zfill(len(num))
    return cand if cand in names else None


def chain_previous(parsed):
    """quest_chain.previous_quests: [(code, guessed)] where guessed means Wizard404 inferred it from the code order."""
    chain = parsed.get("quest_chain") or {}
    rows = chain.get("previous_quests") or ([chain["previous"]] if isinstance(chain.get("previous"), dict) else [])
    return [(r["object_name"], bool(r.get("inferred"))) for r in rows if isinstance(r, dict) and r.get("object_name")]


def find_previous(parsed, wrapper, id_to_name):
    """Previous quests the API names directly (any 'previous' list), by name or template ID."""
    found = []

    def walk(node, key=""):
        if isinstance(node, dict):
            for k, v in node.items():
                walk(v, k)
        elif isinstance(node, list) and "previous" in key.lower() and not key.lower().startswith(("hidden", "excluded")):
            for x in node:
                if isinstance(x, dict):
                    x = x.get("quest_name") or x.get("template_id") or x.get("id")
                if x is None:
                    continue
                name = id_to_name.get(str(x), x if isinstance(x, str) and "-" in x else None)
                if name:
                    found.append(name)

    walk(parsed)
    walk({k: v for k, v in wrapper.items() if k != "parsed_data"})
    return list(dict.fromkeys(found))


def goal_template(g, index, by_goal_dialogs, problems):
    gtype = g.get("goal_type_enum") or ""
    cls = GOAL_CLASS.get(gtype)
    if not cls:
        problems.append(f"step {index + 1}: goal type {gtype or g.get('goal_type')} has no mapping; built as a waypoint")
        cls = "WaypointGoalTemplate"
    title_key = text(g.get("goal_title_key")) or text(g.get("goal_title")) or "Goal"
    name = f"{index + 1}_{re.sub(r'[^A-Za-z0-9]', '', title_key)}"
    zone = text(g.get("destination_zone"))
    if not zone:
        # Dungeon steps ("Go To Next Room", "Follow Lucy") name their zone only by its location key, ZoneLocName_<zone
        # template ID>; other steps that go there give that ID's zone path.
        m = re.match(r"ZoneLocName_(\d+)$", text(g.get("location_key")))
        zone = ZONE_BY_ID.get(int(m.group(1)), "") if m else ""
    total =int(g.get("goal_total") or g.get("bounty_total") or 0)
    collect = gtype in ("GOAL_TYPE_BOUNTYCOLLECT", "GOAL_TYPE_COLLECTAFTERBOUNTY")
    t = {
        "$type": T.format(cls),
        "m_goalNameID": int(g.get("goal_name_id") or 0),
        "m_goalUnderway": None, "m_hyperlink": None, "m_completeText": None,
        "m_completeResults": {"m_results": []},
        "m_goalRequirements": None,
        "m_tallyCounter": {
            "m_percentChance": 100 if collect else 1.0,
            "m_descriptor": text(g.get("tally_text")),
            # The second tally is a name key the quest book fills in (TALLYTEXT2, WizardMobs_00004459 for a creature).
            "m_descriptor2": text((g.get("madlibs") or {}).get("TALLYTEXT2")) or text(g.get("tally_text_extra"))
                             or (text(g.get("persona")) if cls == "BountyGoalTemplate" else ""),
            "m_count": total,
            "m_tallyResults": {"m_results": []},
        },
        "m_displayImage1": text(g.get("display_image1")),
        "m_displayImage2": text(g.get("display_image2")),
        "m_clientTags": [],
        "m_genericEvents": [],
        "m_autoQualify": gtype == "GOAL_TYPE_USAGE",
        "m_autoComplete": False,
        "m_dialogList": dialog_list({"Completion": by_goal_dialogs.get(index, [])}),
        "m_noQuestHelper": False,
        "m_destinationZone": zone,
        "m_locationName": text(g.get("location_name")).split("|")[-1],
        "m_goalName": name,
        "m_goalTitle": title_key,
        "m_goalType": gtype or "GOAL_TYPE_WAYPOINT",
    }
    adjectives = listish(next((g[k] for k in ADJECTIVE_KEYS if g.get(k)), []))
    tags = listish(next((g[k] for k in TAG_KEYS if g.get(k)), []))
    if cls == "WaypointGoalTemplate":
        t.update({"m_zoneEntry": True, "m_zoneTag": zone, "m_proximityTag": "", "m_zoneExit": False})
        if not zone:
            problems.append(f"step {index + 1} ({title_key}): go-to step without a zone")
    elif cls == "BountyGoalTemplate":
        t.update({"m_npcAdjectives": adjectives, "m_bountyTotal": int(g.get("bounty_total") or total or 1),
                  "m_bountyType": "BT_MOB_KILL"})
        if not adjectives:
            problems.append(f"step {index + 1} ({title_key}): no creature adjectives; creatures named "
                            f"'{t['m_tallyCounter']['m_descriptor2'] or t['m_tallyCounter']['m_descriptor']}' count (by name) in {zone}")
    elif cls == "ScavengeGoalTemplate":
        t["m_clientTags"] = tags
        t.update({"m_itemAdjectives": adjectives or [x + ".AdjRef" for x in tags], "m_itemTotal": total or 1})
        if not tags and not adjectives:
            problems.append(f"step {index + 1} ({title_key}): no object tag; works only if the game's object for "
                            f"'{t['m_tallyCounter']['m_descriptor']}' in {zone} names this quest step itself")
    elif cls == "PersonaGoalTemplate":
        persona = text(g.get("persona_id")) or (text(g.get("persona")) if text(g.get("persona")).endswith("_Persona") else "")
        if not persona and NPCS is not None:
            display = text(g.get("persona_display_name")) or text(g.get("persona"))
            persona, _, others = NPCS.resolve(display, QUEST_CODE) if display else ("", 0, [])
            if persona and others:
                problems.append(f"step {index + 1} ({title_key}): '{display}' is {persona} (picked from "
                                f"{len(others) + 1} NPCs with that name; others: {', '.join(others[:4])})")
        if not persona:
            speakers = [d.get("persona_id") for d in by_goal_dialogs.get(index, []) if d.get("persona_id")]
            persona = speakers[0] if speakers else ""
        t.update({"m_personaName": persona, "m_usePatron": False})
        if not persona:
            problems.append(f"step {index + 1} ({title_key}): talk step without a persona for '{g.get('persona')}'")
    extra = sorted(k for k in g if k not in KNOWN_GOAL_KEYS)
    if extra:
        UNSEEN_KEYS.update(extra)
    return t


KNOWN_GOAL_KEYS = {
    "bounty_count", "bounty_total", "destination_zone", "destination_zone_display_name", "destination_zone_template_id",
    "display_image1", "display_image2", "goal_count", "goal_id", "goal_name_id", "goal_title", "goal_title_key",
    "goal_total", "goal_type", "goal_type_enum", "location_key", "location_name", "madlibs", "order", "persona",
    "persona_display_name", "persona_first_name_key", "persona_title_key", "raw_dialogue_goal_index", "source",
    "status", "tally_text", "tally_text_extra", "tally_text_extra_key", "tally_text_key", "use_tally", "world_name",
    *ADJECTIVE_KEYS, *TAG_KEYS, "persona_id",
}
UNSEEN_KEYS = collections.Counter()
STAGES = collections.Counter()
REWARD_TYPES = collections.Counter()


def drop_table(name, title, world, parsed):
    gold = xp = 0
    items = []
    for r in parsed.get("end_results") or []:
        if not isinstance(r, dict):
            continue
        kind = (r.get("result_type") or "").replace("class ", "")
        REWARD_TYPES[kind] += 1
        if kind == "ResAddGold":
            gold += int(r.get("gold") or 0)
        elif kind == "ResAddMagicXP":
            xp += int(r.get("experience") or 0)
        elif kind == "ResLootTable" or r.get("template_id") or r.get("item_template_id"):
            tid = r.get("template_id") or r.get("item_template_id")
            if tid:
                items.append({"ItemId": str(tid), "ItemName": text(r.get("display_name")), "Notes": "", "Requirements": None})
    for r in parsed.get("rewards") or []:
        if not isinstance(r, dict):
            continue
        kind = (r.get("reward_type") or "").lower()
        REWARD_TYPES[kind] += 1
        amount = int(r.get("amount") or 0)
        if kind == "gold":
            gold += amount
        elif kind in ("xp", "experience", "exp"):
            xp += amount
        elif r.get("template_id") and kind not in ("spell",):
            items.append({"ItemId": str(r["template_id"]), "ItemName": text(r.get("label")), "Notes": "", "Requirements": None})
    return {"Name": name, "Description": f"{world} quest {title} (from Wizard404)", "RollChance": 1.0, "Weight": 100,
            "NoneChance": 0.0, "PityCounter": 0.0, "MinGold": gold, "MaxGold": gold, "ExperienceAmount": xp,
            "TrainingPoints": 0, "Items": items}


def main():
    global RAW, OUT
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", default=DEFAULT_SERVER)
    ap.add_argument("--overwrite", action="store_true")
    ap.add_argument("--raw", default=RAW)
    ap.add_argument("--out", default=OUT)
    a = ap.parse_args()
    RAW, OUT = a.raw, a.out

    have = set()
    for f in glob.glob(os.path.join(a.server, "spiraldb-*", "QuestTemplates", "*.json")):
        m = re.match(r"questtemplates_(.+)\.json$", os.path.basename(f))
        if m:
            have.add(m.group(1))

    raw = {}
    for f in glob.glob(os.path.join(RAW, "*.json")):
        with open(f, encoding="utf-8") as fh:
            raw[os.path.splitext(os.path.basename(f))[0]] = json.load(fh)
    parsed_all = {tid: quest_of(p) for tid, p in raw.items()}
    # Community records carry no quest code, only its hash (quest_name_id). codes.json gives the codes found for those
    # hashes (from the game's own files and the code patterns of known quests): {"<template id>": "<code>"}.
    recovered = {}
    codes_file = os.path.join(HERE, "codes.json")
    if os.path.exists(codes_file):
        with open(codes_file, encoding="utf-8") as fh:
            recovered = json.load(fh)
    for tid, (p, _) in parsed_all.items():
        if not p.get("quest_name") and recovered.get(tid):
            p["quest_name"] = recovered[tid]
            p["_code_recovered"] = True
    id_to_name = {tid: p.get("quest_name") for tid, (p, _) in parsed_all.items() if p.get("quest_name")}
    names = have | set(id_to_name.values())
    global NPCS, QUEST_CODE
    seen = [text(x.get("persona_id")) or text(x.get("persona")) for p, _ in parsed_all.values()
            for x in (p.get("goals") or []) + (p.get("dialog_list") or []) if isinstance(x, dict)]
    NPCS = Npcs(a.server, seen)
    for p, _ in parsed_all.values():
        for g in p.get("goals") or []:
            if isinstance(g, dict) and g.get("destination_zone_template_id") and text(g.get("destination_zone")):
                ZONE_BY_ID.setdefault(int(g["destination_zone_template_id"]), g["destination_zone"])
    if not NPCS.loaded:
        print(f"No {os.path.join(a.server, 'id-lookup', 'ids.csv')}: NPCs named only by display name stay unresolved.")

    os.makedirs(os.path.join(OUT, "QuestTemplates"), exist_ok=True)
    os.makedirs(os.path.join(OUT, "DropTables"), exist_ok=True)
    report, made, skipped = [], 0, 0
    for tid, (p, wrapper) in sorted(parsed_all.items(), key=lambda x: x[1][0].get("quest_name") or ""):
        name = p.get("quest_name")
        if not name:
            report.append(f"{tid}: community record '{text(p.get('display_name'))}' with no known quest code, skipped")
            continue
        if name in have and not a.overwrite:
            skipped += 1
            continue
        problems = []
        QUEST_CODE = name
        if p.get("_code_recovered"):
            problems.append("community record: code worked out from its quest ID; it has no dialog lines")
        title = text(p.get("display_name")) or text(p.get("quest_title")) or name
        worlds = p.get("associated_worlds") or []
        world = worlds[0] if worlds else next(
            (text(g.get("destination_zone")).split("/")[0] for g in p.get("goals") or [] if text(g.get("destination_zone"))), "")

        quest_dialogs = collections.defaultdict(list)
        goal_dialogs = collections.defaultdict(list)
        goals = sorted(p.get("goals") or [], key=lambda g: (g.get("order") or 0))
        dialogs = p.get("dialog_list") or []
        if dialogs and all(d.get("stage") is None and d.get("goal_index") is None for d in dialogs):
            place_unstaged(dialogs, goals, quest_dialogs, goal_dialogs)
            dialogs = []
        talk_steps = [(i, persona_of({"persona": text(g.get("persona_display_name")) or text(g.get("persona"))}))
                      for i, g in enumerate(goals) if g.get("goal_type_enum") == "GOAL_TYPE_PERSONA"]
        for d in dialogs:
            STAGES[d.get("stage")] += 1
            where = stage_tag(d.get("stage"), d.get("goal_index"))
            if where is None:
                problems.append(f"dialog stage '{d.get('stage')}' not placed: {text(d.get('dialog'))[:60]}")
                continue
            if where[0] == "skip":
                continue
            if where[0] == "quest" and where[1] == "Complete":
                # A closing line with no step number: the last talk step whose NPC says it, else the quest's own end.
                speaker = persona_of(d)
                step = next((i for i, p in reversed(talk_steps) if speaker and p == speaker), None)
                if step is not None:
                    goal_dialogs[step].append(d)
                    continue
            if where[0] == "quest":
                quest_dialogs[where[1]].append(d)
            else:
                goal_dialogs[int(d.get("goal_index"))].append(d)
        if not quest_dialogs.get("Prep"):
            problems.append("no offer (Prep) dialog: no NPC will offer it until a giver is set in the Quest Editor")

        built = [goal_template(g, i, goal_dialogs, problems) for i, g in enumerate(goals)]
        if not built:
            problems.append("no steps")
        # Steps with the same order run together; each group starts when the one before it is done.
        groups = collections.OrderedDict()
        for g, t in zip(goals, built):
            groups.setdefault(g.get("order") or 0, []).append(t["m_goalName"])
        group_list = list(groups.values())
        logic = []
        for i, group in enumerate(group_list):
            last = i == len(group_list) - 1
            logic.append({"m_goalsAND": group, "m_goalsOR": [], "m_goalsToAdd": [] if last else group_list[i + 1],
                          "m_completeQuest": last, "m_requiredORCount": 1})

        chain = chain_previous(p)
        previous = [q for q, _ in chain] or find_previous(p, wrapper, id_to_name)
        how = "Wizard404 quest chain"
        if chain and all(guess for _, guess in chain):
            how = "Wizard404's guess from the quest code"
        if not previous and prev_by_code(name, names):
            previous, how = [prev_by_code(name, names)], "quest code order"
        reqs = None
        if previous:
            # Several previous quests are alternatives (one per school, or two ways into the same chain): any one will do.
            op = "ROP_OR" if len(previous) > 1 else "ROP_AND"
            reqs = {"m_requirements": [{"$type": T.format("ReqHasEntry"), "m_entryName": "Complete", "m_displayName": None,
                                        "m_isQuestRegistry": True, "m_questName": q, "m_applyNOT": False,
                                        "m_operator": op} for q in previous],
                    "m_applyNOT": False, "m_operator": "ROP_AND"}
            missing = [q for q in previous if q not in names]
            if missing:
                problems.append(f"needs quests nobody has: {', '.join(missing)}")
            if how != "Wizard404 quest chain":
                problems.append(f"previous quest guessed from the {how}: {previous[0]}")
        else:
            problems.append("no previous quest found: offered to everyone who meets the giver")

        # No NPC offers it: like the other quests rebuilt from Wizard404, it starts by itself once the quest before it is
        # done. Without a previous quest it would start for everyone at once, so that one waits for a giver instead.
        tags = []
        if not quest_dialogs.get("Prep"):
            problems[:] = [x for x in problems if not x.startswith("no offer (Prep) dialog")]
            if reqs:
                tags.append("ImlightAutoStart")
                problems.append("no NPC offers it: starts by itself once the quest before it is done")
            else:
                problems.append("no NPC offers it and no previous quest: it never starts until a giver is set in the "
                                "Quest Editor")

        quest = {
            "m_questName": name, "m_questNameID": 0, "m_questTitle": title,
            "m_questInfo": None, "m_questPrep": None, "m_questUnderway": None, "m_questComplete": None,
            "m_startGoals": group_list[0] if group_list else [],
            "m_goals": built,
            "m_startResults": {"m_results": []},
            "m_endResults": {"m_results": [{"$type": T.format("ResDropTable"), "m_tableName": name}]},
            "m_requirements": reqs,
            "m_prepRequirements": None, "m_pruneRequirements": None, "m_prepAlways": False,
            "m_clientTags": tags,
            "m_goalLogic": logic,
            "m_questLevel": int(p.get("quest_level") or p.get("level") or 1),
            "m_questRepeat": 0, "m_onStartQuestScript": "", "m_onEndQuestScript": "",
            "m_dialogList": dialog_list(quest_dialogs),
            "m_missionDoors": [], "m_dynaMods": [],
            "m_isHidden": bool(p.get("is_hidden")), "m_outdated": False, "m_noQuestHelper": False,
            "m_mainline": bool(p.get("mainline")), "m_activityType": "ACTIVITY_NotActivity",
        }
        with open(os.path.join(OUT, "QuestTemplates", f"questtemplates_{name}.json"), "w", encoding="utf-8") as fh:
            json.dump(quest, fh, ensure_ascii=False, indent=2)
        with open(os.path.join(OUT, "DropTables", f"droptables_{name.lower()}.json"), "w", encoding="utf-8") as fh:
            json.dump(drop_table(name, title, world, p), fh, ensure_ascii=False, indent=2)
        made += 1
        if problems:
            report.append(f"{name} ({title}, {world}):")
            report += [f"    {x}" for x in problems]

    summary = [f"{made} quests written, {skipped} skipped (already on the server), from {len(raw)} downloaded.",
               f"dialog stages seen: {dict(STAGES)}",
               f"reward types seen: {dict(REWARD_TYPES)}",
               f"goal fields not used yet: {dict(UNSEEN_KEYS)}", ""]
    with open(os.path.join(OUT, "report.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(summary + report) + "\n")
    print("\n".join(summary[:4]))
    print(f"Details: {os.path.join(OUT, 'report.txt')}")


if __name__ == "__main__":
    main()
