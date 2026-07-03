# FWAQ — Fun with Wholesome Auto Quester

A community enhancement of **Wholesome Auto Quester** for [WRobot](https://wrobot.eu/) (World of Warcraft **3.3.5a / Wrath of the Lich King**), focused on making **class quests** and **"use an item at a location" quests** run fully automatically — the kind of quests the base quester can't derive on its own.

---

## 🙏 Huge credits to ZerO

FWAQ stands entirely on the shoulders of the original **Wholesome Auto Quester** by **ZerO**:

### 👉 https://github.com/Wholesome-wRobot

Everything that makes the quester actually work — the task engine, travel manager, object scanner, quest state machine, JSON data pipeline, the GUI — is **ZerO's work**. FWAQ is only a layer of extra behaviour and data on top of that foundation. None of this would exist without it. **If you use FWAQ, go support the original project first.** ❤️

---

## What FWAQ adds

The base quester is excellent at "normal" quests (kill, gather, talk, escort). It struggles with quests whose objective **can't be derived from the database** — the classic examples being **class quests** (shaman totems, warlock summons, …) where you have to *use a quest item at a very specific spot*. FWAQ makes those work, **driven entirely by the world database — no hand-tuned coordinates.**

### 1. Class-quest priority ("must-do" tier)
Class quests unlock core character mechanics (the four shaman totems, warlock pets, …), so FWAQ gives every class quest a dedicated top-priority tier. The bot will always progress the class quest first — **even across continents** — instead of getting distracted by nearby world quests.

### 2. DB-driven "use item at a location" quests
The core discovery behind FWAQ: **the exact spot where you use a quest item is a GameObject in the DB** — a `SPELL_FOCUS` (type 8) or its paired `GOOBER` (type 10). That's the WoW *"this spell only works near this object"* mechanic (the Spring Well a waterskin is filled at, the Shaman Shrine a sapta is used at, the Summoning Circle a warlock summons on, …).

`quest_poi` alone is only a rough map marker (routinely **18–43 yards off**), so FWAQ uses it purely as a **travel anchor** and then derives the **precise** use-coordinate from the **nearest SPELL_FOCUS/GOOBER GameObject**. Result: sub-yard accuracy across hundreds of quests.

### 3. Adaptive approach (reaching the exact spot)
Many use-spots are solid world objects (wells, shrines, braziers) whose collision the navmesh stops ~2–3 yards short of. FWAQ's use-item state:
1. tries the item from where pathing dropped it (~6 y),
2. if it doesn't take, closes in with `GoToTask.ToPosition(spot, 1f)` (tight precision),
3. and if the navmesh still stops short, pushes the last yards with a **direct `MovementManager.MoveTo`** (no A\*) right up against the object.

So a slightly-imperfect coordinate self-corrects — which is what makes the DB-derived coordinates viable without hand-tuning.

### 4. Cross-continent / transport quests
Some class quests legitimately cross continents (the shaman **Water** totem fills a waterskin in **Eastern Kingdoms** via the **Orgrimmar → Undercity zeppelin**, then returns to Kalimdor). FWAQ lets low-level class quests through the continent gate so the existing transport travel actually fires end-to-end.

### 5. Robustness fixes (help *all* quests, not just use-item ones)
- **False-completion guard** — a quest that leaves the log is only recorded as completed if the **server's finished-set** confirms it. Abandoning a quest no longer poisons the local "completed" list and falsely satisfies downstream prerequisites.
- **Pickup-gossip retry** — when a freshly-unlocked quest hasn't appeared in an NPC's gossip yet (server chain delay), re-open the gossip and retry a few times instead of benching it for 15 minutes and wandering off.
- **Post-loot hold** — after a class-quest kill, hold briefly so a **spawned** turn-in object / NPC (e.g. a script-spawned brazier or manifestation) can appear and be scanned before the bot re-evaluates.
- **Do-Not-Sell protection** — the use-item **and** its result item (e.g. the *Filled* Waterskin) are added to WRobot's Do-Not-Sell list while the quest is active, so the inventory-manager plugin can't delete the objective item as a "deprecated low-level quest item".
- **Cross-faction leak blacklist** — quests with no race restriction in the exported data that are really faction-locked (via the `conditions` table) no longer drag the bot into the enemy faction's zones.

### 6. Dev-time enrichment tool
[`tools/generate_classquest_steps.py`](tools/generate_classquest_steps.py) reads the raw AzerothCore world-DB dumps and **generates** the use-item coordinate data. It:
- detects use-item quests DB-wide (an item with an on-use spell — `spellid_1 > 0 && spelltrigger_1 == 0` — as the quest's SourceItem or the previous quest's reward),
- finds the nearest `SPELL_FOCUS`/`GOOBER` GameObject to each quest's `quest_poi`,
- emits the shippable `ClassQuestSteps.json`.

The heavy DB never ships — only the small generated JSON is embedded in the product.

### 7. Bulk / batch questing
The planner counts open quest work near the player (within ~80 yards) and, when there's more to do right there, **defers a turn-in so the bot finishes the whole cluster before travelling on** — far fewer wasted round-trips across a zone. Class-quest turn-ins are **exempt** from the batching, so a totem/summon quest is always handed in immediately.

### 8. Native settings overlay
A dark, tabbed settings panel drawn **directly over the game window** (ported from the AIO3 fightclass overlay, with a green accent instead of blue). You change quester settings in-game without touching WoW's Lua/UI — edits are two-way bound, persist, and apply on the next planner cycle. It runs on its own STA thread, tracks the WoW window, and gracefully falls back to the normal config window if WPF can't start. Its window is decoupled from the fightclass overlay, so both can sit side by side.

---

## Full list of changes vs. upstream

A granular reference of everything FWAQ changes on top of the base quester (files under `Wholesome_Auto_Quester/` unless noted).

**New quest capability**
- Class-quest **priority tier** — a dedicated must-do tier below all ordinary tasks (`TaskPriority`, `TaskManager`), so class quests always progress, even across continents.
- **Use-item quests** — new `Bot/TaskManagement/Tasks/WAQTaskUseItem.cs` + `States/WAQStateUseItem.cs` with the adaptive approach (GoToTask precise → direct `MovementManager.MoveTo` push).
- An `IsClassQuest` flag threaded through **every** task type (`IWAQTask`, `WAQBaseTask`, all `WAQTask*`).

**New embedded data + loaders**
- `Database/ClassQuestSteps.json` + `ClassQuestStepsData.cs` — the auto-generated use-item steps.
- `Database/QuestBlacklist.json` + `QuestBlacklistData.cs` — the blacklist moved **out of code into data**.

**New: an offline-testable core**
- New project **`Wholesome_Auto_Quester.Logic`** — WRobot-free decision logic extracted so it runs with no game attached: the quest-status ladder, prerequisite checks, chain scoring, task priority (incl. the class-quest tier), task validity, recovery policy.
- New project **`Wholesome_Auto_Quester.Tests`** — **76 xUnit tests** over that logic.

**Reliability fixes (help every quest, not just class quests)**
- **Server-confirmed completion** — a quest that leaves the log is only recorded completed if the server's finished-set confirms it (`Helpers/ToolBox.cs`, `QuestManager`); abandoning no longer poisons the completed list and falsely satisfies prerequisites.
- **Pickup-gossip retry** on spawn delay (`WAQTaskPickupQuestFromCreature`).
- **Post-loot hold** for script-spawned turn-in objects/NPCs (`States/WAQStateLoot.cs`).
- **Do-Not-Sell protection** for objective items so the inventory-manager plugin can't delete them (`QuestManager`).
- **Load-filter fixes** (`Bot/JSONManagement/JSONManager.cs`): class quests are exempt from the neutral/friendly giver-ender strip and correctly class-masked; the "no giver/ender → drop" check now also counts **GameObject** givers/enders (so a GO turn-in isn't dropped).
- `AbandonUnfitQuests` + blacklist **exemptions** for class-quest steps; a quest-objective-index bounds fix.
- **Cross-faction leak blacklist** + a low-level **continent-gate exemption** for class quests (`WAQQuest`).

**Planner / navigation**
- **Bulk / batch questing** — cluster nearby open quest work (~80 y) and finish it before travelling on (`TaskManager`).
- **"Grab a quest you're walking past"** state — interact with a nearer turn-in / giver while travelling to a distant objective, instead of walking straight past it (`States/WAQStateGrabNearbyQuest.cs`).

**UX / dev tooling**
- **Native settings overlay** over the game (`GUI/QuesterOverlay.cs`, `GUI/OverlaySettings.cs`).
- **Performance logging** — per-label p95/max timing of FSM states and background loops (`Helpers/PerfLog.cs`).
- **Dev-time enrichment tool** (`tools/generate_classquest_steps.py`).
- Design notes: `ROADMAP.md`, `ROADMAP-CLASS-QUESTS.md`.

---

## Coverage

FWAQ ships **285 curated quest steps** in `ClassQuestSteps.json` — all DB-generated, no hand-tuning: **~50 class quests**, **183 world use-item quests**, and **55 explore quests**.

### Supported class quests

- **Shaman** — all four totem quest chains: **Call of Earth / Fire / Water** (full, including the cross-continent zeppelin leg and the temporary script-spawned turn-in NPC), for **both factions** incl. the Draenei/Outland variants. *Air* is handled natively.
- **Warlock** — the demon-summoning ritual chain **"The Binding"** (all summon steps) plus the pet quests **"Imp Delivery"**, **"Shard of a Felhound" / "Shard of an Infernal"**, **"The Rune of Summoning"** and **"Kroshius' Infernal Core"**.
- **Warrior** — *Honoring a Hero*, *Tracing the Source*, and the holiday quests (Stink Bomb, Preserved Holly, …).
- **Death Knight** — the Barbaric/Mithril **plans-crafting** chain (*On Iron Pauldrons*, *In Search of Galvan*, …).
- **Rogue** — *Mirror Lake*, *The Purest Water*, *Attunement to Dalaran*.
- **Druid** — *The Pledge of Secrecy* (all three).
- **Paladin** — *The Tome of Divinity*, *Redeeming the Dead*.
- **Mage** — *The Affray*, *Strength of One*, *The Rethban Gauntlet*.

### World quests

- **183 use-item quests** — e.g. the **Cleansing Totem** chain (Winterhoof / Thunderhorn / Wildmane), phials at moonwells, gems & recipes at anvils and cooking fires, *Dartol's Rod of Transformation*.
- **55 explore ("reach an area") quests** — a category the base quester couldn't do at all (it ships no areatrigger data).

> ⚠️ **Important — steps are derived from a reference DB; private servers vary. Do not AFK-bot these quests.**
> Every coordinate and step is generated from a reference AzerothCore WotLK world DB. Private servers routinely differ in quest scripting, spawn positions, script-spawned NPCs and conditions, so an individual step may not match your server and a quest can stall. A stuck step **self-benches** (it will not break the bot), but it takes a human to notice and, if needed, finish that quest by hand. **Watch your runs** — class/use-item/explore questing here is not safe to leave unattended. You can also turn class-quest forcing off entirely with the **"Class quests"** toggle in the overlay.

---

## How to use

FWAQ is not a standalone product — it **is** Wholesome Auto Quester with the changes above. Build the solution and drop the compiled product into your WRobot `Products` folder, exactly like the upstream project. See the upstream repo for the base setup.

- `Wholesome_Auto_Quester/` — the WRobot product (with the FWAQ additions)
- `Wholesome_Auto_Quester.Logic/` — WRobot-free, unit-tested decision logic
- `Wholesome_Auto_Quester.Tests/` — xUnit tests
- `Db_To_Json/` — the DB → `AQ.json` generation pipeline
- `tools/` — the dev-time enrichment tool
- `ROADMAP.md` / `ROADMAP-CLASS-QUESTS.md` — design notes

---

## Roadmap

1. **`conditions`-table enrichment** — the biggest reliability win: bake the race/faction/prerequisite gating (14k+ rules) into the data so the bot never chases a quest it can't actually take (kills phantom / cross-faction quests DB-wide).
2. **Ship the ~767 world use-item quests** — same pipeline, once a "the base quester already derives an objective for this" filter is in place to avoid redundant steps.
3. **Explore / area-trigger quests** — a whole category the base quester currently can't do; the `areatrigger` tables carry the exact coordinates.
4. **Script-spawned NPC locations** — give temporary turn-in/giver NPCs (no DB spawn) a location from their spawn trigger, for rock-solid chain endings.

---

## Credits & license

- **Original project & all core code:** [ZerO / Wholesome-wRobot](https://github.com/Wholesome-wRobot) — thank you.
- **FWAQ additions:** Talamin.
- FWAQ is a derivative work of Wholesome Auto Quester and is shared with the same community-friendly spirit as the original. Please respect the upstream project's terms.
