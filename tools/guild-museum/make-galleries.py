"""
Makes src/Imlight.Director/spiraldb-extra/GuildMuseum/Galleries.json, the Guild Museum's galleries, exhibits and
artifacts, from a COPY of the game files (nothing in the game folder is changed).

  python3 make-galleries.py <decoded Root.wad folder> "<Key details folder>" <out.json>

The decoded Root.wad folder needs GuildMuseum/*Gallery.xml.json (the BINd files decoded to JSON, as the Manager's
Game Files tab does), Locale/en-US/GuildMuseum.lang and Locale/en-US/GUI3.lang. Key details is the Game Files tab's
folder (Creatures.csv and Items and objects.csv).
"""
import csv, glob, json, os, re, sys
from collections import defaultdict

root, details, out = sys.argv[1], sys.argv[2], sys.argv[3]


def lang(name, prefix):
    text = open(os.path.join(root, 'Locale', 'en-US', name), 'rb').read().decode('utf-16').replace('\r', '').split('\n')
    found, i = {}, 0
    while i < len(text):
        key = text[i]
        if key.isdigit() and len(key) == 8:
            j = i + 1
            while j < len(text) and text[j] == '':
                j += 1
            found[prefix + key] = text[j].strip() if j < len(text) else ''
            i = j + 1
        else:
            i += 1
    return found


words = {**lang('GuildMuseum.lang', 'GuildMuseum_'), **lang('GUI3.lang', 'GUI3_')}
items = {}
for row in csv.DictReader(open(os.path.join(details, 'Items and objects.csv'), encoding='utf-8-sig')):
    items[row['Template ID']] = row['Shown as'] or row['Name']
creatures = defaultdict(list)
for row in csv.DictReader(open(os.path.join(details, 'Creatures.csv'), encoding='utf-8-sig')):
    creatures[row['Shown as'].strip().lower()].append(int(row['Template ID']))

galleries = []
for path in sorted(glob.glob(os.path.join(root, 'GuildMuseum', '*Gallery.xml.json'))):
    data = json.load(open(path))
    gallery = {
        'World': data['m_worldName'],
        'Zone': data['m_galleryZone'],
        'DoorTemplateID': data['m_worldDoorTemplateID'],
        'LevelScale': data['m_levelScaleLevel'],
        'Exhibits': [],
    }
    for exhibit in data['m_exhibitList']:
        entry = {'Tag': exhibit['m_tagName'], 'Name': words.get(exhibit['m_displayName'], exhibit['m_tagName']),
                 'NameKey': exhibit['m_displayName'], 'Artifacts': []}
        for artifact in exhibit['m_artifactList']:
            source = words.get(artifact['m_overrideLocation'], '')
            match = re.match(r'(.+?) in ', source)
            ids = sorted(set(creatures.get(match.group(1).strip().lower(), []))) if match and not source.startswith('Cantrip') else []
            entry['Artifacts'].append({
                'TemplateID': artifact['m_templateID'],
                'Name': artifact['m_overrideDisplayName'] or items.get(str(artifact['m_templateID']), str(artifact['m_templateID'])),
                'Tiers': [artifact['m_tier1'], artifact['m_tier2'], artifact['m_tier3'], artifact['m_tier4']],
                'From': source,
                'Creatures': ids,
            })
        gallery['Exhibits'].append(entry)
    galleries.append(gallery)

os.makedirs(os.path.dirname(out) or '.', exist_ok=True)
json.dump({'Galleries': galleries}, open(out, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
print(out, sum(len(e['Artifacts']) for g in galleries for e in g['Exhibits']), 'artifacts')
