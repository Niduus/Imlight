"""
Writes the real booster pack loot tables from Wizard404's pack pages (data-pack.jsonl.gz) into
spiraldb-extra/DropTables/droptables_pack_<name>.json, one file per table.

- Each gear piece that comes in level versions is listed once (its lowest level version); the server swaps it for
  the wizard's level when the pack opens (PackGearLevels.cs), as the real game picks among the versions for your level.
- Chance is the item's weight in the table (Wizard404's drop_rate_ratio, in percent).

Run: python3 -I make_pack_tables.py <data-pack.jsonl.gz> <spiraldb-extra/DropTables>
"""
import gzip, json, os, re, sys
from collections import OrderedDict

src, out = sys.argv[1], sys.argv[2]
tables = OrderedDict()
catalog = {}

for line in gzip.open(src, 'rt', encoding='utf-8'):
    page = json.loads(line)
    configs = page.get('json', {}).get('loot-drops-config mob-drops-config') or []
    for cfg in configs:
        for entry in cfg.get('lootEntryCatalog') or []:
            catalog.setdefault(entry['template_id'], entry)
        for group in cfg.get('lootTableGroups') or []:
            name = group.get('table_name')
            if name and group.get('entries') and name not in tables:
                tables[name] = (page.get('name'), group['entries'])

def version_key(entry):
    parsed = entry.get('parsed') or {}
    if entry.get('item_type') != 'gear' or parsed.get('sub_type', entry.get('sub_type')) in ('Pet', 'Mount', None):
        return None
    return (entry['display_name'], entry.get('sub_type'), parsed.get('school'))

written = 0
for name, (pack, entries) in tables.items():
    items, seen = [], {}
    ratios = [e['drop_rate_ratio'] for e in entries if e.get('drop_rate_ratio')]
    default = ratios[0] if ratios else {'numerator': 1, 'denominator': max(1, len(entries))}
    for e in entries:
        if 'template_id' not in e:  # a gold entry (two Skeleton Key bundles)
            continue
        info = catalog.get(e['template_id'], {})
        ratio = e.get('drop_rate_ratio') or default
        chance = round(100.0 * ratio['numerator'] / max(1, ratio['denominator']), 4)
        key = version_key(info)
        level = (info.get('parsed') or {}).get('level') or 0
        if key is not None and key in seen:
            kept = seen[key]
            if level < kept['_level']:
                kept.update(ItemId=str(e['template_id']), _level=level)
            continue
        item = {'ItemId': str(e['template_id']), 'ItemName': info.get('display_name', ''), 'Notes': '', 'Chance': chance,
                '_level': level}
        if key is not None:
            seen[key] = item
        items.append(item)
    for item in items:
        item.pop('_level')
    table = OrderedDict(Name=name, Description=f'{pack} (from Wizard404)', RollChance=1, Weight=100, NoneChance=0,
                        PityCounter=0, MinGold=0, MaxGold=0, ExperienceAmount=0, TrainingPoints=0, Items=items)
    safe = re.sub(r'[^a-z0-9_.-]+', '-', name.lower())
    with open(os.path.join(out, f'droptables_pack_{safe}.json'), 'w', encoding='utf-8') as f:
        json.dump(table, f, indent=2, ensure_ascii=False)
        f.write('\n')
    written += 1

print(f'{written} pack tables written from {len(tables)} found.')
