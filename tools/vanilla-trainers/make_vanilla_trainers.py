import sys, json, gzip, glob
raw = sys.argv[1]; out = sys.argv[2]
KIND = {'wiz_training': 'School', 'fishing_training': 'Fishing', 'garden_training': 'Gardening', 'monster_magic_training': 'MonsterMagic'}
def node(r):
    if not r: return None
    c = r.get('class'); n = {'Op': 'OR' if r.get('operator') == 'OR' else 'AND'}
    if r.get('apply_not'): n['Not'] = True
    if c == 'RequirementList':
        n['Class'] = 'List'; n['Items'] = [x for x in (node(i) for i in r.get('requirements') or []) if x]
    elif c == 'ReqHasSpell': n.update(Class='HasSpell', Spell=r['spell_object_name'], Id=r.get('spell_template_id'))
    elif c == 'ReqSchoolOfFocus': n.update(Class='SchoolOfFocus', School=r['magic_school'])
    elif c == 'ReqHasEntry': n.update(Class='HasEntry', Entry=r['entry_name'], Quest=r.get('quest_display_name') or '', QuestKey=r.get('quest_display_name_key') or '')
    elif c == 'ReqHasBadge': n.update(Class='HasBadge', Badge=r['badge_name'], Shown=r.get('badge_display_name') or '')
    elif c == 'ReqHasSecondarySchool': n.update(Class='SecondarySchool', School=r['secondary_school'])
    elif c == 'ReqHasShadowMagic': n.update(Class='ShadowMagic')
    else: raise SystemExit('unknown requirement ' + str(c))
    return n
trainers = {}
for f in sorted(glob.glob(raw + '/api-npc*.jsonl.gz')):
    for line in gzip.open(f, 'rt'):
        o = json.loads(line)
        p = (((o.get('data') or {}).get('npc') or {}).get('parsed_data')) or {}
        t = p.get('trainer') or {}
        if not t.get('spells'): continue
        spells = []
        for s in sorted(t['spells'], key=lambda s: s['order']):
            e = {'Id': s['spell_template_id'], 'Name': s['spell_object_name'], 'School': s['school'] or '',
                 'Kind': KIND[s['source_behavior']], 'Rank': s['school_rank'] or 0, 'Cost': s['training_cost']}
            needs = node(s['requirements'])
            if needs and needs.get('Items'): e['Needs'] = needs
            spells.append(e)
        trainers[p['template_id']] = {'TemplateId': p['template_id'], 'Name': p.get('display_name') or p.get('object_name'),
                                      'Object': p.get('object_name'), 'Spells': spells}
doc = {'_about': "Every spell trainer of the real game (Wizard404, game revision r806919: the NPCs' training lists) with each "
       "spell's level or trade rank, training point cost (for a wizard of another school) and what it needs first (spells, "
       "school of focus, quests, badges, secondary school, shadow magic). Used while [Trainers] VanillaTrainers is on. "
       "Made by tools/vanilla-trainers/make_vanilla_trainers.py from the Wizard404 scrape.",
       'Trainers': [trainers[k] for k in sorted(trainers)]}
json.dump(doc, open(out, 'w'), indent=1, ensure_ascii=False)
print(len(trainers), sum(len(t['Spells']) for t in trainers.values()))
