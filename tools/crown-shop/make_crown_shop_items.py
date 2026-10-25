"""
Writes spiraldb-extra/CrownShop/GameFileItems.json: every item the game files price in crowns that SpiralDB's Crown Shop
list (CrownShop/Tab*.json) leaves out, with the shop section it goes in.

- Crowns = the item's m_creditsCost and gold = its m_baseCost, which is what SpiralDB's list uses for the items it has.
- Retired, developer-only and unreleased items are left out.
- Gear and bundles that come in level versions (same name, slot and school) are one entry with all their versions, and
  the server shows each wizard the version for their level, like the real shop.
- The items come from Wizard404's dump of the same game revision as the client (api-*.jsonl.gz).

Run: python3 -I make_crown_shop_items.py <spiraldb CrownShop folder> <out json> <wizard404 raw folder>...
"""
import glob, gzip, json, re, sys
from collections import defaultdict

shop_dir, out_path, raw_dirs = sys.argv[1], sys.argv[2], sys.argv[3:]

listed = set()
for f in glob.glob(f'{shop_dir}/Tab*.json'):
    for item in json.load(open(f, encoding='utf-8')).get('items', []):
        listed.add(item['templateId'])

items = {}
for raw in raw_dirs:
    for f in sorted(glob.glob(f'{raw}/api-*.jsonl.gz')):
        for line in gzip.open(f, 'rt', encoding='utf-8'):
            record = json.loads(line)
            parsed = (record.get('data') or {}).get('parsed_data') or {}
            tid = parsed.get('template_id') or record.get('id')
            if tid and tid not in items:
                parsed['_kind'] = record['kind']
                items[tid] = parsed

GEAR = {'hat': 11, 'robe': 9, 'shoes': 10, 'weapon': 15, 'amulet': 13, 'ring': 12, 'athame': 14, 'deck': 19}
RENTAL = re.compile(r'\(\s*\d+\s*(Day|Days|Hour|Hours)\s*\)', re.I)

def category(p):
    kind, sub, adj = p['_kind'], (p.get('sub_type') or '').lower(), set(p.get('adjectives') or [])
    if kind == 'gear':
        if sub in GEAR: return GEAR[sub]
        if sub == 'mount': return 3 if RENTAL.search(p.get('display_name') or '') else 2
        if sub == 'pet': return 4
        if sub == 'elixir': return 16
        if sub == 'henchman': return 18
        return 19
    if kind == 'bundle':
        return 8 if 'SetGear' in adj else 33
    if kind == 'pack': return 20
    if kind == 'reagent': return 28
    if kind == 'snack': return 26
    if kind == 'cosmetic': return 31
    if kind == 'housing':
        if 'RaidKey' in adj: return 48
        if 'CastleBlock' in adj: return 30
        if 'Fishing' in adj: return 29
        if adj & {'Seed', 'GardeningItem', 'GardeningSoil', 'GardeningPot', 'GardeningMultiPot', 'CrownShop_Gardening_Tab'}: return 7
        if 'Minigame' in adj: return 24
        if adj & {'Deed', 'Islands'}: return 5
        return 6
    return None

def entry(tid, p):
    adj = p.get('adjectives') or []
    e = {'TemplateId': tid, 'CrownsCost': int(p.get('credits_cost') or 0), 'GoldCost': int(p.get('base_cost') or 0)}
    if 'FLAG_NoGift' in adj:
        e['NoGift'] = True
    return e

groups = defaultdict(list)
for tid, p in items.items():
    adj = set(p.get('adjectives') or [])
    if (p.get('credits_cost') or 0) <= 0 or p.get('developer_only') or p.get('retired') or adj & {'Unreleased', 'FLAG_Retired'}:
        continue
    if category(p) is None:
        continue
    key = (p['_kind'], p.get('display_name') or str(tid), (p.get('sub_type') or '').lower(), p.get('school') or '')
    groups[key].append((int(p.get('level') or 0), tid, p))

single, versioned = [], []
for key, members in sorted(groups.items(), key=lambda kv: min(m[1] for m in kv[1])):
    if any(tid in listed for _, tid, _ in members):
        continue
    members.sort(key=lambda m: (m[0], m[1]))
    cat = category(members[0][2])
    levels = {m[0] for m in members}
    if key[0] in ('gear', 'bundle') and len(levels) > 1:
        versions, seen = [], set()
        for level, tid, p in members:
            if level in seen:
                continue
            seen.add(level)
            versions.append(dict(Level=level, **entry(tid, p)))
        versioned.append({'Category': cat, 'Name': key[1], 'Versions': versions})
    else:
        for _, tid, p in members:
            single.append(dict(Category=cat, Name=p.get('display_name') or '', **entry(tid, p)))

with open(out_path, 'w', encoding='utf-8') as f:
    json.dump({'Items': single, 'Versioned': versioned}, f, separators=(',', ':'), ensure_ascii=False)
    f.write('\n')
print(f'{len(single)} items and {len(versioned)} items with level versions '
      f'({sum(len(v["Versions"]) for v in versioned)} versions).')
