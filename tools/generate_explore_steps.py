#!/usr/bin/env python
"""
Dev-time enrichment for EXPLORE quests. The base quester ships no areatrigger data, so "explore this
area" objectives never generate. We feed one from the DB: areatrigger_involvedrelation gives which quests
are explore quests; quest_poi gives the objective's (x,y); the Z (missing from quest_poi) is taken from
the nearest creature spawn. Emits Action="explore" steps merged into Database/ClassQuestSteps.json.
"""
import re, io, os, math, json
BASE = r'C:\Users\Daniel\Wholesome\Datenbank\sql\base'
PRODUCT = r'C:\Users\Daniel\Wholesome\Wholesome-Auto-Quester-Rework\Wholesome_Auto_Quester\Database\ClassQuestSteps.json'

def rd(f): return io.open(os.path.join(BASE, f), encoding='utf-8', errors='replace').read()
def iter_rows(t):
    i, n = 0, len(t)
    while i < n:
        if t[i] == '(':
            depth = 1; j = i + 1; inq = False; prev = ''
            while j < n and depth > 0:
                c = t[j]
                if c == "'" and prev != '\\': inq = not inq
                elif not inq and c == '(': depth += 1
                elif not inq and c == ')': depth -= 1
                prev = c; j += 1
            yield t[i + 1:j - 1]; i = j
        else: i += 1

explore_qids = {int(m.group(2)) for m in re.finditer(r'\((\d+),(\d+)\)', rd('areatrigger_involvedrelation.sql'))}
print(f"explore quests (areatrigger_involvedrelation): {len(explore_qids)}")

poi_meta = {}
for row in iter_rows(rd('quest_poi.sql')):
    f = row.split(',')
    if len(f) >= 4:
        try: qid, pid, oi, mp = int(f[0]), int(f[1]), int(f[2]), int(f[3])
        except: continue
        if qid in explore_qids: poi_meta[(qid, pid)] = (oi, mp)
poi_pts = {}
for row in iter_rows(rd('quest_poi_points.sql')):
    f = row.split(',')
    if len(f) >= 5:
        try: qid, pid, x, y = int(f[0]), int(f[1]), float(f[3]), float(f[4])
        except: continue
        if (qid, pid) in poi_meta:
            oi, mp = poi_meta[(qid, pid)]
            poi_pts.setdefault(qid, []).append((oi, mp, x, y))
quest_coord = {}
for qid, pts in poi_pts.items():
    obj = [p for p in pts if p[0] >= 0] or pts
    mp = obj[0][1]
    quest_coord[qid] = (mp, sum(p[2] for p in obj) / len(obj), sum(p[3] for p in obj) / len(obj))

# creature spawns per map for Z lookup
cre_by_map = {}
for row in iter_rows(rd('creature.sql')):
    f = row.split(',')
    if len(f) > 9:
        try: cre_by_map.setdefault(int(f[2]), []).append((float(f[7]), float(f[8]), float(f[9])))
        except: continue
def nearest(mp, x, y):
    best = None
    for (cx, cy, cz) in cre_by_map.get(mp, []):
        d = (cx - x) ** 2 + (cy - y) ** 2
        if best is None or d < best[0]: best = (d, cz)
    return (math.sqrt(best[0]), best[1]) if best else (None, None)

# titles
qtxt = rd('quest_template.sql'); qc = {c: i for i, c in enumerate(re.findall(r'^\s*`(\w+)`', qtxt, re.M))}
titles = {}
for row in iter_rows(qtxt):
    f = row.split(',')
    if len(f) > qc['LogTitle']:
        try: titles[int(f[qc['ID']])] = f[qc['LogTitle']].strip().strip("'")
        except: pass

steps = []
for qid, (mp, x, y) in sorted(quest_coord.items()):
    d, z = nearest(mp, x, y)
    if z is None: continue
    steps.append({
        "QuestId": qid, "Action": "explore",
        "ItemId": 0, "ItemName": "", "CompleteItemId": 0, "CompleteItemName": "",
        "ObjectiveIndex": 1, "Map": mp,
        "X": round(x, 2), "Y": round(y, 2), "Z": round(z, 2), "Tolerance": 0,
        "Comment": f"AUTO from DB: explore area @ quest_poi (map {mp}); Z from nearest creature ({d:.0f}y). {titles.get(qid,'')!r}."
    })
print(f"explore steps with a coord: {len(steps)}")

product = json.load(io.open(PRODUCT, encoding='utf-8'))
have = {e['QuestId'] for e in product}
added = [s for s in steps if s['QuestId'] not in have]
product += added
product.sort(key=lambda e: e['QuestId'])
json.dump(product, io.open(PRODUCT, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
io.open(PRODUCT, 'a', encoding='utf-8').write('\n')
print(f"merged {len(added)} explore steps -> ClassQuestSteps.json (now {len(product)} total)")
