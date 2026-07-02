# Roadmap — Class Quests + Use-Item Objectives (and forcing class quests)

## Goal
Make WAQ execute quests whose objective is **"obtain/use a quest item by ACTING at a location"** (the ritual /
use-item-on steps that today generate no objective and stall). We start with the **Shaman totem chains** — which unlock
core class mechanics (the fire/water/air totems themselves) — and build it **generally** so the same machinery unlocks
the ~208 DB-wide "use-item" quests → **more quests overall**, not a shaman one-off. Plus a **forcing/priority
mechanism** so class quests (which unlock mechanics) are done as early as possible.

## The unblocker (why this is feasible now)
The earlier verdict was "not worth it — `AQ.json` has no location data telling the bot WHERE to use the item"
(see the analysis note). The AzerothCore DB (now local at `..\Datenbank`) resolves exactly that:

- **`quest_poi` + `quest_poi_points`** = per-objective map coordinates. **8953 quests** have them — including **all 20**
  Shaman totem-quest steps. Spot-checked and they **match the manually-hardcoded hotspots** in the old EasyQuest
  profiles (e.g. Earth-Sapta stone `(-878,-4290)`, water-use spot `(-3584,-1882)`, Fire-Sapta NPC `(-270,-4001)`).
- The location data `AQ.json` lacked EXISTS in the DB; it just wasn't extracted. Also: **1464 "Fire Sapta" IS in the
  DB** — it was only dropped by the `Db_To_Json` generator's filters, not missing.

So: the blocker was never really "impossible", it was "missing data" — and the data is on board now.

## Design principles
1. **General-first.** Build the reusable pipeline (POI data) + a reusable objective type ("use item at a location").
   Every quest benefits; the shaman chains are just the first consumer.
2. **Data from the DB, not hand-authored.** Extract POI/mechanics from `quest_poi` / `quest_template` / `item_template`
   so it scales to hundreds of quests instead of curating XML per quest.
3. **Additive + reversible.** Don't destabilize the field-hardened core; new objective type + optional data behind a
   setting; keep the current behaviour when a quest has no POI/use-item.
4. **Class quests forced.** A priority (not a hard block by default) so mechanic-unlocking class quests are done first.

---

## Phases

### Phase 0 — POI data into the pipeline *(the enabler; benefits ALL quests)*
- Extend `Db_To_Json/AutoQuesterGeneration.cs` to read **`quest_poi` + `quest_poi_points`** and attach, per quest
  objective (by `ObjectiveIndex`), the POI **coordinates (map + X/Y)** to the emitted data.
- **Delivery choice (pick one):**
  - **A — companion file `AQ_poi.json`** *(recommended):* a separate small file (questId → objective → coords), loaded
    alongside `AQ.json`. Leaves the 26 MB base untouched → no risky full regen (respects the "don't refresh AQ.json"
    stance — this is the SAME source DB, only enriched).
  - **B — regenerate `AQ.json`** with the POI fields inline. Cleaner data model, but touches the whole base.
- **Audit the generator's quest filters** vs the class-quest sorts so class quests currently dropped (e.g. 1464) are
  kept.
- *Effort:* moderate (generator + a one-time extract). *Payoff:* location data available for ~all quests.

### Phase 1 — `UseItemObjective` *(the missing objective type; benefits ALL use-item quests)*
- In `Database/Models/ModelQuestTemplate.cs` objective generation: when a quest has a **RequiredItem with NO loot
  source** (the "use/provide item" pattern the earlier analysis flagged) **AND** a POI exists → emit a new
  **`UseItemObjective(item, poiLocation, optional targetEntry)`**.
- New WAQ state (or extend `WAQStateInteract`): **travel to the POI → `ItemsManager.UseItem(item)`** (optionally on a
  nearby GameObject/creature at the POI). This mirrors the old EasyQuest **`UseItemOn`** step exactly — a proven recipe.
- *Effort:* the real feature, moderate. *Payoff:* unlocks the **~208 DB-wide use-item quests** → materially "more
  quests".

### Phase 2 — POI as objective / navigation fallback *(more quests + better routing)*
- For quests where WAQ builds **no** objective today (the "dead" set) but a POI exists → a **"go to the POI + auto-
  complete / interact"** fallback objective.
- Feed POI coords into **hotspot selection** for EXISTING quests too (a curated POI beats the raw spawn-scan) → tighter
  routing, fewer "can't find target" churns.
- *Effort:* low–moderate. *Payoff:* broad, helps normal quests as well.

### Phase 3 — Class-quest detection + forced prioritization *(the "Zwang")*
- **Detect** class quests: `QuestSortID` in the class-sort set (the negative class sorts — Shaman −82, Warlock −61,
  Rogue −162, Mage −263/−81, Druid −141, …) **AND** `AllowableClasses` matches the player's class.
- **Forcing (two levers, layer as needed):**
  - **(a) Strong priority boost** *(recommended default):* a large `TaskPriority` discount so an available class quest
    is picked **ASAP, before regular quests**. Non-blocking → safe (if it's temporarily unreachable, normal questing
    continues).
  - **(b) Optional soft gate:** prefer not to out-level / leave the zone while an available class quest is undone.
    Behind a setting; off by default (a hard gate risks stalling if the quest is unreachable).
- **Why:** class quests unlock the mechanics the bot's power depends on — **Shaman totems**, Rogue poisons, Hunter
  pet/taming, Warlock pet & mount, etc. A shaman without the totem quests literally can't drop fire/water/air totems.
- New setting **"Prioritize class quests"** (default on) + a tuning knob; decision logic in `.Logic`
  (`TaskPriority` / a new `ClassQuest` helper), unit-tested.
- *Effort:* low (a scoring rule + detection). Independent of Phases 0–2 → **can land first** (it already helps the
  class-quest steps WAQ can do today).

### Phase 4 — The specific chains + travel glue
- Run the Shaman totem chains end-to-end: prereqs via `PreviousQuestsIds` + the existing Phase-6 chain scoring already
  order them.
- Handle the special **travel glue** the old XMLs hardcoded (zeppelin off-mesh, Hearthstone reset). Most is the
  product's continent-travel; add a few per-quest off-mesh connections only where a chain provably needs it.
- *Effort:* per-quest polish.

### Phase 5 — Verify + generalize
- In-game verify the four Shaman totem chains (Earth / Fire / Water / Air) on GREZ.
- Spot-check the `UseItemObjective` on a handful of the ~208 non-class use-item quests to confirm it generalizes.

---

## Sequencing / dependencies — RECOMMENDED ORDER
Build it clean + structured, each step independently valuable and in-game verifiable, no big-bang change to the
field-hardened core:

1. **Phase 0 — POI data** (companion `AQ_poi.json`). Foundation, no behaviour change; DB-wide from the start.
2. **Phase 1 — `UseItemObjective` as a VERTICAL SLICE:** build the objective + state and prove it on ONE shaman ritual
   quest end-to-end, verify in-game, THEN let it apply data-driven to all use-item quests (the generation is generic).
   → After 0+1 the shaman totem chains are completable — verify the four chains here.
3. **Phase 3 — class-quest forcing.** Only NOW — forcing a quest before its ritual steps are completable just makes the
   bot prioritise a quest it then stalls on (churn, and the mechanic never unlocks). Priority boost + optional soft gate.
4. **Phase 2 — POI as nav/objective fallback.** Broad bonus once the POI data is in (helps normal quests too).
5. **Phase 4/5 — travel-glue polish + generalisation verify.**

**Why not Phase 3 first (correcting an earlier note):** the "Zwang" only pays off once the class quest can actually be
FINISHED (Phase 0+1). Forcing an un-completable quest is counter-productive — it stalls, benches, retries.

## Honest risks / open questions
- **Item-use mechanic varies** per step: use-at-ground vs use-on-a-GameObject vs cast-on-target. Phase 1 must classify
  each (from `item_template.spellid_1` + `spell_scripts` / `spell_target_position`) and resolve a target at the POI
  where needed.
- **POI ↔ step mapping:** `quest_poi` is objective-indexed; wiring a POI to the right step needs the `ObjectiveIndex`
  matched correctly (some quests have several POIs).
- **Don't over-force:** a *hard* "must finish before leaving" gate can strand the bot if a class quest is unreachable —
  default to the priority boost, keep any hard gate opt-in.
- **Perf:** POI data is small (coords); no meaningful cost. Regen (option B) vs companion file (option A) is the only
  pipeline decision.
- **Not every class quest is item-use** — many steps are already kill/loot/gossip (WAQ does them today); Phase 1 is
  only needed for the ritual subset.

---

## Phase 1 pre-work — step classification (done) + vertical-slice design (quest 1535)

### Classification of the 20 Shaman steps
~13–14 the WAQ can already do (Kill/Loot, Gossip pickup/turn-in, have-item turn-in). **~4–5 need the new
`UseItemObjective`:** 1517 (Earth Sapta at the stone), 1535 / 1536 / 1534 (the three waterskins), and the use-Torch
part of 1526. **1464 "Fire Sapta"** is in the DB but generator-filtered → must be un-filtered.

**HONEST CAVEAT (important for generalisation):** `quest_template`/`AQ.json` does NOT cleanly encode the use-item
mechanic — e.g. 1517 has no `RequiredItem`, so it *looks* like a gossip quest, but it actually completes when you USE an
item at a spot (via a spell-script/event). So:
- For the **Shaman set**, the accurate mechanic source is Daniel's **old EasyQuest XML profiles** (they classify each
  step explicitly: UseItemOn / KillAndLoot / …) + `quest_poi` for the coordinates (the two match). → a small curated
  data file is enough.
- **Generalising** the use-item *detection* to all quests needs a DB pass over `spell_scripts` / item use-spells /
  `RequiredSpellCast` (the mechanic isn't in a clean field). POI *coordinates* are already universal; detecting *which*
  quest needs a use-item action is the harder, distinct sub-task → tracked as **Phase 1b**.

### Vertical-slice target: quest 1535 "Call of Water3"
Concrete, fully-specified from the DB + XML:
- Giver/ender: **Brine (5899)**, Kalimdor (mapId 1), ~(-3617,-1776). Prereq: **1530**. Next: 1536.
- Objective: **use item 7766 "Empty Brown Waterskin"** at **(-3584,-1882), mapId 1** → yields **7769 "Filled Brown
  Waterskin"** → objective satisfied → turn in at Brine. (`quest_poi`: poiId0/objIdx-1 = giver, poiId1/objIdx4 =
  the use spot.)

### Design (the walking skeleton)
1. **Data file** `Database/ClassQuestSteps.json` (embedded resource, same pattern as `QuestBlacklist.json` +
   `QuestBlacklistData.cs`): per step `{ questId, action:"use-item", itemId, targetEntry?, map, x, y, completeItemId? }`.
   For the slice, one entry (1535). Extracted from the XML (mechanic+item) + `quest_poi` (coords).
2. **New task** `WAQTaskUseItem` — mirror **`WAQTaskExploreLocation`** (the existing COORDINATE-based task, no scanner
   object needed — the use spot is a coordinate, not a scanned unit), + carry `itemId`/`targetEntry`/`completeItemId`.
   `IsValid` while the quest is `ToComplete` and we DON'T yet hold `completeItemId` (7769) — i.e. the objective isn't
   done. Location = (map,x,y).
3. **New FSM state** `WAQStateUseItem` (mirror `WAQStateInteract`, same band): travel to the POI (`GoToTask.ToPosition`
   — already used in `Helpers/MoveHelper.cs`; Z resolved from the nav mesh), then `ItemsManager.UseItem(itemId)`
   (already used in `WAQWaitResurrectionSickness`; optionally `ToPositionAndInteractWithNpc`/cast-on-target when
   `targetEntry` is set). This is exactly what the old EasyQuest `UseItemOn` did (UseItem + hotspot + auto-detect).
4. **Wiring:** `JSONManager`/`QuestManager` loads `ClassQuestSteps.json`; the task builder adds a `WAQTaskUseItem` for a
   quest that has an entry, instead of (or alongside) the loot-derived objectives. Turn-in + prereq chain already work
   (`PreviousQuestsIds`).
5. **Completion:** using 7766 at the spot grants 7769 → the quest objective flips complete in the quest log → the
   existing turn-in flow fires. The `WAQTaskUseItem` self-terminates once `completeItemId` is held (or the quest is
   `ToTurnIn`).
6. **Verify in-game** on GREZ (level a shaman to the water chain, or /run to it): it should travel to (-3584,-1882),
   use the waterskin, then turn in — no stall.

Once 1535 works end-to-end, the same `WAQTaskUseItem` + a few more `ClassQuestSteps.json` entries cover 1517 / 1536 /
1534 / 1526-torch, and 1464 gets un-filtered — the four totem chains complete. THEN Phase 3 (forcing) is safe to add.

### STATUS (built, compiles clean — pending in-game verify)
Implemented and building (Debug, EXIT 0):
- NEW `Database/ClassQuestSteps.json` (+ `.csproj` EmbeddedResource) — data-driven step table. Ships TWO entries:
  **1517** "Call of Earth" (Earth Sapta 6635 at the Durotar stone, ~lvl 4-10 — LOW-LEVEL TEST TARGET for GREZ) and
  **1535** (waterskin, Desolace ~lvl 30).
- NEW `Database/ClassQuestStepsData.cs` — embedded-resource loader (mirrors `QuestBlacklistData`), `GetSteps(questId)`.
- NEW `Bot/TaskManagement/Tasks/WAQTaskUseItem.cs` — coordinate task (mirrors `WAQTaskExploreLocation`, `InteractionType.None`).
- NEW `States/WAQStateUseItem.cs` — fires in-radius, `ItemsManager.UseItem`, graduated-recovery bench on missing item /
  no-complete; registered ABOVE `WAQStateMoveToHotspot` so the arrival-timeout can't kill it.
- EDIT `Bot/QuestManagement/WAQQuest.cs` — new "use item at a location" generation block (gated on `IsObjectiveCompleted`).
- EDIT `Bot/WAQBot.cs` — state registered. EDIT `.csproj` — 3 Compile + 1 EmbeddedResource.

### VERIFIED IN-GAME (2026-07-02)
- **Earth chain COMPLETE end-to-end**: 1516 (kill) → 1517 (use Earth Sapta → Minor Manifestation spawns → turn in) →
  1518 (delivery) → **first Earth Totem obtained.** 1518 needed no code (no spell on its start item).
- Two use-item PATTERNS handled: (a) **spawn-at-spot** (1517 — the turn-in NPC spawns where you use the item) and
  (b) **use-here-turn-in-elsewhere** (waterskins — use at a water spot, hand in back at Brine). Pattern (b) required the
  "release the use-item task once the item is consumed" fix in `WAQStateUseItem` so the bot returns to the turn-in NPC.
- `ClassQuestSteps.json` now ships 4 entries: 1517 (earth), 1535/1536/1534 (the three waterskins; 1536 is cross-continent,
  map 0). Fire (1464 missing from AQ.json + 1526 has no ender) and Air (works out of the box) still TODO.

### GENERALIZATION GOAL (Daniel, explicit — do not lose sight of this)
The end state is DB-WIDE: **every** use-item quest — including ones no character is on today — must participate, not just
the curated shaman list. The CODE machinery is already quest-agnostic (every fix keys on "has a use-item step", not on
specific IDs). What's still hand-authored is the per-step DATA (item + map/x/y/z). The generalization step is to
**auto-derive those steps from the DB instead of hand-authoring**:
- location ← `quest_poi` / `quest_poi_points` (needs Phase 0: POI data into the pipeline as a companion file),
- "this is a use-item step" ← the quest's Start/Required item that `HasASpellAttached` (already how JSONManager detects
  them to filter),
- then build the same `WAQTaskUseItem` automatically for any such quest.
The hand-authored `ClassQuestSteps.json` is the PROVEN spec + the fallback/override for special cases; auto-derivation
replaces the bulk of it. Track: Phase 0 (POI pipeline) → Phase 1b (auto-detect use-item steps DB-wide).

**BOTH FACTIONS via generalization (Daniel, 2026-07-02): the auto-derivation MUST cover Alliance too — it's the same
structure, different quest IDs/givers/mobs.** The CODE fixes (tracking exemption, class-quest priority tier, GO-ender
counting, turn-in-deferral exemption) are already faction-agnostic (keyed on AllowableClasses), so a Draenei shaman's
totem quests are tracked + prioritized automatically TODAY; only the use-item RITUAL steps need step-data. Deferred to
the generalization pass (NOT hand-authored now): Draenei "Call of Water" use-item steps **9501 / 9504 / 9508** (Lvl 20),
plus the remaining Horde use-item entries. Draenei counts: 23 shaman totem quests, 20 auto (gossip/kill/delivery/turn-in),
3 use-item. (Draenei Earth chain has NO use-item step, unlike Horde 1517.)

Next: deploy (needs WRobot CLOSED) → verify the remaining chains as characters reach them; then Phase 0/1b generalization
(covering BOTH factions' use-item steps in one pass).

## Bottom line
Earlier: *"not worth it — no location data."* Now: **the DB supplies the location data (quest_poi) for ~all quests**,
so the same machinery that makes the Shaman totem quests work also unlocks the ~208 use-item quests DB-wide — plus a
priority so the mechanic-unlocking class quests get done first. This is a real feature (a new objective type + a data
step), but it's now genuinely feasible and high-ROI.
