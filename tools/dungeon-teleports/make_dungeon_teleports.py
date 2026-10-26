"""Works out where dungeon sigils and dungeon doors go when SpiralDB has no capture of them.

The game's zone files never say where a sigil or an exit leads (the live server knows), but every way in or out is
named like the spot it lands on: the street's "Target location (Street6 Tower1 Exit)" stands next to the sigil, and the
tower has "Target location (Street6 Tower1 Entrance)"; a floor's "Trigger (Level15 Level16 Entrance)" lands on
"Target location (Level15 Level16 Entrance)". This pairs them up.

Input: a folder of decoded zone wads (one folder per wad with gamedata.bin.json and triggers.xml.json, as the
Manager's Game Files reader writes them) and a SpiralDB ZoneTransfer folder. Output: spiraldb-extra/DungeonTeleports
JSON (a list of {ZoneName, Teleports}) and a plain report of what could not be paired.

    python3 make_dungeon_teleports.py <decoded wads> <spiraldb ZoneTransfer> <out.json> <report.txt>
"""
import difflib
import glob
import json
import math
import os
import re
import sys

PAD_RADIUS = 1500.0


def load_spiraldb(folder):
    known = {}
    for file in glob.glob(os.path.join(folder, '*.json')):
        zone = json.load(open(file, encoding='utf-8'))
        known.setdefault(zone['ZoneName'].lower(), {}).update(
            {t['TriggerName']: t['Teleport'] for t in zone.get('Teleports') or []})
    return known


def walk(node, cls, out):
    if isinstance(node, dict):
        if node.get('$class') == cls:
            out.append(node)
        for value in node.values():
            walk(value, cls, out)
    elif isinstance(node, list):
        for value in node:
            walk(value, cls, out)
    return out


def norm(text):
    text = re.sub(r'[^a-z0-9]', '', (text or '').lower())
    return re.sub(r'(?<![0-9])0+(?=[0-9])', '', text)


PREFIX = re.compile(r'^\s*(teleport\s*location|target\s*location|teleport\s*trigger|teleport|target|trigger)\s*', re.I)


def key(name):
    """The part of a way's name both of its ends share: "Teleport location (Level16 Level15 Exit)" -> "level16level15exit"."""
    return norm(PREFIX.sub('', name or ''))


STOP_WORDS = {'to', 'teleport', 'teleporter', 'tele', 'sigil', 'interior', 'interiors', 'instance', 'the', 'into', 'enter',
              'go', 'from', 'portal', 'door', 'sigl', 'combat', 'room', 'cave', 'lair', 'tower'}


def words_of(text):
    text = re.sub(r'([a-z])([A-Z])', r'\1 \2', text or '')
    return [w for w in re.split(r'[^a-z]+', text.lower()) if w]


def swap(name):
    return re.sub(r'exit', 'Entrance', name, flags=re.I) if re.search(r'exit', name, re.I) else None


class Zone:
    def __init__(self, folder):
        self.data = json.load(open(os.path.join(folder, 'gamedata.bin.json'), encoding='utf-8'))
        self.name = self.data['m_zoneName']
        self.world = self.name.split('/')[0]
        self.locations = [(l['m_locName'], l['m_location']) for l in self.data.get('m_locationList') or []]
        self.sigils = walk(self.data, 'MinigameSigilInfo', [])
        trig = os.path.join(folder, 'triggers.xml.json')
        self.triggers = json.load(open(trig, encoding='utf-8')).get('m_triggers') or [] if os.path.exists(trig) else []

    def entrance(self):
        entrances = [n for n, _ in self.locations if 'entrance' in n.lower()]
        return entrances[0] if len(entrances) == 1 else 'Start'

    def blank_teleports(self):
        for trigger in self.triggers:
            for result in (trigger.get('m_results') or {}).get('m_results') or []:
                if result.get('$class') == 'ResTeleport' and not result.get('m_destinationZone'):
                    yield trigger['m_triggerName']
                    break


def teleport(zone, loc):
    return {'m_destinationLoc': loc, 'm_destinationZone': zone, 'm_exitTeleporter': 0, 'm_teleporterTag': 0,
            'm_teleportType': 'TELEPORT_STATIC', 'm_transitionID': 0}


def main(decoded, spiraldb, out_path, report_path):
    known = load_spiraldb(spiraldb)
    zones = {}
    for folder in sorted(glob.glob(os.path.join(decoded, '*'))):
        if os.path.exists(os.path.join(folder, 'gamedata.bin.json')):
            zone = Zone(folder)
            zones[zone.name.lower()] = zone
    by_loc = {}
    for zone in zones.values():
        for name, _ in zone.locations:
            by_loc.setdefault((zone.world, key(name)), set()).add(zone.name)
    exit_triggers = {}
    for zone in zones.values():
        for name in zone.blank_teleports():
            exit_triggers.setdefault((zone.world, key(name)), set()).add(zone.name)

    added = {}
    report = []
    entered_from = {}  # instance zone -> (zone, location) its sigil stands at

    def add(zone, trigger, dest_zone, dest_loc, why):
        added.setdefault(zone, {})[trigger] = teleport(dest_zone, dest_loc)
        report.append(f'ADDED  {zone} | {trigger} -> {dest_zone} @ {dest_loc}   ({why})')

    def known_dest(zone, trigger):
        return known.get(zone.lower(), {}).get(trigger)

    def server_dest(zone, tag):
        # What the server already finds for a pad (InteractDungeonSigilComponent.MatchEntranceTeleport).
        flat = lambda t: re.sub(r'[^a-z0-9]', '', (t or '').lower())
        teleports = known.get(zone.lower(), {})
        for name, tp in teleports.items():
            if flat(name) and flat(name) == flat(tag):
                return tp
        m = re.search(r'street\s*(\d+)\s+(.+?)\s+instance\s+sigil', tag, re.I)
        if m:
            for name, tp in teleports.items():
                if 'street' + m.group(1) in flat(name) and flat(m.group(2)) in flat(name):
                    return tp
        return None

    def unique_zone(zone, test):
        hits = [z for z in zones.values() if z.world == zone.world and z.name.lower() != zone.name.lower() and test(z)]
        return hits[0] if len(hits) == 1 else None

    def by_zone_name(zone, tag, at):
        # Later worlds name a pad and the spot next to it after the dungeon's zone: "ToZ02i01-Crab Castle" stands at
        # "From Z02i1 - CoralCastle" and leads to Interiors/CL_Z02i1_Crab_Realm; "TeleportTo_CelestianResearchLab"
        # to a zone named CelestianResearchLab.
        spots = [n for n, p in zone.locations
                 if re.search('from', n, re.I) and math.dist((at['X'], at['Y']), (p['X'], p['Y'])) <= PAD_RADIUS]
        for text in [tag] + spots:
            for code in re.findall(r'[a-z]+[0-9]+[a-z]+[0-9]+', norm(text)):
                target = unique_zone(zone, lambda z: re.search(re.escape(code) + r'(?![0-9])', norm(z.name.split('/')[-1])))
                if target:
                    return target.name, target.entrance(), f'the zone named {code} in "{text}"'
        rest = norm(re.sub(r'^(teleport|sigil|to|_|\s|-)+', '', tag, flags=re.I))
        rest = re.sub(r'sigil$', '', rest)
        if len(rest) >= 6:
            target = unique_zone(zone, lambda z: rest in norm(z.name.split('/')[-1]))
            if target:
                return target.name, target.entrance(), f'the zone named like "{tag}"'
        # The words of the pad's name, each found in one zone's name: "To AV_02_TowerHarlequin" -> AV_Z02_TowerHarlequin.
        words = [w for w in words_of(tag) if w not in STOP_WORDS and len(w) >= 3]
        if any(len(w) >= 5 for w in words):
            world = zone.name.split('/')[0]
            # A number at the end ("JungleClearing01") must be in the zone's name too, and words alone only lead indoors.
            number = re.search(r'([0-9]+)\W*$', norm(tag))
            target = unique_zone(zone, lambda z: z.name.split('/')[0] == world and '/Interiors/' in z.name
                                 and all(w in norm(z.name.split('/')[-1]) for w in words)
                                 and (not number or re.search(r'(?<![0-9])' + number.group(1) + '$', norm(z.name))))
            if target:
                return target.name, target.entrance(), f'the zone whose name has the words of "{tag}"'
        return None

    # Sigils.
    pads = {}  # zone -> [(location, destination teleport)]
    unpaired = []
    for zone in zones.values():
        for sigil in zone.sigils:
            tag = sigil.get('m_zoneTag2') or sigil.get('m_zoneTag') or ''
            at = sigil.get('m_location') or {'X': 0, 'Y': 0}
            near = sorted((math.dist((at['X'], at['Y']), (p['X'], p['Y'])), n) for n, p in zone.locations
                          if re.search('exit', n, re.I))
            near = [n for d, n in near if d <= PAD_RADIUS]
            captured = server_dest(zone.name, tag)
            if captured and captured.get('m_destinationZone'):
                pads.setdefault(zone.name, []).append((at, captured))
                if near:
                    entered_from.setdefault(captured['m_destinationZone'].lower(), (zone.name, near[0]))
                continue
            dest = None
            for exit_name in near[:1]:
                pair = swap(exit_name)
                hits = {z for z in by_loc.get((zone.world, key(pair)), set()) if z.lower() != zone.name.lower()}
                if len(hits) == 1:
                    dest = (hits.pop(), pair, f'pad stands at "{exit_name}"')
                    break
                hits = {z for z in exit_triggers.get((zone.world, key(exit_name)), set()) if z.lower() != zone.name.lower()}
                if len(hits) == 1:
                    target = hits.pop()
                    dest = (target, zones[target.lower()].entrance(), f'pad stands at "{exit_name}", the exit of {target}')
                    break
            if not dest:
                dest = by_zone_name(zone, tag, at)
            if dest:
                add(zone.name, tag, *dest)
                pads.setdefault(zone.name, []).append((at, teleport(dest[0], dest[1])))
                back = near or [n for d, n in sorted((math.dist((at['X'], at['Y']), (p['X'], p['Y'])), n)
                                                    for n, p in zone.locations if re.search('from', n, re.I)) if d <= PAD_RADIUS]
                if back:
                    entered_from.setdefault(dest[0].lower(), (zone.name, back[0]))
            else:
                unpaired.append((zone, tag, at, near))

    # A quest's own copy of a zone ("MB_Museum_Instance") has the pads of the zone it copies, at the same spots.
    def twin_names(name):
        base = re.sub(r'_Instanced?$', '', name)
        return {base, base + '_Instance', base + '_Instanced'} - {name}

    for zone, tag, at, near in unpaired:
        twin = next(((other, tp) for other in twin_names(zone.name) for p, tp in pads.get(
            zones[other.lower()].name if other.lower() in zones else other, [])
            if math.dist((at['X'], at['Y']), (p['X'], p['Y'])) <= 250), None)
        if twin:
            add(zone.name, tag, twin[1]['m_destinationZone'], twin[1]['m_destinationLoc'], f'the same pad of {twin[0]}')
        else:
            report.append(f'NO SIGIL  {zone.name} | {tag} (nearest exit spot: {near[0] if near else "none"})')

    # Doors and exits with no destination.
    for zone in zones.values():
        for name in zone.blank_teleports():
            if known_dest(zone.name, name):
                continue
            k = key(name)
            own = [n for n, _ in zone.locations if key(n) == k]
            if own:
                add(zone.name, name, zone.name, own[0], 'a spot of this zone has its name')
                continue
            # A spot named with a typo ("Level16 Leve17 Entrance"): one letter off, and the only one that close.
            close = sorted(((difflib.SequenceMatcher(None, key(n), k).ratio(), n) for n, _ in zone.locations), reverse=True)
            if close and close[0][0] >= 0.94 and (len(close) == 1 or close[1][0] < close[0][0]) \
                    and abs(len(key(close[0][1])) - len(k)) <= 1 and re.search('exit|entrance', name, re.I):
                add(zone.name, name, zone.name, close[0][1], 'a spot of this zone has its name, but for a typo')
                continue
            # Another zone only by a name that says which way it goes ("Teleport location 0" names nothing).
            hits = {z for z in by_loc.get((zone.world, k), set()) if z.lower() != zone.name.lower()}
            if len(hits) == 1 and re.search('exit|entrance', name, re.I):
                target = hits.pop()
                spot = next(n for n, _ in zones[target.lower()].locations if key(n) == k)
                add(zone.name, name, target, spot, 'a spot of that zone has its name')
                continue
            back = entered_from.get(zone.name.lower())
            if back and re.search('exit', name, re.I):
                add(zone.name, name, back[0], back[1], 'back to the sigil this dungeon is entered from')
                continue
            report.append(f'NO DOOR   {zone.name} | {name}')

    out = [{'ZoneName': zones[z.lower()].name, 'Teleports': [{'TriggerName': t, 'Teleport': tp} for t, tp in sorted(ts.items())]}
           for z, ts in sorted(added.items())]
    json.dump(out, open(out_path, 'w', encoding='utf-8'), indent=1)
    open(report_path, 'w', encoding='utf-8').write('\n'.join(sorted(report)) + '\n')
    print(f'{sum(len(t) for t in added.values())} teleports added in {len(added)} zones; report in {report_path}')


if __name__ == '__main__':
    main(*sys.argv[1:5])
