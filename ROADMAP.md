# Wholesome Auto Quester — Rework Roadmap

> A phased, ship-as-you-go plan to fix the Auto-Quester's real weaknesses **without a rewrite**.
> Companion to [CODE-REVIEW.md](CODE-REVIEW.md) (the diagnosis). This file is the plan of attack and a
> living checklist. Code references are relative to this repo root.

---

## Guiding decision: refactor in place, NOT a rewrite

This is the single most important call, and it's where this project differs from AIO3.

AIO3 was allowed to be a ground-up rewrite because a fightclass is small (a rotation is a few hundred lines)
and the old code was structurally beyond repair. The Auto-Quester is the opposite: **~10,700 lines, mature
and field-hardened**, with years of invisible edge-case rescues baked in — anti-drown, spirit-healer res on
broken durability, resurrection-sickness wait, loading-screen lock, anti-snap-back hysteresis, interact-distance
self-correction, proactive path-clearing. A rewrite would throw exactly those away and become a regression
factory. **The bones are good** (two-brain architecture, scanner registry, task abstraction).

→ **Every phase below is an in-place refactor on the existing bones, individually shippable and in-game
verifiable** — the same incremental, test-and-verify cadence that made AIO3 work.

---

## Symptom → root cause (what you actually feel while testing)

| Observed behaviour | Confirmed cause in code |
|---|---|
| **Stutter / hitches while travelling** | [WAQCheckPathAhead.cs](Wholesome_Auto_Quester/States/WAQCheckPathAhead.cs) runs tracelines **+ pathfinds per (point × mob) every 10 ms tick** the whole time you travel; `_losCache` is a LINQ-scanned `List` with 3-yard fuzz → constantly invalidated by moving units. Plus `GetListObjManagerHostiles()` is a full LINQ sweep computed **3× per tick** ([ToolBox.cs](Wholesome_Auto_Quester/Helpers/ToolBox.cs)). |
| **Bot "freezes" / stops finding objects** | The 500 ms scanner `Pulse` throws exceptions as control flow (`GetTaskMatchingWithObject`, `RemoveFromScannerRegistry`) **with no try/catch** ([WowObjectScanner.cs](Wholesome_Auto_Quester/Bot/TaskManagement/WowObjectScanner.cs)). One bad registry entry kills the thread → `ActiveWoWObject` freezes forever. |
| **Quest accepted but never worked on** | `RecordObjectiveIndices` matches a synthesized name against the **localized** quest-log text via `StartsWith`, retries 5× with blocking `Thread.Sleep(1000)`, then **silently generates no tasks** ([WAQQuest.cs](Wholesome_Auto_Quester/Bot/QuestManagement/WAQQuest.cs)). |
| **Quests "disappear" / whole areas avoided for hours** | Blacklisting is the *only* recovery: stuck → `AddNPC`+`AddZone`+15-min timeout ([WAQBot.cs](Wholesome_Auto_Quester/Bot/WAQBot.cs)); "unreachable" → **3-hour** timeout ([WowObjectScanner.cs](Wholesome_Auto_Quester/Bot/TaskManagement/WowObjectScanner.cs)). A transient obstacle parks a quest for 3 h. |
| **Only one quest taken per town (no hub behaviour)** | Greedy-nearest scoring + a spatial-cluster bonus that is deliberately **stronger for kill-packs (`SpatialWeight 1.0`) than for quest givers (`SpatialWeight 0.25`)** → after one pickup, a dense field objective outscores the remaining nearby givers and travel-hysteresis locks the bot onto leaving. See **Phase 7**. |
| **Visible pauses / "standstill"** | `Thread.Sleep` on the engine thread (Interact ~1.4 s, AntiDrown tens of seconds) blocks the whole FSM — no higher-priority state can preempt. |

All seven CODE-REVIEW findings were re-verified against current code; they hold.

---

## The dev loop (why testing has been painful, and how this fixes it)

WRobot loads a product DLL via `Assembly.LoadFrom`; a loaded .NET assembly **locks the file**, so the whole
app must be restarted to swap it. That's a platform constraint we can't toggle off from the product side —
**but the testing pain is mostly the absence of an offline test seam.** The rework routes around the restart:

- **Phase 2** moves all *decision* logic (status ladder, scoring, hub harvest, DAG, objective mapping) behind a
  read-model seam with a `FakeQuestSnapshot`, so the bulk of iteration becomes `dotnet test` in seconds — no
  WRobot, no restart (the AIO3 dev loop: 500+ offline tests).
- **Replay harness** (cross-cutting): capture a live game snapshot to JSON, replay it through the planner offline.
- **Live tunability**: prefer the existing settings GUI / overlay for behaviour tweaks over rebuilds.
- Only the genuine integration parts (movement, pathfinding, pickup/turn-in) still need the game. `wrobot-api-scout`
  to confirm whether this WRobot build supports product reload (stop → re-select) without a full app restart.

---

## Phases (ordered by risk/value; each shippable + in-game verifiable)

### Phase 0 — Safety net + observability  *(small, immediate, highest protection)*  ☐
Stop dying silently; make everything measurable before changing it.
- Wrap `Pulse()` in **both** the scanner and TaskManager in try/catch; turn the three `throw`-as-control-flow
  sites into **guard + return + log-warn**.
- Add type/validity guards before `WoWObject`→`WoWUnit` casts in the Kill/Loot states.
- Promote the `AllowStopWatch` instrumentation into a **structured perf log** (per state + per background loop,
  p95/max) — this becomes our "debug.log" for proving the next phases.
- **Risk:** minimal. **Value:** kills the two worst "bot does nothing" classes.

### Phase 1 — Get heavy work off the 10 ms thread  *(biggest felt smoothness win)*  ☐
Discipline straight from AIO3: **keep the hot thread cheap; heavy queries run in the 500 ms loop; states consume
the cached result.**
- Compute **one** hostile/lootable list per tick (or carry it in the 500 ms scanner) and hand it to
  Defend / CheckPathAhead / (Blacklist) — instead of 3 independent sweeps.
- Move the danger-path analysis (`EnemyAlongTheLine`, tracelines+pathfinds) into the 500 ms loop;
  `WAQCheckPathAhead.NeedToRun` only checks a cached `UnitOnPath` result.
- Replace `_losCache` (LINQ `List`) with a **quantized dictionary** so the cache hits instead of invalidating.
- Replace engine-thread `Thread.Sleep` with **timer-gated re-entry**.
- **Verify:** Phase-0 perf log before/after.

### Phase 2 — Scoped test seam  *(the structural lever)*  ☐
**Not** "everything behind IGameClient" — only the **decision logic**. The Quester is fundamentally integration;
movement/pathfinding/combat are NOT unit-testable and stay outside the seam.
- Define `IQuestSnapshot` (read-model: my position, level, faction/rep, per-quest log status, known objects,
  blacklist, bag contents) + a thin `IGameCommands` (move/interact/blacklist/…).
- Move the pure deciders behind it: quest status ladder, `CalculatePriority`/task scoring, objective validity,
  later the DAG + hub harvest. These stop calling static WRobot APIs directly.
- Add `FakeQuestSnapshot` → **xUnit offline** (mirrors AIO3's `FakeGameClient`). Everything after this becomes
  "refactor with tests", not "deploy and pray."
- **Risk:** medium but mechanical. **Value:** unlocks Phases 3–7 safely.

### Phase 3 — Slim the model: `QuestCompiler` + data files  ☐
[ModelQuestTemplate.cs](Wholesome_Auto_Quester/Database/Models/ModelQuestTemplate.cs) is ~60 lines of data + a
~480-line compiler-constructor with 4–6× copy-paste (`RequiredItem1..6`, `RequiredNpcOrGo1..4`), embedded game
rules (hardcoded mob/quest IDs), **and** reputation API calls inside a "data" class.
- Extract a `QuestCompiler` (JSON→domain); **loop over arrays** instead of copy-paste.
- Move the ~140-line hardcoded quest blacklist + special cases (e.g. [QuestManager.cs `InitializeWAQSettings`](Wholesome_Auto_Quester/Bot/QuestManagement/QuestManager.cs), `QuestModifiedLevel` in ToolBox, the hardcoded id in WAQQuest) into **data files** (JSON infra already exists). Reputation logic out of the model.
- Now unit-testable thanks to Phase 2.

### Phase 4 — Objective completion via index/event model  *(correctness landmine)*  ☐
The *completion* check already uses the index-based `GetQuestLogLeaderBoard` `finished` flag; only the **mapping**
of our objective to its index is brittle.
- Replace `RecordObjectiveIndices`'s localized `StartsWith` with **position/order mapping** (DB objective order ↔
  leaderboard order). No language match, no `Thread.Sleep` retry, no silent failure.
- Optionally add `QUEST_WATCH_UPDATE`/credit events as push instead of polling.
- Fixes non-EN clients and wording mismatches.

### Phase 5 — Graduated recovery instead of blacklist-nuke  ☐
- Before `AddZone`/`AddNPC`+timeout, first try **re-route / alternate spawn node / a brief unstuck maneuver**.
  Only then blacklist, with a **shorter, escalating** timeout (not a flat 3 h).
- The commented-out `WAQBlacklistDanger` was a churn bomb; the graduated variant replaces it sensibly.

### Phase 6 — Real quest-chain DAG  *(throughput multiplier + foundation for hubs)*  ☐
`Previous/NextQuestsIds` / `ExclusiveGroup` exist but are used only as an ad-hoc "OR any one completed"
(`IsQuestPickable` even comments the doubt). Build a proper prerequisite graph →
- correct chain ordering, fewer underleveled-abandon cycles,
- **hub look-ahead** (know the whole chain that unlocks in a hub) — prerequisite for Phase 7.

### Phase 7 — Hub harvesting  *(the "grab the whole town" behaviour you asked about)*  ☐
**Today there is no hub model.** The scorer is greedy-nearest with a cluster bonus that *punishes* quest givers
(`SpatialWeight 0.25`) relative to kill-packs (`SpatialWeight 1.0`), so after one pickup the bot bolts to a dense
field objective and the travel-hysteresis keeps it there. Note: some "many quests" in a town are *correctly*
skipped (chain-locked via prerequisites, or level/faction filtered) — the goal is **all currently-available
quests + the chain that unlocks in this hub**, not "grab everything."
- **Hub detection + collect pass:** when ≥2 pickable givers cluster within X yards, collectively raise their
  pickups' priority so the bot **empties the hub before leaving**. This also corrects the SpatialWeight asymmetry.
- **DAG look-ahead (needs Phase 6):** turn in a prerequisite → immediately take the follow-up offered at the same
  or a nearby NPC, in-hub.
- **Symmetric batch turn-in** on return.

### Cross-cutting (fold in opportunistically)
- **Compiled-quest cache:** the 25 MB JSON → model compile is redone on every `PLAYER_ENTERING_WORLD` + level-up
  → persist the compiled result.
- **`HashSet` instead of `List.Contains`** for `ListCompletedQuests` (grows to thousands of ids, probed in hot loops).
- **Incremental task pile** instead of `_taskPile.Clear()` + KD-tree rebuild every 500 ms (tasks only change on
  status events; only re-score on movement).
- **Extract a MoveHelper** for the "if not already going there, FindPath + Go" block copy-pasted across ~6 states.
- **Replay harness** for offline planner iteration (see "The dev loop").

---

## Recommended order

**0 → 1 → 2 → (3, 4, 5 in any order) → 6 → 7**, cross-cutting opportunistically.

Phases 0+1 are small, low-risk, and hit the *felt* downsides (freezing, stutter) for an immediate visible win.
Phase 2 is the investment that makes 3–7 safe. Phases 4 (correctness) and 5 (quest parking) carry the highest
"bot finally does what it should" value; 6+7 carry the highest throughput value. Phase 7 (hubs) depends on 6.

---

## Agents for the rework

- **`wrobot-api-scout` — reuse immediately.** Reflects the installed WRobot assemblies; project-agnostic (same
  WRobot API as AIO3). Pull it in for any uncertain `MovementManager` / `PathFinder` / `wManagerSetting` / quest-log
  Lua signature, and to confirm product-reload behaviour. Never guess an API.
- **Do NOT point the AIO3 agents at the Quester** (`aio3-architecture-guard`, `aio3-test-author`,
  `aio3-rotation-author`, `aio3-content-porter`) — they assume AIO3's layering, DSL, and `FakeGameClient`.
- **Two Quester-specific agents — worth creating, but only once Phase 2 lands** (they pay off with the seam + fake):
  - **`waq-architecture-guard`** — checks edits don't leak wManager types above the seam, don't reintroduce
    throws-as-control-flow on background threads, don't push heavy work onto the 10 ms thread.
  - **`waq-test-author`** — writes xUnit tests against `FakeQuestSnapshot` (status ladder, scoring, hub harvest, DAG).
- **Generic agents** (`Explore`, `Plan`, `general-purpose`) for parallel research — e.g. validating the ~140
  hardcoded blacklist entries against the quest DB JSON in Phase 3/4.

---

*Net: keep the strong bones (two-tier architecture, scanner registry, task abstraction). Fix, in order, the
silent-death paths, the engine-thread heavy work, the missing test seam, the overloaded model, the brittle
objective mapping, blacklist-only recovery, and finally add real chain + hub intelligence — each on top of the
existing bones, each verifiable in-game.*
