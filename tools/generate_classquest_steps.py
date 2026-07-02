#!/usr/bin/env python
"""
Dev-time enrichment: derive the exact "use the item HERE" coordinate for use-item quests from the
world DB, so we no longer hand-tune coordinates.

Key insight (validated in-game with Daniel): the use spot of a use-item quest IS a GameObject -- a
SPELL_FOCUS (gameobject_template.type=8, e.g. the "Spring Well" a waterskin is filled at) or its paired
GOOBER (type=10, e.g. the "Shaman Shrine"). quest_poi is only a rough map marker (18-43y off), so it's a
TRAVEL anchor; the GameObject gives the precise point. Runtime then closes the last yards with a direct
MovementManager.MoveTo push (WAQStateUseItem).

Pipeline: for every quest whose SourceItem OR previous-quest reward is an ON-USE item
(item_template.spellid_1>0 AND spelltrigger_1==0), find the nearest SPELL_FOCUS/GOOBER GameObject to the
quest's quest_poi. If one is close enough, that GO's position is the use spot.

Reads Datenbank/sql/base and writes tools/ClassQuestSteps.generated.json (+ a review table on stdout).
Only the JSON output ships (embedded in the product). Review before replacing the shipped file.
"""
import re, io, os, math, json

BASE = r'C:\Users\Daniel\Wholesome\Datenbank\sql\base'
OUT = os.path.join(os.path.dirname(__file__), 'ClassQuestSteps.generated.json')

NEAR_HIGH = 35.0   # <= this from quest_poi => high confidence
NEAR_MAX = 55.0    # > this => don't emit (the item is used on a target / natural spot, not a GO)

def rd(f):
    return io.open(os.path.join(BASE, f), encoding='utf-8', errors='replace').read()

def col_index(txt):
    return {c: i for i, c in enumerate(re.findall(r'^\s*`(\w+)`', txt, re.M))}

def split_fields(row):
    out = []; cur = ''; inq = False; prev = ''
    for c in row:
        if c == "'" and prev != '\\': inq = not inq; cur += c
        elif c == ',' and not inq: out.append(cur); cur = ''
        else: cur += c
        prev = c
    out.append(cur); return out

def iter_rows(txt):
    """Yield each top-level (...) VALUES row string, respecting quotes/nested parens."""
    i, n = 0, len(txt)
    while i < n:
        if txt[i] == '(':
            depth = 1; j = i + 1; inq = False; prev = ''
            while j < n and depth > 0:
                c = txt[j]
                if c == "'" and prev != '\\': inq = not inq
                elif not inq and c == '(': depth += 1
                elif not inq and c == ')': depth -= 1
                prev = c; j += 1
            yield txt[i + 1:j - 1]
            i = j
        else:
            i += 1

def ival(s):
    s = s.strip()
    try: return int(s)
    except: return 0

def clean(s):
    # strip surrounding quotes + undo SQL escaping so the name matches the in-game name exactly
    return s.strip().strip("'").replace("\\'", "'").replace('\\"', '"').replace("\\\\", "\\")

# ---------- item_template: on-use items + names ----------
itxt = rd('item_template.sql'); ic = col_index(itxt)
I_ENTRY, I_NAME, I_SP1, I_TRG1 = ic['entry'], ic['name'], ic['spellid_1'], ic['spelltrigger_1']
use_items = set()      # entry -> is an on-use item
item_name = {}
for row in iter_rows(itxt):
    f = split_fields(row)
    if len(f) <= I_TRG1: continue
    e = ival(f[I_ENTRY])
    item_name[e] = clean(f[I_NAME])
    if ival(f[I_SP1]) > 0 and ival(f[I_TRG1]) == 0:
        use_items.add(e)
print(f"item_template: {len(item_name)} items, {len(use_items)} on-use")

# ---------- quest_template: index the fields we need ----------
qtxt = rd('quest_template.sql'); qc = col_index(qtxt)
Q = {k: qc[k] for k in ('ID', 'QuestSortID', 'PrevQuestId', 'SourceItemId',
                        'RewardItem1', 'RewardItem2', 'RewardItem3', 'RewardItem4',
                        'RequiredItemId1', 'LogTitle')}
quests = {}   # id -> dict
for row in iter_rows(qtxt):
    f = split_fields(row)
    if len(f) <= Q['RequiredItemId1']: continue
    qid = ival(f[Q['ID']])
    quests[qid] = {
        'sort': ival(f[Q['QuestSortID']]),
        'prev': ival(f[Q['PrevQuestId']]),
        'src': ival(f[Q['SourceItemId']]),
        'rewards': [ival(f[Q['RewardItem%d' % k]]) for k in range(1, 5)],
        'reqitem': ival(f[Q['RequiredItemId1']]),
        'title': f[Q['LogTitle']].strip().strip("'"),
    }
print(f"quest_template: {len(quests)} quests")

# ---------- detect use-item quests ----------
def use_item_for(qid):
    q = quests[qid]
    cands = [q['src']] + (quests[q['prev']]['rewards'] if q['prev'] in quests else [])
    for c in cands:
        if c in use_items:
            return c
    return 0

detected = {qid: use_item_for(qid) for qid in quests}
detected = {qid: it for qid, it in detected.items() if it}
print(f"use-item quests detected: {len(detected)}")

# ---------- quest_poi anchors: qid -> list of (map, x, y) ----------
poi_map = {}
for row in iter_rows(rd('quest_poi.sql')):
    f = row.split(',')
    if len(f) < 4: continue
    try: poi_map[(int(f[0]), int(f[1]))] = int(f[3])
    except: continue
anchors = {}
for row in iter_rows(rd('quest_poi_points.sql')):
    f = row.split(',')
    if len(f) < 5: continue
    try:
        qid, pid, x, y = int(f[0]), int(f[1]), float(f[3]), float(f[4])
    except: continue
    mp = poi_map.get((qid, pid))
    if qid in detected and mp is not None:
        anchors.setdefault(qid, []).append((mp, x, y))

# ---------- gameobject_template: SPELL_FOCUS(8)/GOOBER(10) ----------
gt = rd('gameobject_template.sql'); gtc = col_index(gt)
GT_E, GT_T, GT_N = gtc['entry'], gtc['type'], gtc['name']
GT_D1 = gtc.get('Data1')
focus_go = {}   # entry -> (type, name, data1)
for row in iter_rows(gt):
    f = split_fields(row)
    if len(f) <= GT_T: continue
    typ = ival(f[GT_T])
    if typ not in (8, 10): continue
    d1 = ival(f[GT_D1]) if (GT_D1 is not None and GT_D1 < len(f)) else 0
    focus_go[ival(f[GT_E])] = (typ, clean(f[GT_N]), d1)

# ---------- gameobject spawns of those GOs only: (map, entry, x, y, z) ----------
spawns_by_map = {}
for row in iter_rows(rd('gameobject.sql')):
    f = row.split(',')
    if len(f) < 8: continue
    try:
        e = int(f[1])
        if e not in focus_go: continue
        mp = int(f[2]); x = float(f[5]); y = float(f[6]); z = float(f[7])
    except: continue
    spawns_by_map.setdefault(mp, []).append((e, x, y, z))
print(f"gameobject: {sum(len(v) for v in spawns_by_map.values())} SPELL_FOCUS/GOOBER spawns")

TYPES = {8: 'SPELL_FOCUS', 10: 'GOOBER'}

def nearest_focus(qid):
    best = None
    for (amap, ax, ay) in anchors.get(qid, []):
        for (e, x, y, z) in spawns_by_map.get(amap, []):
            d = math.hypot(x - ax, y - ay)
            if best is None or d < best[0]:
                typ, nm, d1 = focus_go[e]
                best = (d, e, TYPES[typ], nm, amap, x, y, z, d1)
    return best

rows_out = []
for qid, itemId in detected.items():
    go = nearest_focus(qid)
    if not go or go[0] > NEAR_MAX:
        continue
    d, e, tn, nm, gmap, x, y, z, radius = go
    q = quests[qid]
    completeId = q['reqitem']
    rows_out.append({
        'qid': qid, 'dist': round(d, 1), 'title': q['title'], 'item': item_name.get(itemId, ''),
        'go': nm, 'gotype': tn, 'entry': e, 'sort': q['sort'],
        'json': {
            "QuestId": qid, "Action": "use-item",
            "ItemId": itemId, "ItemName": item_name.get(itemId, ''),
            "CompleteItemId": completeId, "CompleteItemName": item_name.get(completeId, '') if completeId else "",
            "ObjectiveIndex": 1, "Map": gmap,
            "X": round(x, 3), "Y": round(y, 3), "Z": round(z, 3), "Tolerance": 0,
            "Comment": f"AUTO from DB: use {item_name.get(itemId,'')} at '{nm}' ({tn} GO {e}, {d:.1f}y from quest_poi). '{q['title']}'."
        }
    })

rows_out.sort(key=lambda r: (r['dist']))
print(f"\n=== {len(rows_out)} use-item quests with a SPELL_FOCUS/GOOBER within {NEAR_MAX:.0f}y ===")
print(f"{'quest':>6} {'dist':>5} {'sort':>5}  {'item':<26} -> {'GameObject':<28} conf")
for r in rows_out:
    conf = 'HIGH' if r['dist'] <= NEAR_HIGH else 'review'
    print(f"{r['qid']:>6} {r['dist']:>5} {r['sort']:>5}  {r['item'][:26]:<26} -> {r['go'][:28]:<28} {conf}")

with io.open(OUT, 'w', encoding='utf-8') as fh:
    json.dump([r['json'] for r in rows_out], fh, indent=2, ensure_ascii=False)
print(f"\nwrote {len(rows_out)} entries -> {OUT}")
