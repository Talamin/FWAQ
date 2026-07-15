#!/usr/bin/env python
"""
Dev-time enrichment: derive "use the item ON this creature/gameobject" steps for use-item quests from the
world DB, so the use-item-on-npc entries in Database/QuestSteps.json are REPRODUCIBLE (they were produced
by a one-off script once; this is the durable tool) and both factions are covered systematically.

Signals, in confidence order (validated by back-deriving ALL 175 previously shipped entries):
  1. `conditions` rows: SourceTypeOrReferenceId=18 (SPELL_IMPLICIT_TARGET) + ConditionTypeOrReference=31
     (OBJECT_ENTRY_GUID) map spell -> (TypeID 3 creature / 5 gameobject, entry). Precise but RARE in this dump
     (3 rows) -- 3.3.5 AC keeps most spell targeting in the spell DBC, not the world DB.
  2. THE workhorse (reproduces the whole existing set): the quest's **SourceItemId** has an on-use spell
     (non-generic-consumable) and a RequiredNpcOrGo creature credit exists. All 175 shipped entries are exactly
     src-item + target-in-credits. Single-credit quests are unambiguous -> shippable; multi-credit quests are
     ambiguous (WHICH credit gets the item?) -> held for review, one candidate row per credit.

Held for review (NOT shipped): kill-credit indirection (conditions target != credit entry -- the runtime step
lookup keys on RequiredNpcOrGo and would never fire), multi-credit candidates, and prev-reward/required-item
candidates (the item came from elsewhere than SourceItemId -- plausible, unproven).

Outputs:
  tools/UseItemOnNpcSteps.generated.json   -- everything detected, with a Signal field (reference)
  tools/UseItemOnNpcSteps.shippable.json   -- the safe creature-target subset, minus quests already covered
  tools/UseItemOnGoSteps.generated.json    -- gameobject-target hits (input for the use-item-on-go task work)
(Talamin)
"""
import io, json, math, os, re

BASE = r'C:\Users\Daniel\Wholesome\Datenbank\sql\base'
HERE = os.path.dirname(__file__)
EXISTING_STEPS = os.path.join(HERE, r'..\Wholesome_Auto_Quester\Database\QuestSteps.json')
OUT_ALL = os.path.join(HERE, 'UseItemOnNpcSteps.generated.json')
OUT_SHIP = os.path.join(HERE, 'UseItemOnNpcSteps.shippable.json')
OUT_GO = os.path.join(HERE, 'UseItemOnGoSteps.generated.json')

CLASS_SORTS = {-22, -61, -81, -82, -101, -141, -161, -262, -201, -121}
GENERIC_CONSUMABLE_SUB = {1, 2, 3, 5, 7}   # potion/elixir/flask/food/bandage (see generate_classquest_steps.py)

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

# ---------- item_template: on-use items (all 5 spell slots), names, class ----------
itxt = rd('item_template.sql'); ic = col_index(itxt)
item_name = {}; item_class = {}; item_spells = {}
n_generic = 0
for row in iter_rows(itxt):
    f = split_fields(row)
    if len(f) <= ic['spelltrigger_5']: continue
    e = ival(f[ic['entry']])
    item_name[e] = clean(f[ic['name']])
    item_class[e] = (ival(f[ic['class']]), ival(f[ic['subclass']]))
    spells = []
    for k in range(1, 6):
        sp = ival(f[ic['spellid_%d' % k]])
        if sp > 0 and ival(f[ic['spelltrigger_%d' % k]]) == 0:   # 0 = ON USE
            spells.append(sp)
    if spells:
        if item_class[e][0] == 0 and item_class[e][1] in GENERIC_CONSUMABLE_SUB:
            n_generic += 1
        else:
            item_spells[e] = spells
print(f"item_template: {len(item_name)} items, {len(item_spells)} on-use ({n_generic} generic consumables excluded)")

# ---------- conditions: spell implicit target restrictions ----------
# SourceTypeOrReferenceId=18 (SPELL_IMPLICIT_TARGET), SourceEntry=spell, ConditionTypeOrReference=31
# (OBJECT_ENTRY_GUID): ConditionValue1=TypeID (3 unit / 5 gameobject), ConditionValue2=entry.
ctxt = rd('conditions.sql')
spell_unit_targets = {}   # spell -> set(creature entry)
spell_go_targets = {}     # spell -> set(gameobject entry)
n_cond = 0
for row in iter_rows(ctxt):
    f = split_fields(row)
    if len(f) < 10: continue
    if ival(f[0]) != 18 or ival(f[5]) != 31: continue
    spell = ival(f[2]); typeid = ival(f[7]); entry = ival(f[8])
    if spell <= 0 or entry <= 0: continue
    n_cond += 1
    if typeid == 3: spell_unit_targets.setdefault(spell, set()).add(entry)
    elif typeid == 5: spell_go_targets.setdefault(spell, set()).add(entry)
print(f"conditions(18/31): {n_cond} rows -> {len(spell_unit_targets)} spells with unit targets, {len(spell_go_targets)} with GO targets")

# ---------- creature_template: names + kill-credit indirection ----------
cttxt = rd('creature_template.sql'); cc = col_index(cttxt)
creature_name = {}; kill_credit = {}
for row in iter_rows(cttxt):
    f = split_fields(row)
    if len(f) <= cc['KillCredit2']: continue
    e = ival(f[cc['entry']])
    creature_name[e] = clean(f[cc['name']])
    kc = [ival(f[cc['KillCredit1']]), ival(f[cc['KillCredit2']])]
    kill_credit[e] = [k for k in kc if k > 0]
print(f"creature_template: {len(creature_name)} creatures")

# ---------- gameobject_template names (for the GO output) ----------
gt = rd('gameobject_template.sql'); gtc = col_index(gt)
go_name = {}
for row in iter_rows(gt):
    f = split_fields(row)
    if len(f) <= gtc['name']: continue
    go_name[ival(f[gtc['entry']])] = clean(f[gtc['name']])

# ---------- spawn counts: a target with ZERO world spawns can never generate a runtime task (the task loops
# iterate the spawn list) -- e.g. SUMMONED credits (Warlock demons, 'Weakened' boss variants). Review, not ship.
creature_spawns = {}
for row in iter_rows(rd('creature.sql')):
    f = row.split(',')
    if len(f) >= 2:
        creature_spawns[ival(f[1])] = creature_spawns.get(ival(f[1]), 0) + 1
go_spawns = {}
for row in iter_rows(rd('gameobject.sql')):
    f = row.split(',')
    if len(f) >= 2:
        go_spawns[ival(f[1])] = go_spawns.get(ival(f[1]), 0) + 1
print(f"spawns: {len(creature_spawns)} creature entries, {len(go_spawns)} GO entries")

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
        'reqitems': [ival(f[qc['RequiredItemId%d' % k]]) for k in range(1, 7)],
        'npcgo': [ival(f[qc['RequiredNpcOrGo%d' % k]]) for k in range(1, 5)],
        'title': clean(f[qc['LogTitle']]),
    }
print(f"quest_template: {len(quests)} quests")

# candidate on-use item for a quest: its source item, the previous quest's rewards, or an on-use required item
def candidate_items(qid):
    q = quests[qid]
    cands = [q['src']]
    if q['prev'] in quests:
        cands += quests[q['prev']]['rewards']
    cands += q['reqitems']
    seen = set(); out = []
    for c in cands:
        if c and c in item_spells and c not in seen:
            seen.add(c); out.append(c)
    return out

# ---------- existing steps (curation/previous runs win; never duplicate) ----------
existing = set()
with io.open(EXISTING_STEPS, encoding='utf-8-sig') as fh:
    for e in json.load(fh):
        if e.get('Action') == 'use-item-on-npc':
            existing.add((e['QuestId'], e.get('TargetEntry', 0)))
print(f"existing use-item-on-npc steps: {len(existing)}")

# ---------- derive ----------
all_out, ship_out, go_out = [], [], []
n_reproduced = 0
seen = set()   # (qid, target, item) already emitted

def emit(qid, q, item, target, obj_index, signal, ship, is_go=False):
    global n_reproduced
    if (qid, target, item) in seen: return
    seen.add((qid, target, item))
    tname = go_name.get(target, '?') if is_go else creature_name.get(target, '?')
    n_spawns = (go_spawns if is_go else creature_spawns).get(target, 0)
    if n_spawns == 0 and ship:
        signal += ' no-spawns REVIEW'   # summoned/transformed credit: no runtime task possible today
        ship = False
    entry = {
        "QuestId": qid, "Action": "use-item-on-go" if is_go else "use-item-on-npc",
        "ItemId": item, "ItemName": item_name.get(item, ''),
        "CompleteItemId": 0, "CompleteItemName": "",
        "ObjectiveIndex": obj_index, "Map": 0,
        "X": 0, "Y": 0, "Z": 0, "Tolerance": 0,
        "TargetEntry": target,
        "Comment": f"AUTO from DB ({signal}): use {item_name.get(item,'')} ({item}) on "
                   f"{'GO ' if is_go else ''}{tname} ({target}), {n_spawns} spawns. {q['title']!r}.",
    }
    if is_go:
        go_out.append({**entry, "Signal": signal})
        return
    all_out.append({**entry, "Signal": signal})
    if (qid, target) in existing:
        n_reproduced += 1
    elif ship:
        ship_out.append(entry)

for qid, q in quests.items():
    credits = {e: i + 1 for i, e in enumerate(q['npcgo']) if e > 0}       # creature entry -> 1-based objective
    go_credits = {-e: i + 1 for i, e in enumerate(q['npcgo']) if e < 0}   # GO entry -> 1-based objective
    cands = candidate_items(qid)

    # tier 1: conditions-based (precise, rare)
    for item in cands:
        for spell in item_spells[item]:
            for target in sorted(spell_unit_targets.get(spell, ())):
                if target in credits:
                    emit(qid, q, item, target, credits[target], 'conditions+credit', True)
                elif any(k in credits for k in kill_credit.get(target, ())):
                    oi = next(credits[k] for k in kill_credit.get(target, ()) if k in credits)
                    emit(qid, q, item, target, oi, 'conditions+killcredit-indirection REVIEW', False)
            for target in sorted(spell_go_targets.get(spell, ())):
                if target in go_credits:
                    emit(qid, q, item, target, go_credits[target], 'conditions+credit', True, is_go=True)

    # tier 2: the workhorse -- SourceItemId with an on-use spell + creature/GO credits
    src = q['src']
    if src in item_spells:
        if len(credits) == 1:
            (target, oi), = credits.items()
            emit(qid, q, src, target, oi, 'src-item+single-credit', True)
        else:
            for target, oi in credits.items():
                emit(qid, q, src, target, oi, 'src-item+multi-credit REVIEW', False)
        if len(go_credits) == 1 and not credits:
            (target, oi), = go_credits.items()
            emit(qid, q, src, target, oi, 'src-item+single-go-credit', True, is_go=True)
        elif go_credits:
            for target, oi in go_credits.items():
                emit(qid, q, src, target, oi, 'src-item+go-credit REVIEW', False, is_go=True)

    # tier 3: item from the previous quest's rewards / a required item -> review only
    for item in cands:
        if item == src: continue
        for target, oi in credits.items():
            emit(qid, q, item, target, oi, 'nonsrc-item REVIEW', False)

all_out.sort(key=lambda e: (e['QuestId'], e['TargetEntry']))
ship_out.sort(key=lambda e: (e['QuestId'], e['TargetEntry']))
go_out.sort(key=lambda e: (e['QuestId'], e['TargetEntry']))
json.dump(all_out, io.open(OUT_ALL, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
json.dump(ship_out, io.open(OUT_SHIP, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
json.dump(go_out, io.open(OUT_GO, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)

from collections import Counter
sig_counts = Counter(e['Signal'] for e in all_out)
n_class = sum(1 for e in ship_out if quests[e['QuestId']]['sort'] in CLASS_SORTS)
print(f"\ndetected total (creature, incl. review-only): {len(all_out)}")
print(f"  reproduced already-shipped steps: {n_reproduced} / {len(existing)}")
print(f"  NEW shippable (not yet covered): {len(ship_out)}  (class quests among them: {n_class})")
for sig, n in sig_counts.most_common():
    print(f"    {sig}: {n}")
print(f"  GO-target steps (for use-item-on-go work): {len(go_out)}")
print(f"\nwrote {OUT_ALL}\nwrote {OUT_SHIP}\nwrote {OUT_GO}")
