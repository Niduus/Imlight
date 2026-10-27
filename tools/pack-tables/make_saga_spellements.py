"""
Writes the Spellements of each Saga Spellement Pack (Malistaire, Morganthe, Spider, Dasein) from Wizard404's pack
pages (data-pack.jsonl.gz) into spiraldb-extra/Packs/SagaSpellements.json, by arc and school.

The packs' slots (Spellements_P_Elemental_Arc01_01, _Spiritual_, _School_) have no loot table anywhere we can read,
so the server rolls them from this list (SagaSpellements.cs). A Spellement's school is the one in its icon's name
(Praxis/Praxis_Fire_Immolate.dds).

Run: python3 -I make_saga_spellements.py <data-pack.jsonl.gz> <spiraldb-extra/Packs/SagaSpellements.json>
"""
import gzip, json, re, sys

src, out = sys.argv[1], sys.argv[2]
SCHOOLS = ('Fire', 'Ice', 'Storm', 'Life', 'Death', 'Myth', 'Balance')
arcs = {}

for line in gzip.open(src, 'rt', encoding='utf-8'):
    page = json.loads(line)
    if 'Saga Spellement Pack' not in page.get('name', ''):
        continue
    for cfg in page.get('json', {}).get('loot-drops-config mob-drops-config') or []:
        arc = next((m.group(1) for g in cfg.get('lootTableGroups') or []
                    if (m := re.search(r'_(Arc\d+)_', g.get('table_name', '')))), None)
        if arc is None:
            continue
        spellements = arcs.setdefault(arc, {'Pack': page['name'], 'Spellements': []})['Spellements']
        for entry in cfg.get('lootEntryCatalog') or []:
            image = (entry.get('parsed') or {}).get('image_name', '')
            school = next((s for s in SCHOOLS if f'Praxis_{s}_' in image), None)
            if school is None or entry.get('item_type') != 'reagent':
                print(f'skipped {entry.get("display_name")} ({image})', file=sys.stderr)
                continue
            if all(s['Id'] != entry['template_id'] for s in spellements):
                spellements.append({'Id': entry['template_id'], 'Name': entry['display_name'], 'School': school})

for arc in arcs.values():
    arc['Spellements'].sort(key=lambda s: (SCHOOLS.index(s['School']), s['Name']))

with open(out, 'w', encoding='utf-8', newline='\n') as f:
    json.dump(dict(sorted(arcs.items())), f, indent=2, ensure_ascii=False)
    f.write('\n')

for name, arc in sorted(arcs.items()):
    counts = {s: sum(1 for x in arc['Spellements'] if x['School'] == s) for s in SCHOOLS}
    print(name, arc['Pack'], counts)
