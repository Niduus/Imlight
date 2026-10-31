#!/usr/bin/env python3
"""
Boss cheat twins (Niduus, 2026-10-31: "expand more on cheats based on established factors").

The game gives each creature a combat trigger file (its cheats live on the real servers, but the file name is known).
Creatures that share a trigger file cheat the same way: hoard-pack and Battle Rift copies at other levels, the same
boss in a second zone, and Polaris's shared cheat files (PL-IceOff-01 and others). This writes a BossCheats file for
every creature that has none of its own but shares a trigger file with one that has, copying that boss's cheats.

Usage: build_twins.py <wizard404 raw dir> <BossCheats dir>
Rerun after a source boss changes: files this tool wrote (first line marker) are rewritten, hand-made ones never.
"""
import glob, gzip, json, os, re, sys
from collections import defaultdict

MARK = "// Twin: same game cheat file as"

# Creatures left out on purpose: another creature of the same fight (its file is the boss's, not theirs), test or
# placeholder creatures, and Monstrology summons (players' creatures, which never cheat).
SKIP = {
    1663255, 1663256, 1663257, 1663268, 1663269, 1663270,  # Tse-Tse's brothers: Tse-Tse's file runs the fight
    1707637, 1698889,  # Library Guard: Darkmoor's shared vampire file holds the lords' own cheats
    1336711, 1336727,  # Apollo, Cronus: Zeus's duel file
    1562536,           # Moosetaur Bandit (a test copy)
    211508, 184190,    # Default MOB, Thief King: General Stormclaw's file
    1466267,           # Feckless Soul: the Mirage cold ghosts' file
    1569512,           # Aeon of Atavus: part of Atavus Monquistan's fight
    1669491,           # Shadow Weaver's own minion: the boss's file has it cast for him
}
CREATURE_KEYS = ("Creature", "Spawn", "Brings", "BringsRandom")
NUM = re.compile(r"^(.*?)[_-]?L?(\d+)$")
# When creatures sharing a file were built differently, the fullest one is copied.
PREFER = {
    "PL-IceOff-01": 1340520,          # Tundarian Enforcer: both Iceblades
    "BR_ZF_TseTse_A_100": 1663253,    # Tse-Tse Snaketail (normal)
}
ADV = re.compile(r"^Adv[_-]|[_-]Adv$", re.I)


def strip_comments(text):
    return re.sub(r"^\s*//.*$", "", text, flags=re.M)


def main(raw, out):
    recs, api = {}, {}
    for fn in glob.glob(os.path.join(raw, "data-mob*.jsonl.gz")):
        for line in gzip.open(fn, "rt"):
            r = json.loads(line)
            recs[r["id"]] = r.get("mob") or {}
    for fn in glob.glob(os.path.join(raw, "api-mob*.jsonl.gz")):
        for line in gzip.open(fn, "rt"):
            r = json.loads(line)
            api[r["id"]] = ((r.get("data") or {}).get("parsed_data")) or {}

    def trig(i):
        t = (recs.get(i) or {}).get("trigger_file") or (api.get(i) or {}).get("triggers")
        return t if t and t != "null" else None

    def obj(i):
        return (api.get(i) or {}).get("object_name") or ""

    def name(i):
        return (recs.get(i) or {}).get("name") or (api.get(i) or {}).get("display_name") or str(i)

    files, made = {}, set()
    for fn in glob.glob(os.path.join(out, "*.json")):
        text = open(fn, encoding="utf-8").read()
        tid = int(os.path.basename(fn).split(".")[0])
        if text.startswith("{\n  " + MARK):
            made.add(tid)
        else:
            files[tid] = json.loads(strip_comments(text))

    by_obj = {obj(i): i for i in api if obj(i)}
    by_trig = defaultdict(list)
    for i in set(recs) | set(api):
        t = trig(i)
        if t:
            by_trig[t].append(i)

    by_family = defaultdict(list)
    for i in api:
        m = NUM.match(obj(i))
        if m:
            by_family[m.group(1)].append((int(m.group(2)), i))

    def remap(ref, twin, src, notes):
        # The creature a source cheat names (a minion, an ally) at the twin's own level or place.
        o_ref, o_twin, o_src = obj(ref), obj(twin), obj(src)
        # The part of the name that differs between source and twin (A -> B, Wooden -> Stone, 130 -> 25), swapped
        # in the named creature's own file name.
        p = 0
        while p < min(len(o_src), len(o_twin)) and o_src[p] == o_twin[p]:
            p += 1
        q = 0
        while q < min(len(o_src), len(o_twin)) - p and o_src[-1 - q] == o_twin[-1 - q]:
            q += 1
        while p > 0 and o_src[p - 1] not in "_-":
            p -= 1
        while q > 0 and o_src[len(o_src) - q] not in "_-":
            q -= 1
        a, b = o_src[p:len(o_src) - q], o_twin[p:len(o_twin) - q]
        if a and o_ref.count(a) == 1:
            hit = by_obj.get(o_ref.replace(a, b))
            if hit:
                return hit
        for a, b in (("STRD_", ""), ("", "STRD_")):
            if o_src.startswith(a) and o_twin == b + o_src[len(a):] and o_ref.startswith(a):
                hit = by_obj.get(b + o_ref[len(a):])
                if hit:
                    return hit
        mt, ms, mr = NUM.match(o_twin), NUM.match(o_src), NUM.match(o_ref)
        if mt and ms and mr and mt.group(1) == ms.group(1):
            own, ref_fam = sorted(by_family[mt.group(1)]), sorted(by_family[mr.group(1)])
            if len(own) == len(ref_fam):
                return ref_fam[[i for _, i in own].index(twin)][1]
        zone = ((recs.get(twin) or {}).get("zone") or {}).get("id")
        here = [i for i in recs if name(i) == name(ref) and ((recs[i].get("zone") or {}).get("id")) == zone]
        if zone and len(here) == 1:
            return here[0]
        notes.append(ref)
        return ref

    def walk(node, twin, src, notes):
        if isinstance(node, list):
            return [walk(n, twin, src, notes) for n in node]
        if not isinstance(node, dict):
            return node
        out = {}
        for k, v in node.items():
            if k in CREATURE_KEYS and isinstance(v, int) and v > 0:
                v = remap(v, twin, src, notes)
            elif k in CREATURE_KEYS and isinstance(v, list):
                v = [str(remap(int(x), twin, src, notes)) if str(x).isdigit() else x for x in v]
            else:
                v = walk(v, twin, src, notes)
            out[k] = v
        return out

    written = []
    for t, ids in sorted(by_trig.items()):
        built = [i for i in ids if i in files]
        if not built:
            continue
        for i in sorted(ids):
            if i in files or i in SKIP:
                continue
            o = obj(i)
            if o.endswith("_MM") or "MonstFix" in o:  # Monstrology summons
                continue
            if ADV.search(o) and by_obj.get(ADV.sub("", o)) in files:  # the server already falls back for these
                continue
            same = [b for b in built if name(b).replace(" (Advanced)", "") == name(i).replace(" (Advanced)", "")]
            if t in PREFER:
                src = PREFER[t]
            elif len({json.dumps(files[b]["Cheats"], sort_keys=True) for b in built}) == 1:
                src = built[0]
            elif len(same) == 1:
                src = same[0]
            else:
                print(f"skipped {i} {name(i)}: {t} is built differently on {built}", file=sys.stderr)
                continue
            boss = files[src]
            rank = (recs.get(i) or {}).get("rank")
            twin = {
                "TemplateID": i,
                "Name": f"{name(i)} (Rank {rank})" if rank else name(i),
            }
            unmapped = []
            for k, v in boss.items():
                if k == "Cheats":
                    # A "comes in with its minions" whose minions have no copy at the twin's level is left out: the
                    # twin's own battle places its helpers.
                    kept = []
                    for c in v:
                        miss = []
                        c2 = walk(c, i, src, miss)
                        if miss and (c.get("Brings") or c.get("BringsRandom")):
                            continue
                        unmapped += miss
                        kept.append(c2)
                    twin[k] = kept
                elif k not in ("TemplateID", "Name"):
                    twin[k] = walk(v, i, src, unmapped)
            if not twin.get("Cheats"):
                continue
            if unmapped:
                print(f"{i} {name(i)}: kept the source's creatures {sorted(set(unmapped))}", file=sys.stderr)
            head = (f"{MARK} {boss.get('Name', src)} ({src}), trigger {t}.\n"
                    f"  // Made by tools/boss-twins/build_twins.py; change the source boss and rerun, not this file.\n")
            body = json.dumps(twin, indent=2, ensure_ascii=False)
            with open(os.path.join(out, f"{i}.json"), "w", encoding="utf-8", newline="\n") as f:
                f.write("{\n  " + head + body[2:] + "\n")
            written.append((i, name(i), src))

    for i in made - {w[0] for w in written}:
        os.remove(os.path.join(out, f"{i}.json"))
    for w in written:
        print(*w, sep="\t")
    print(f"{len(written)} twins", file=sys.stderr)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
