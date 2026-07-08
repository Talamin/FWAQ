#!/usr/bin/env python
"""
Dev-time enrichment: derive the exact "use the item HERE" coordinate for use-item quests from the
world DB, so we no longer hand-tune coordinates.

Key insight (validated in-game): the use spot of a use-item quest IS a GameObject -- a SPELL_FOCUS
(gameobject_template.type=8, e.g. the "Spring Well" a waterskin is filled at) or its paired GOOBER
(type=10, e.g. the "Shaman Shrine"). quest_poi is only a rough map marker (18-43y off), so it's a TRAVEL
anchor; the GameObject gives the precise point. Runtime closes the last yards with a direct
MovementManager.MoveTo push (WAQStateUseItem).

SHIP FILTER: we only emit a step for a quest the base quester CANNOT already do natively -- i.e. one with
NO derivable objective (no RequiredNpcOrGo kill/interact, and no RequiredItem that has a loot source).
Class quests are always emitted (their "use item -> spawns the kill/turn-in" pattern needs the step even
when a kill objective exists).

Outputs:
  tools/ClassQuestSteps.generated.json  -- ALL detected use-item quests with a nearby SPELL_FOCUS/GOOBER (reference)
  tools/ClassQuestSteps.shippable.json  -- the SAFE subset to ship (class + world-with-no-derivable-objective)
"""
import re, io, os, math, json

BASE = r'C:\Users\Daniel\Wholesome\Datenbank\sql\base'
OUT_ALL = os.path.join(os.path.dirname(__file__), 'ClassQuestSteps.generated.json')
OUT_SHIP = os.path.join(os.path.dirname(__file__), 'ClassQuestSteps.shippable.json')

NEAR_MAX = 55.0   # > this from quest_poi => don't emit (item is used on a target, not at a GO)
CLASS_SORTS = {-22, -61, -81, -82, -101, -141, -161, -262, -201, -121}

def rd(f):
    p = os.path.join(BASE, f)
    return io.open(p, encoding='utf-8', errors='replace').read() if os.path.exists(p) else ''

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
            yield txt[i + 1:j - 1]; i = j
        else: i += 1

def ival(s):
    try: return int(s.strip())
    except: return 0

def clean(s):
    return s.strip().strip("'").replace("\\'", "'").replace('\\"', '"').replace("\\\\", "\\")

# ---------- item_template: on-use items + names ----------
itxt = rd('item_template.sql'); ic = col_index(itxt)
I_ENTRY, I_NAME, I_SP1, I_TRG1 = ic['entry'], ic['name'], ic['spellid_1'], ic['spelltrigger_1']
I_CLASS, I_SUB = ic['class'], ic['subclass']
# Generic class-0 (Consumable) subclasses whose on-use spell is a self heal/buff, NEVER a quest "use it HERE":
# 1 Potion, 2 Elixir, 3 Flask, 5 Food & Drink, 7 Bandage. Excluding them stops false-positive steps like
# "use Minor Healing Potion at the Anvil" on talk-to/kill quests (e.g. 805 Report to Sen'jin Village) that
# merely carry -- or follow a quest that rewards -- such a consumable. Real quest use-items are class 12
# (Quest) / 15 (Misc) or a class-0 subclass we keep. (Talamin)
GENERIC_CONSUMABLE_SUB = {1, 2, 3, 5, 7}
use_items = set(); item_name = {}
n_skipped_generic = 0
for row in iter_rows(itxt):
    f = split_fields(row)
    if len(f) <= max(I_TRG1, I_CLASS, I_SUB): continue
    e = ival(f[I_ENTRY]); item_name[e] = clean(f[I_NAME])
    if ival(f[I_SP1]) > 0 and ival(f[I_TRG1]) == 0:
        if ival(f[I_CLASS]) == 0 and ival(f[I_SUB]) in GENERIC_CONSUMABLE_SUB:
            n_skipped_generic += 1; continue
        use_items.add(e)
print(f"item_template: {len(item_name)} items, {len(use_items)} on-use ({n_skipped_generic} generic consumables excluded)")

# ---------- loot tables: item ids obtainable by loot (=> the base quester CAN derive an objective) ----------
lootable = set()
for lf in ('creature_loot_template.sql', 'gameobject_loot_template.sql', 'item_loot_template.sql'):
    for row in iter_rows(rd(lf)):
        ff = row.split(',')
        if len(ff) >= 2: lootable.add(ival(ff[1]))
print(f"lootable items: {len(lootable)}")

# ---------- quest_template ----------
qtxt = rd('quest_template.sql'); qc = col_index(qtxt)
quests = {}
for row in iter_rows(qtxt):
    f = split_fields(row)
    if len(f) <= qc['RequiredItemId6']: continue
    qid = ival(f[qc['ID']])
    quests[qid] = {
        'sort': ival(f[qc['QuestSortID']]),
        'prev': ival(f[qc['PrevQuestId']]),
        'src': ival(f[qc['SourceItemId']]),
        'rewards': [ival(f[qc['RewardItem%d' % k]]) for k in range(1, 5)],
        'reqitem': ival(f[qc['RequiredItemId1']]),
        'reqitems': [ival(f[qc['RequiredItemId%d' % k]]) for k in range(1, 7)],
        'npcgo': [ival(f[qc['RequiredNpcOrGo%d' % k]]) for k in range(1, 5)],
        'title': clean(f[qc['LogTitle']]),
    }
print(f"quest_template: {len(quests)} quests")

# ---------- detect use-item quests ----------
def use_item_for(qid):
    q = quests[qid]
    for c in [q['src']] + (quests[q['prev']]['rewards'] if q['prev'] in quests else []):
        if c in use_items: return c
    return 0
detected = {qid: it for qid, it in ((q, use_item_for(q)) for q in quests) if it}
print(f"use-item quests detected: {len(detected)}")

def is_derivable(qid):
    q = quests[qid]
    if any(x != 0 for x in q['npcgo']): return True            # kill (>0) or interact-GO (<0)
    if any(it in lootable for it in q['reqitems'] if it): return True  # lootable required item
    return False

# ---------- quest_poi anchors ----------
poi_map = {}
for row in iter_rows(rd('quest_poi.sql')):
    f = row.split(',')
    if len(f) >= 4:
        try: poi_map[(int(f[0]), int(f[1]))] = int(f[3])
        except: pass
anchors = {}
for row in iter_rows(rd('quest_poi_points.sql')):
    f = row.split(',')
    if len(f) < 5: continue
    try: qid, pid, x, y = int(f[0]), int(f[1]), float(f[3]), float(f[4])
    except: continue
    mp = poi_map.get((qid, pid))
    if qid in detected and mp is not None:
        anchors.setdefault(qid, []).append((mp, x, y))

# ---------- SPELL_FOCUS/GOOBER templates + spawns ----------
gt = rd('gameobject_template.sql'); gtc = col_index(gt)
focus_go = {}
for row in iter_rows(gt):
    f = split_fields(row)
    if len(f) <= gtc['type']: continue
    if ival(f[gtc['type']]) in (8, 10):
        focus_go[ival(f[gtc['entry']])] = (ival(f[gtc['type']]), clean(f[gtc['name']]))
spawns_by_map = {}
for row in iter_rows(rd('gameobject.sql')):
    f = row.split(',')
    if len(f) < 8: continue
    try:
        e = int(f[1])
        if e not in focus_go: continue
        spawns_by_map.setdefault(int(f[2]), []).append((e, float(f[5]), float(f[6]), float(f[7])))
    except: continue

TYPES = {8: 'SPELL_FOCUS', 10: 'GOOBER'}
def nearest_focus(qid):
    best = None
    for (amap, ax, ay) in anchors.get(qid, []):
        for (e, x, y, z) in spawns_by_map.get(amap, []):
            d = math.hypot(x - ax, y - ay)
            if best is None or d < best[0]:
                typ, nm = focus_go[e]; best = (d, e, TYPES[typ], nm, amap, x, y, z)
    return best

all_out, ship_out = [], []
skipped_native = 0
for qid, itemId in detected.items():
    go = nearest_focus(qid)
    if not go or go[0] > NEAR_MAX: continue
    d, e, tn, nm, gmap, x, y, z = go
    q = quests[qid]
    is_class = q['sort'] in CLASS_SORTS
    derivable = is_derivable(qid)
    entry = {
        "QuestId": qid, "Action": "use-item",
        "ItemId": itemId, "ItemName": item_name.get(itemId, ''),
        "CompleteItemId": q['reqitem'], "CompleteItemName": item_name.get(q['reqitem'], '') if q['reqitem'] else "",
        "ObjectiveIndex": 1, "Map": gmap,
        "X": round(x, 3), "Y": round(y, 3), "Z": round(z, 3), "Tolerance": 0,
        "Comment": f"AUTO from DB: use {item_name.get(itemId,'')} at {nm!r} ({tn} GO {e}, {d:.1f}y from quest_poi). {q['title']!r}."
    }
    all_out.append(entry)
    shippable = is_class or not derivable
    if shippable:
        ship_out.append(entry)
    else:
        skipped_native += 1

all_out.sort(key=lambda e: e['QuestId']); ship_out.sort(key=lambda e: e['QuestId'])
json.dump(all_out, io.open(OUT_ALL, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
json.dump(ship_out, io.open(OUT_SHIP, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)

ship_class = [e for e in ship_out if quests[e['QuestId']]['sort'] in CLASS_SORTS]
ship_world = [e for e in ship_out if quests[e['QuestId']]['sort'] not in CLASS_SORTS]
print(f"\nwith a SPELL_FOCUS/GOOBER within {NEAR_MAX:.0f}y: {len(all_out)}")
print(f"  SHIPPABLE (class OR no derivable objective): {len(ship_out)}  ->  class {len(ship_class)} + world {len(ship_world)}")
print(f"  skipped (base quester already derives an objective): {skipped_native}")
print(f"\nwrote {OUT_ALL}  ({len(all_out)})")
print(f"wrote {OUT_SHIP}  ({len(ship_out)})")
