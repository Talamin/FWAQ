# Wholesome Auto Quester — Code Review & Improvement Notes

> An honest architecture/perf/robustness review of the Auto-Quester, written for later reference.
> Scope read: `Main.cs`, `Bot/WAQBot.cs` (the FSM wiring), `Bot/TaskManagement/{TaskManager,WowObjectScanner}.cs`
> (the decision loop), plus a deep read of the quest data model (`Bot/QuestManagement/*`, `Database/*`) and the
> FSM states (`States/*`, `Bot/TravelManagement`, `Helpers/ToolBox.cs`).

## Bottom line

A **mature, field-hardened product with good bones** — but it carries the **same structural debt the old AIO had**
(the thing AIO3 was built to fix), plus a few concrete perf/robustness issues that cost **throughput**. None of it is
a dead end; it's all refactorable on the existing bones.

## What's genuinely good (fair credit)

- **Two-brain architecture:** the scanner + task manager run on their own 500 ms threads (the "slow brain"); the FSM
  just consumes `ActiveTask` / `ActiveWoWObject`. That's the right shape — heavy planning is *meant* to be off the
  engine thread.
- **Scanner registry** (`Dictionary<int entry, List<IWAQTask>>`) — an elegant O(1) way to match live WoW objects to
  pending work.
- **Task abstraction** (`WAQBaseTask` / `WAQBaseScannableTask`): adding a new objective type is local and low-risk.
- **Event-driven status updates** (hooks `QUEST_LOG_UPDATE`, `BAG_UPDATE`, `PLAYER_LEVEL_UP`, …) instead of polling
  everything — the status machine reacts to the real game log.
- **Clean DI at the manager layer** (`ITaskManager`, `IWowObjectScanner`, `IContinentManager`, `ITravelManager`,
  `IJSONManager`), constructor-injected.
- **Very mature edge cases:** anti-drown, spirit-healer res on broken durability, resurrection-sickness wait,
  loading-screen lock, anti-snap-back hysteresis, choosing the closest object by **real path distance** (not
  straight-line), the **proactive path-danger clearing** (pull a mob before you run into it), and interact-distance
  self-correction (listens for the "too far away" UI error and shrinks the per-entry distance).
- **Dev observability:** Radar3D draws the danger tracelines / path points / active task, and a per-state `>200 ms`
  stopwatch warning — the author built the instrument to *see* the perf problems.

## The real issues (ordered by leverage)

### 1. No test seam / WRobot coupling everywhere — the #1 structural debt
Every layer (even the "data" models) calls static WRobot/Lua APIs directly (`ObjectManager`, `MovementManager`,
`Fight`, `PathFinder`, `Interact`, `Lua`, `wManagerSetting`); the data models even call `WoWFactionTemplate` /
`DBCFaction` reputation APIs. → **Nothing is testable offline**, so every change is "deploy and pray." This is the
exact problem AIO3 solved with `IGameClient`. The DI is already there — it just stops at the managers.
**Introduce a thin game-state seam** and task-priority / quest-status / validity become unit-testable. This is the
lever that de-risks everything else.

### 2. Expensive work leaking onto the 10 ms FSM thread — the #1 perf/stutter loss
- `WAQCheckPathAhead` runs **tracelines + pathfinds per (point, unit) pair, every tick, the whole time you're
  traveling** (`_losCache` is a fuzzy `List` scanned with LINQ; keys are 3-yard distance fuzz, so moving units/player
  constantly invalidate it).
- `ToolBox.GetListObjManagerHostiles()` is a full LINQ sweep over all units, computed **3× independently per tick**
  (CheckPathAhead, Defend, BlacklistDanger).
- `ToolBox.HostilesAreAround` does **N pathfinds per call** on the engine thread.
- `Thread.Sleep` in `Run()` **freezes the whole FSM** (Interact ~1.4 s, AntiDrown tens of seconds in a sleep-loop) →
  no higher-priority state can preempt.
- **Fix:** move the heavy analysis (danger scan, hostile scan) into the existing 500 ms loop; states only consume the
  **cached result**. Replace `Thread.Sleep` with timer-gated re-entry. (Exactly the "keep the hot thread cheap"
  discipline from AIO3 — frame-lock only around the snapshot, slow queries unlocked.)

### 3. The model layer does too much
`ModelQuestTemplate` is ~60 lines of data + a **480-line compiler-constructor** with 4–6× copy-pasted
`RequiredItem1..6` / `RequiredNpcOrGo1..4` blocks, embedded game rules (hardcoded mob/quest IDs), view formatting,
**and** reputation API calls inside a "data" class. `ModelItemTemplate` is ~90 lines of dead commented-out fields.
→ extract a `QuestCompiler`, loop over arrays instead of copy-paste, and move the **140-line hardcoded quest
blacklist** + special cases into data files (the JSON infra already exists).

### 4. Objective completion via localized Lua string-matching = a correctness landmine
`RecordObjectiveIndices` matches a synthesized `ObjectiveName` against the **localized** quest-log leaderboard text,
retries **5× with blocking `Thread.Sleep(1000)`**, then **silently generates no tasks** on failure. Breaks on non-EN
clients and on any wording mismatch (e.g. "X slain" vs the log's phrasing). → move to an **index / credit-event
model** (`QUEST_WATCH_UPDATE`, or map objectives to indices by position rather than text).

### 5. Exceptions as control flow on background threads
`AddTaskToPile` / `AddTaskToDictionary` / `GetTaskMatchingWithObject` / `RemoveFromScannerRegistry` **throw** on
"shouldn't happen" states — **inside the 500 ms scanner `Pulse` with no try/catch**. One bad registry entry kills the
scanner thread → `ActiveWoWObject` freezes → **the bot just stops finding objects**. → guard + return instead of
throw, and wrap `Pulse` in try/catch. (Also add type/validity guards before `WoWObject`→`WoWUnit` casts in the
Kill/Loot states.)

### 6. Blacklisting as the only recovery
Unreachable / stuck / surrounded / dangerous-zone all → `AddZone` / `AddNPC` + a 15-minute timeout → **churn + dead
quests** (a transient obstacle permanently parks a quest). No re-route / unstuck maneuver before the nuclear option.
(`WAQBlacklistDanger` is commented out of the FSM for a reason — it was a churn bomb.) → add **graduated recovery**
(re-route / alternate node / brief unstuck) before blacklisting.

### 7. O(n) membership + per-tick rebuilds
`ListCompletedQuests` is a `List.Contains` (grows to thousands of IDs, probed in hot loops — status ladder, condition
checks, `IsQuestPickable`) → use a **HashSet**. The task pile + KD-tree are **rebuilt from scratch every 500 ms** →
could be incremental, since tasks only change on status events; re-score on movement.

## Untapped potential (where to get more)

- **A real quest-chain DAG.** `Previous/NextQuestsIds` / `ExclusiveGroup` exist but are used only as an ad-hoc "OR
  any one completed" (the code even comments the doubt about it). A proper prerequisite graph → **correct chain
  ordering, hub look-ahead** (grab the whole chain in a hub), fewer underleveled-abandon cycles = **directly more
  quests/hour**.
- **Compiled-quest cache.** The 25 MB JSON → `ModelQuestTemplate` compile (FK hydration + objective generation) is
  redone on `PLAYER_ENTERING_WORLD` and every level-up → persist/cache the compiled result.
- **Vehicle / escort / "use item on target"** handling is thin (only a blind `WAQExitVehicle`) — common WotLK quest
  patterns currently left to luck / the CustomClass.
- **Share the per-tick scans.** Compute the hostile list / lootable list **once per tick** (or piggyback the 500 ms
  scanner) and hand them to Defend/CheckPathAhead/BlacklistDanger instead of 3 independent sweeps.
- **Extract a movement helper.** The "if not already going there, FindPath + Go" block is copy-pasted across ~6
  states.

## Recommended order of attack

1. **Robustness quick wins first** (small, high impact): wrap the scanner `Pulse` in try/catch + turn throws into
   guards (#5); `HashSet` instead of `List.Contains` (#7); `Thread.Sleep` → timer-gated re-entry in the hot states
   (part of #2).
2. **Get the heavy work off the FSM thread** (#2) — the biggest, most noticeable smoothness/perf win.
3. **Introduce the game-state seam** (#1) — the structural lever; then you can rebuild the rest **with tests** safely.
4. Then **model → QuestCompiler + data files** (#3), the **objective-completion event model** (#4), **graduated
   recovery** (#6), and the **quest-chain DAG**.

---

*Net: the shape of the system is good — the two-tier architecture, the scanner registry, and the task abstraction are
the strong bones. The weaknesses are concentrated in (a) the model layer doing far too much, (b) the brittle Lua
string-matching objective model with blocking retries, (c) exceptions-as-control-flow on background threads with zero
test seam, and (d) O(n) list membership + full per-tick rebuilds and heavy work on the engine thread. All refactorable
on top of the existing bones.*
