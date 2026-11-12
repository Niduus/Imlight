import json,sys
old=json.load(open(sys.argv[1])); new=json.load(open(sys.argv[2]))
A={c['name']:c for c in old['classes'].values()}
B={c['name']:c for c in new['classes'].values()}
fixed_sp=0; fixed_enum=0
# check old: SharedPointer<X> mirrors X
for n,c in B.items():
    if n.startswith('class SharedPointer<') and not c['properties'] and not c['bases']:
        inner=n[len('class SharedPointer<'):-1]
        src=B.get(inner)
        if src and (src['properties'] or src['bases']):
            c['bases']=list(src['bases']); c['properties']=json.loads(json.dumps(src['properties'])); fixed_sp+=1
for n,c in B.items():
    if n not in A: continue
    for p,v in c['properties'].items():
        o=A[n]['properties'].get(p)
        if o and o.get('enum_options') and not v.get('enum_options') and o['type']==v['type']:
            v['enum_options']=o['enum_options']; fixed_enum+=1
print('sharedpointer filled',fixed_sp,'enum options kept',fixed_enum, file=sys.stderr)
json.dump(new, open(sys.argv[3],'w'), indent=2, ensure_ascii=False)
# Second pass: properties whose names clash once ".m_full" is cut off (new in r813697: SandboxGameObjectInfo has both
# m_templateID.m_full and a plain m_templateID). The generator writes by hash, so a renamed plain one still matches the game.
d=json.load(open(sys.argv[3])); ren=0
for c in d['classes'].values():
    props=c['properties']; full={p[:-7] for p in props if p.endswith('.m_full')}
    if any(p in full for p in props):
        c['properties']={(p+'_u32' if p in full else p):v for p,v in props.items()}; ren+=1
print('renamed',ren,file=sys.stderr)
json.dump(d, open(sys.argv[3],'w'), ensure_ascii=False)
