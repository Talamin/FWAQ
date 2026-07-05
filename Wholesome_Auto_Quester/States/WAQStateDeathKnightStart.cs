using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Wholesome_Auto_Quester.Bot.ScriptedProfile;
using Wholesome_Auto_Quester.Helpers;
using wManager;
using wManager.Events;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    /// <summary>
    /// Runs the hand-authored ORDERED Death Knight start profile (Ebon Hold, map 609) - the linear, gossip/vehicle
    /// scripted chain the DB-driven quester can't derive on its own. Active ONLY for a Death Knight standing in the
    /// Ebon Hold map; everywhere else it returns false and normal WAQ runs untouched. It walks the profile in order,
    /// finds the first UNsatisfied step, and executes it, tracking progress purely from the quest log + bag (so it
    /// survives restarts). Actions grow one verified quest at a time: pickup / turnin / get-item-from-go / use-item.
    /// Sits BELOW combat / loot / regen / town in the FSM (they still interrupt it) but ABOVE the normal quest
    /// routing, so inside Ebon Hold the profile owns the questing.
    /// </summary>
    internal class WAQStateDeathKnightStart : State, IWAQState
    {
        private const int EbonHoldMapId = 609; // Plaguelands: The Scarlet Enclave

        // --- "Death Comes From On High" (12641) vehicle constants. Values from the tested EasyQuest reference
        // (Questfiles/DK/Part 1, q12641) cross-checked against the world DB. ---
        private const uint EyeControlBuff = 51852;  // "Eye of Acherus" control aura (gained from the control mechanism)
        private const uint EyeSiphonSpell = 51859;  // the eye's siphon/analyze cast used on each objective
        private const uint EyeReturnSpell = 52694;  // returns/destroys the eye -> drops the control aura
        private const int EyeControlGo = 191609;    // GO "Eye of Acherus Control Mechanism" (DB: tied to 12641)
        private const float EyeCruiseClearance = 25f; // metres to stay ABOVE objective altitude in transit (mobs on the
                                                      // ground shoot the eye down on a direct low approach - Talamin).
        private static readonly Vector3 EyeControlGoPos = new Vector3(2321.645f, -5661.928f, 426.0302f);
        // The 4 analyze objectives of 12641, in DB objective order (RequiredNpcOrGo1-4):
        //  1 New Avalon Forge (28525) | 2 Town Hall (28543) | 3 Scarlet Hold (28542) | 4 Chapel (28544).
        // Fly-points are the tested reference coords, one per objective index.
        private static readonly Vector3[] EyeObjectivePoints =
        {
            new Vector3(1811.8f,   -5984.5f,   144.1f),
            new Vector3(1594.373f, -5736.991f, 141.2463f),
            new Vector3(1654.128f, -5988.128f, 145.1512f),
            new Vector3(1388.325f, -5703.44f,  149.0317f),
        };

        // "ride-transporter": within this many yards of a step's TargetZ = we've arrived on that level. Must be well
        // below the level separation (~44y in Ebon Hold) so it can't confuse origin with destination, and above the
        // floor's own Z wobble. Works both directions (down to the Hall of Command, or a future ride back up).
        private const float TransporterArrivedBand = 15f;

        // --- "Grand Theft Palomino" (12680) vehicle constants (tested reference Part 2 q12680 + world DB). ---
        private const uint StolenHorseBuff = 52263;     // aura while mounted on the stolen Havenshire Stallion
        private const int HavenshireStallion = 28605;   // the horse to interact/steal
        private const int SalanarEntry = 28653;         // Salanar the Horseman - deliver the horse to him
        private static readonly Vector3 StallionStealSpot = new Vector3(2202.142f, -5800.674f, 101.3499f);
        private static readonly Vector3 HorseDeliverSpot = new Vector3(2328.635f, -5675.169f, 153.9203f);

        // "grind-kill": how far to look for objective mobs before roaming to the next hotspot.
        private const float GrindSearchRange = 60f;
        private int _grindHotspotIndex;

        // While riding a transporter we pause WRobot's teleport-detection (the sudden level change would otherwise STOP
        // the bot - Talamin). Saved here so we can restore it once we're across.
        private bool _teleportGuardActive;
        private bool _savedCloseIfPlayerTeleported;
        private bool _noCombatHooked; // true while our FightEvents cancel-hooks are subscribed (NoCombat step active)

        public override string DisplayName { get; set; } = "WAQ DK Start Profile";

        private static bool IsQuestDone(int questId) =>
            Quest.FinishedQuestSet.Contains(questId) || ToolBox.IsQuestCompleted(questId);

        /// <summary>Quest is in the log with all objectives met (ready to hand in) - the signal that a scripted action
        /// step (e.g. runeforge) has done its job even though the quest has no DB-derivable objective.</summary>
        private static bool IsQuestCompleteInLog(int questId) =>
            Quest.GetLogQuestId().Any(q => q.ID == questId && q.State == Quest.PlayerQuest.StateFlag.Complete);

        private static bool HasItem(int itemId) =>
            itemId > 0 && ItemsManager.GetItemCountById((uint)itemId) > 0;

        /// <summary>
        /// Is this step already satisfied? Conditions are designed to be MONOTONIC along the sequence (they never
        /// regress as we progress), so "first unsatisfied step" always advances correctly:
        /// - pickup:           have the quest, or it's done.
        /// - get-item-from-go: hold the item, OR its transformed result, OR the quest is done.
        /// - use-item:         the result item is in the bag (the use produced it), OR the quest is done.
        /// - turnin:           the quest is done.
        /// </summary>
        private static bool IsStepSatisfied(ScriptedProfileStep s)
        {
            switch (s.Action)
            {
                case "pickup":
                    return Quest.HasQuest(s.QuestId) || IsQuestDone(s.QuestId);
                case "get-item-from-go":
                    return HasItem(s.ItemId) || HasItem(s.ResultItemId) || IsQuestDone(s.QuestId);
                case "use-item":
                    return (s.ResultItemId > 0 ? HasItem(s.ResultItemId) : !HasItem(s.ItemId)) || IsQuestDone(s.QuestId);
                case "runeforge":
                case "interact-go":
                case "steal-horse":
                case "duel":
                case "into-realm":
                case "cannon":
                case "persuade":
                case "ambush":
                case "escort-battle":
                    return IsQuestCompleteInLog(s.QuestId) || IsQuestDone(s.QuestId);
                case "raise-ghouls":
                    return IsQuestCompleteInLog(s.QuestId) || ScarletGhoulCount() >= 5 || IsQuestDone(s.QuestId);
                case "patrol":
                    // may serve several quests at once (kill + gather in one area) - done when ALL are complete.
                    return PatrolQuestIds(s).All(q => IsQuestCompleteInLog(q) || IsQuestDone(q));
                case "ride-transporter":
                case "take-taxi":
                    // done once we've reached the destination level (direction-agnostic: TargetZ is the FAR level, well
                    // beyond the arrival band from where we boarded the transporter / gryphon).
                    return System.Math.Abs(ObjectManager.Me.Position.Z - s.TargetZ) < TransporterArrivedBand
                           || IsQuestDone(s.QuestId);
                case "portal":
                    // interacting the portal teleports us UP to Acherus (12757). Done ONLY once we're above TargetZ (the
                    // Acherus level) - NOT complete-in-log: 12757 goes complete-in-log the instant it's accepted, which
                    // would skip the portal and send us running to the Acherus turn-in on foot (Talamin). IsQuestDone
                    // covers the case the turn-in already happened.
                    return ObjectManager.Me.Position.Z > s.TargetZ || IsQuestDone(s.QuestId);
                case "death-gate":
                    // done once the Death Gate has warped us up into Acherus (13165).
                    return Usefuls.SubMapZoneName == "Acherus: The Ebon Hold"
                           || ObjectManager.Me.Position.Z > 300f || IsQuestDone(s.QuestId);
                case "acherus-battle":
                    // done once both objectives are met AND we've ridden back down to the lower level (13166).
                    return (IsQuestCompleteInLog(s.QuestId) && ObjectManager.Me.Position.DistanceZ(AcherusBottomTele) < 30f)
                           || IsQuestDone(s.QuestId);
                case "eye-of-acherus":
                    // done only once all objectives are met AND we've left the eye (so we can walk to the turn-in).
                    return (IsQuestCompleteInLog(s.QuestId) && !ObjectManager.Me.HaveBuff(EyeControlBuff))
                           || IsQuestDone(s.QuestId);
                case "frost-wyrm":
                    // done once both objectives (150 kills + 10 ballistae) are met AND we've ejected the wyrm.
                    return (IsQuestCompleteInLog(s.QuestId) && !ObjectManager.Me.PlayerUsingVehicle)
                           || IsQuestDone(s.QuestId);
                case "turnin":
                case "turnin-go":
                    // turnin-go turns the quest in AT a gameobject (e.g. 12717's Plague Cauldron), so - unlike
                    // interact-go which only completes an objective - it must run until the quest is actually handed
                    // in, NOT be satisfied by complete-in-log (12717 goes complete the instant it's accepted).
                    return IsQuestDone(s.QuestId);
                case "set-ground-mount":
                    // one-shot: once we've applied the ground mount, stay satisfied FOREVER. Do NOT re-check the live
                    // settings - a vehicle quest (the Frost Wyrm's UseMount=false) resets UseGroundMount, which used to
                    // flip this early step back to "current", making VehicleWanted false for a pulse so WAQExitVehicle
                    // ejected the wyrm mid-flight (Talamin). The flag decouples it from those volatile settings.
                    return _groundMountSet;
                case "train":
                    // train at THIS point (by TrainId) while we're up top near the DK trainer; if we've already trained
                    // here, or aren't up top anymore (Z<350), or aren't even in the 609 instance where the trainer lives
                    // (e.g. the relocated Acherus on map 0 - training is long done by then), don't block the chain.
                    return _trainedIds.Contains(s.TrainId) || ObjectManager.Me.Position.Z < 350f
                           || Usefuls.ContinentId != EbonHoldMapId;
                case "special-surprise":
                    // race-gated quest: the id depends on the player's race, so resolve it at runtime.
                    return IsQuestDone(SpecialSurpriseQuestId());
                case "faction-finale":
                    // faction-gated: 13189 (Horde) / 13188 (Alliance) - resolve at runtime.
                    return IsQuestDone(IsHorde() ? 13189 : 13188);
                case "todo":
                    // a not-yet-built CUSTOM quest: only "satisfied" once the quest is somehow done, so the executor
                    // parks here (visible "next to build" marker) instead of looping a turn-in it can't complete.
                    return IsQuestDone(s.QuestId);
                default:
                    return true; // unknown action: don't let it block the chain
            }
        }

        /// <summary>Quests a "patrol" step completes together (its QuestIds list, or just its QuestId).</summary>
        private static System.Collections.Generic.List<int> PatrolQuestIds(ScriptedProfileStep s) =>
            (s.QuestIds != null && s.QuestIds.Count > 0) ? s.QuestIds : new System.Collections.Generic.List<int> { s.QuestId };

        private static ScriptedProfileStep CurrentStep() =>
            ScriptedProfileData.DeathKnightStart.FirstOrDefault(s => !IsStepSatisfied(s));

        // The current step, cached by NeedToRun each pulse so the combat / vehicle guards below are cheap. Those guards
        // run at HIGHER FSM priority than this state, so they read the previous pulse's value - fine, steps change slowly
        // (and while a step keeps combat off, this state IS reached every pulse, so the cache stays fresh).
        private static ScriptedProfileStep _cachedCurrentStep;

        /// <summary>True while the current DK-profile step opted out of combat (e.g. the stolen-horse ride). The combat
        /// states (WAQDefend / WAQStateKill) read this and stand down.</summary>
        public static bool CombatSuppressed => _cachedCurrentStep != null && _cachedCurrentStep.NoCombat;

        /// <summary>True while the current DK-profile step owns a vehicle we must NOT auto-exit (the stolen horse, or the
        /// Eye of Acherus). WAQExitVehicle reads this and leaves the vehicle alone.</summary>
        public static bool VehicleWanted =>
            _cachedCurrentStep != null
            && (_cachedCurrentStep.Action == "steal-horse" || _cachedCurrentStep.Action == "eye-of-acherus"
                || _cachedCurrentStep.Action == "into-realm" || _cachedCurrentStep.Action == "cannon"
                || _cachedCurrentStep.Action == "frost-wyrm");

        /// <summary>After "The Light of Dawn" the Ebon Hold relocates from the DK-start instance (map 609) to hover over
        /// the Plaguelands on the OPEN world (map 0) - so the last quests (13165 turn-in, 13166 "Battle For The Ebon
        /// Hold", the faction finale, and their NPCs Darion 31084 / the battle mobs) live on continent 0, not 609. The
        /// floating necropolis keeps the same internal coords as the 609 version and sits high (Z>300) above the ground
        /// there, so we detect it by that box (plus the zone name). The profile must run here too, else the DB-quester
        /// takes over (badly) and, worse, Wholesome_Vendors' drive-by-sell loops on the dead battle NPCs.</summary>
        private static bool OnRelocatedAcherus()
        {
            if (Usefuls.ContinentId != 0)
                return false;
            Vector3 p = ObjectManager.Me.Position;
            if (p.Z > 300f && p.X > 2200f && p.X < 2600f && p.Y > -5800f && p.Y < -5400f)
                return true;
            string zone = (Usefuls.SubMapZoneName ?? "") + "|" + (Usefuls.MapZoneName ?? "");
            return zone.Contains("Acherus") || zone.Contains("Ebon Hold");
        }

        /// <summary>The faction-finale quest (13188 Alliance / 13189 Horde) is in our quest log (active OR complete but
        /// not yet turned in). Uses the LOG list, not Quest.HasQuest - it's a "report" quest that goes complete the
        /// instant it's accepted, and HasQuest returns false for a complete quest. Keeps the profile alive in the
        /// faction capital (off map 609/0) so it can hand in to Thrall / King Varian.</summary>
        private static bool DkFinaleInLog() =>
            Quest.GetLogQuestId().Any(q => q.ID == 13188 || q.ID == 13189);

        public override bool NeedToRun
        {
            get
            {
                _cachedCurrentStep = null; // guards see "no active DK step" unless we set it below
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid
                    || ObjectManager.Me.WowClass != WoWClass.DeathKnight
                    || (Usefuls.ContinentId != EbonHoldMapId && !OnRelocatedAcherus() && !DkFinaleInLog()))
                    return false;

                EnsureDkVendorsRegistered(); // one-time: teach ToTown / Wholesome_Vendors the DK-start vendors (map 609
                                             // has none in WRobot's NpcDB, so town-runs never had a sell/repair target)
                EnsureDkOffMeshRegistered();  // one-time: teach the pathfinder the Lich-King ramp (navmesh can't climb it)
                EnsureVendorPluginOff();      // one-time: kill the Wholesome_Vendors plugin for the whole DK start (its
                                              // drive-by-sell FSM state loops forever at the dead Acherus battle NPCs)

                ScriptedProfileStep step = CurrentStep();
                _cachedCurrentStep = step;
                if (step == null)
                    return false;

                DisplayName = $"DK Profile: {step.Action} '{step.QuestName}'";
                return true;
            }
        }

        // --- DK-start vendors -------------------------------------------------------------------------------------
        // Map 609 ("DeathKnightStart") has NO vendor/repair NPC in WRobot's NpcDB, and WAQ (a custom product) never
        // loads an EasyQuest profile that would register one - so the built-in ToTown state and the Wholesome_Vendors
        // plugin had no sell/repair target and the bags filled up. Register the two in-zone vendors from the tested
        // reference profiles (Part 2/3) the first time a DK is in the zone.
        private static bool _dkVendorsRegistered;

        private static void EnsureDkVendorsRegistered()
        {
            if (_dkVendorsRegistered)
                return;
            _dkVendorsRegistered = true;
            RegisterDkVendor(28760, "Hargus the Gimp", 2342.9f, -5718.7f, 153.922f); // Death's Breach (Part 2)
            RegisterDkVendor(28943, "Fineous", 1872.76f, -5773.98f, 87.679f);        // New Avalon (Part 3)
        }

        private static void RegisterDkVendor(int entry, string name, float x, float y, float z)
        {
            // Npc.Type is a single enum, so "repairs AND sells" needs two entries. NpcDB.AddNpc dedups by entry +
            // position (<~1yd), so offset the vendor entry a couple of yards to keep both (the real NPC is well within
            // interaction range of either spot). save:false = in-memory only (re-registered each run, no NpcDB.xml
            // pollution); currentProfileNpc:true = selectable even if the AcceptOnlyProfileNpc setting is on.
            var repair = new wManager.Wow.Class.Npc
            {
                Entry = entry, Name = name, Position = new Vector3(x, y, z),
                GossipOption = -1, Active = true,
                Faction = wManager.Wow.Class.Npc.FactionType.Neutral,
                Type = wManager.Wow.Class.Npc.NpcType.Repair,
                VendorItemClass = wManager.Wow.Class.Npc.NpcVendorItemClass.Food,
                ContinentId = wManager.Wow.Enums.ContinentId.DeathKnightStart,
            };
            var vendor = new wManager.Wow.Class.Npc
            {
                Entry = entry, Name = name, Position = new Vector3(x + 2.5f, y, z),
                GossipOption = -1, Active = true,
                Faction = wManager.Wow.Class.Npc.FactionType.Neutral,
                Type = wManager.Wow.Class.Npc.NpcType.Vendor,
                VendorItemClass = wManager.Wow.Class.Npc.NpcVendorItemClass.Food,
                ContinentId = wManager.Wow.Enums.ContinentId.DeathKnightStart,
            };
            NpcDB.AddNpc(repair, false, true);
            NpcDB.AddNpc(vendor, false, true);
            Logger.Log($"[DK Profile] Registered DK-start vendor '{name}' ({entry}) for town-runs (repair + sell).");
        }

        // --- Lich King ramp OffMeshConnection --------------------------------------------------------------------
        // The Lich King (29110) at Death's Breach sits at the top of a ramp WRobot's navmesh can't climb (Talamin: nav
        // problems reaching him for the 12778 turn-in). Register the ramp as a BIDIRECTIONAL OffMeshConnection so the
        // pathfinder can walk up to the turn-in AND back down again - the proper fix vs. a hand-authored moveto. The
        // waypoints come from the tested reference profile's <OffMeshConnections> entry (Part 3).
        private static bool _dkOffMeshRegistered;

        private static void EnsureDkOffMeshRegistered()
        {
            if (_dkOffMeshRegistered)
                return;
            _dkOffMeshRegistered = true;
            var ramp = new PathFinder.OffMeshConnection(
                new System.Collections.Generic.List<Vector3>
                {
                    new Vector3(2321.802f, -5732.131f, 153.9201f), // bottom (Death's Breach floor)
                    new Vector3(2317.175f, -5735.921f, 156.9915f), // mid-ramp
                    new Vector3(2311.269f, -5741.12f, 160.9504f),  // top (~1.5y from the Lich King)
                },
                609, // DeathKnightStart continent
                PathFinder.OffMeshConnectionType.Bidirectional,
                false) // only kick in when the navmesh itself can't find a path (i.e. the ramp)
            {
                Name = "DK Death's Breach - Lich King ramp",
            };
            PathFinder.OffMeshConnections.Add(ramp);
            Logger.Log("[DK Profile] Registered the Lich King ramp OffMeshConnection (Death's Breach)");
        }

        // --- kill the Wholesome_Vendors plugin for the whole DK start ---------------------------------------------
        // Wholesome_Vendors injects a "Drive-by sell" FSM STATE that sits ABOVE this profile in priority. During the
        // Ebon Hold battle the Acherus vendor NPCs (Alchemist Karloff, Dread Commander Thalanor) are DEAD, so that state
        // NeedToRun keeps returning true, its Run() can't open the (dead) vendor, and it loops forever - preempting this
        // profile so the bot never progresses (Talamin). Because it preempts Run(), a per-step DisablePlugins can't help
        // (ApplyNoPlugins only runs inside Run). So we disable the plugin from NeedToRun (which IS called every pulse):
        // flip its Actif flag off (in-memory only, no Save -> re-enabled next WRobot start) and reload, which drops its
        // state. Town-runs don't work in this instance anyway, and greys are handled by the inventory manager.
        private static void EnsureVendorPluginOff()
        {
            // NOT one-shot: WRobot re-reads its plugin config at product start (AFTER our first pass) and re-enables
            // Wholesome_Vendors, so a single disable at init gets undone (log showed "States added" seconds later). So
            // re-check every pulse and re-kill it whenever it comes back on. Once Actif stays false this is a cheap
            // no-op. We must win this race BEFORE its drive-by-sell state starts looping - once it loops it preempts
            // this profile and NeedToRun (hence this method) stops being reached.
            var vendors = wManagerSetting.CurrentSetting.PluginsSettings
                .FirstOrDefault(ps => ps.FileName != null && ps.FileName.Contains("Wholesome_Vendors"));
            if (vendors == null || !vendors.Actif)
                return; // already off (or not installed) -> nothing to do
            vendors.Actif = false;
            wManager.Plugin.PluginsManager.DisposeAllPlugins();
            wManager.Plugin.PluginsManager.LoadAllPlugins(); // reloads every Actif plugin EXCEPT Wholesome_Vendors
            Logger.Log("[DK Profile] Disabled the Wholesome_Vendors plugin (its drive-by-sell loops at the dead Acherus battle NPCs)");
        }

        public override void Run()
        {
            ScriptedProfileStep step = CurrentStep();
            if (step == null)
                return;

            ApplyNoCombat(step.NoCombat);
            ApplyNoPlugins(step.DisablePlugins);

            switch (step.Action)
            {
                case "persuade":
                    RunPersuade(step);
                    break;
                case "special-surprise":
                    RunSpecialSurprise(step);
                    break;
                case "faction-finale":
                    RunFactionFinale(step);
                    break;
                case "ambush":
                    RunAmbush(step);
                    break;
                case "portal":
                    RunPortal(step);
                    break;
                case "frost-wyrm":
                    RunFrostWyrm(step);
                    break;
                case "escort-battle":
                    RunEscortBattle(step);
                    break;
                case "death-gate":
                    RunDeathGate(step);
                    break;
                case "acherus-battle":
                    RunAcherusBattle(step);
                    break;
                case "get-item-from-go":
                    RunGetItemFromGo(step);
                    break;
                case "use-item":
                    RunUseItem(step);
                    break;
                case "runeforge":
                    RunRuneforge(step);
                    break;
                case "interact-go":
                    RunInteractGo(step);
                    break;
                case "turnin-go":
                    RunTurninGo(step);
                    break;
                case "eye-of-acherus":
                    RunEyeOfAcherus(step);
                    break;
                case "ride-transporter":
                    RunRideTransporter(step);
                    break;
                case "take-taxi":
                    RunTakeTaxi(step);
                    break;
                case "patrol":
                    RunPatrol(step);
                    break;
                case "steal-horse":
                    RunStealHorse(step);
                    break;
                case "duel":
                    RunDuel(step);
                    break;
                case "into-realm":
                    RunIntoRealm(step);
                    break;
                case "raise-ghouls":
                    RunRaiseGhouls(step);
                    break;
                case "cannon":
                    RunMassacre(step);
                    break;
                case "set-ground-mount":
                    RunSetGroundMount(step);
                    break;
                case "train":
                    RunTrain(step);
                    break;
                case "todo":
                    RunTodo(step);
                    break;
                default: // "pickup" / "turnin"
                    RunGossip(step);
                    break;
            }
        }

        // --- pickup / turn-in at a creature giver ---------------------------------------------------------------
        private void RunGossip(ScriptedProfileStep step)
        {
            RestoreTeleportGuard(); // we're walking to a giver/ender again -> the transporter ride is over

            // If we're still possessing a vehicle (e.g. the Eye of Acherus right after its scripted flight),
            // leave it first - otherwise we can't walk to the giver/ender or open their gossip.
            if (ObjectManager.Me.PlayerUsingVehicle)
            {
                Logger.Log("[DK Profile] Ejecting vehicle before gossip");
                Usefuls.EjectVehicle();
                Thread.Sleep(1500);
                return;
            }

            Vector3 myPos = ObjectManager.Me.Position;

            WoWUnit npc = ObjectManager.GetObjectWoW()
                .OfType<WoWUnit>()
                .Where(u => u.IsValid && u.Entry == step.Npc && u.IsAlive)
                .OrderBy(u => u.Position.DistanceTo(myPos))
                .FirstOrDefault();

            Vector3 dest = npc != null ? npc.Position : step.GetPosition;

            if (dest.DistanceTo(myPos) > 4.5f)
            {
                if (!MovementManager.InMovement)
                    MovementManager.Go(PathFinder.FindPath(dest));
                return;
            }
            MovementManager.StopMove();

            if (npc == null)
            {
                Logger.Log($"[DK Profile] At {step.Action} spot for '{step.QuestName}' but npc {step.Npc} not visible (phasing?) - waiting");
                Thread.Sleep(500);
                return;
            }

            Thread.Sleep(200);
            Interact.InteractGameObject(npc.GetBaseAddress);
            if (!QuestLUAHelper.WaitForQuestGiverFrame())
            {
                Thread.Sleep(500);
                return;
            }

            if (step.Action == "pickup")
                QuestLUAHelper.GossipPickupQuest(step.QuestName, step.QuestId);
            else if (step.Action == "turnin")
                QuestLUAHelper.GossipTurnInQuest(step.QuestName, step.QuestId);

            Thread.Sleep(600);
        }

        // --- loot a quest item from a gameobject (chest/sword/etc.) at a spot -----------------------------------
        private void RunGetItemFromGo(ScriptedProfileStep step)
        {
            if (HasItem(step.ItemId))
                return;

            // Walk to the spot and only interact once STOPPED. The reference splits travel from the interact for a
            // reason: some of these GOs hand the item over via a channeled "Stealing" cast (e.g. 12724's New Avalon
            // Patrol Schedule GO 191084, gameobject_template type 3 castBarCaption 'Stealing') and the channel breaks
            // the instant we move, so the combined GoToTask...GameObject (which keeps micro-adjusting toward the GO)
            // never lets it finish. Stand still, interact once, then wait the channel out (no-op for instant loot).
            GoToTask.ToPosition(step.GetPosition);
            if (MovementManager.InMovement)
                return;

            WoWGameObject go = ObjectManager.GetWoWGameObjectByEntry(step.GoEntry).FirstOrDefault();
            if (go == null)
            {
                Thread.Sleep(500);
                return;
            }
            Logger.Log($"[DK Profile] Getting item {step.ItemId} from GO {step.GoEntry} for '{step.QuestName}'");
            MovementManager.StopMove();
            Interact.InteractGameObject(go.GetBaseAddress);
            Thread.Sleep(Usefuls.Latency + 400);
            Usefuls.WaitIsCasting();
        }

        // --- use a quest item at a spot (it casts / transforms into ResultItemId) -------------------------------
        private void RunUseItem(ScriptedProfileStep step)
        {
            if (!HasItem(step.ItemId))
                return;
            GoToTask.ToPosition(step.GetPosition);
            MovementManager.StopMove();
            Logger.Log($"[DK Profile] Using item {step.ItemId} at spot for '{step.QuestName}'");
            ItemsManager.UseItem((uint)step.ItemId);
            Usefuls.WaitIsCasting();
            MovementManager.StopMove();
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 1500);
        }

        // --- runeforge the DK weapon (equip -> open Runeforging -> create -> apply). ItemName = the weapon to forge.
        // Exact macro sequence ported from the tested EasyQuest reference (Questfiles/DK/Part 1, quest 12842).
        private void RunRuneforge(ScriptedProfileStep step)
        {
            GoToTask.ToPosition(step.GetPosition);
            MovementManager.StopMove();
            Logger.Log($"[DK Profile] Runeforging '{step.ItemName}' for '{step.QuestName}'");
            Lua.RunMacroText($"/equip {step.ItemName}");
            Thread.Sleep(600);
            Lua.RunMacroText("/use Runeforging");
            Thread.Sleep(1200);
            Lua.LuaDoString("if TradeSkillCreateButton then TradeSkillCreateButton:Click() end");
            Thread.Sleep(700);
            Lua.RunMacroText($"/use {step.ItemName}");
            Thread.Sleep(600);
            Lua.RunMacroText("/use Runeforging");
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 10000);
        }

        // --- repeatedly interact a gameobject until the quest is complete-in-log (e.g. the Acherus Soul Prison for
        // "The Endless Hunger": each click releases + drains an Unworthy Initiate). Ported from the tested reference.
        private void RunInteractGo(ScriptedProfileStep step)
        {
            GoToTask.ToPosition(step.GetPosition);
            if (MovementManager.InMovement)
                return;

            WoWGameObject go = ObjectManager.GetWoWGameObjectByEntry(step.GoEntry).FirstOrDefault();
            if (go == null)
            {
                Thread.Sleep(500);
                return;
            }
            Logger.Log($"[DK Profile] Interacting GO {step.GoEntry} for '{step.QuestName}'");
            Interact.InteractGameObject(go.GetBaseAddress);
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 2000);
        }

        // --- turn a quest in AT a gameobject questender (e.g. 12717's Plague Cauldron 190936). Interacting the GO opens
        // the standard quest frame, but it's a TWO-click hand-in: a "Continue" (progress) screen listing the required
        // items, then a "Complete Quest" (reward) screen (Talamin's screenshot). Interact only OPENS it, so we click
        // through both via Lua. Done-check is IsQuestDone (turnin-go), so once it's handed in this step is satisfied.
        private void RunTurninGo(ScriptedProfileStep step)
        {
            GoToTask.ToPosition(step.GetPosition);
            if (MovementManager.InMovement)
                return;

            WoWGameObject go = ObjectManager.GetWoWGameObjectByEntry(step.GoEntry).FirstOrDefault();
            if (go == null)
            {
                Thread.Sleep(500);
                return;
            }
            Logger.Log($"[DK Profile] Interacting GO {step.GoEntry} to turn in '{step.QuestName}'");
            Interact.InteractGameObject(go.GetBaseAddress);
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 1200);
            // "Continue" on the progress screen -> advances to the reward screen:
            Lua.LuaDoString("if QuestFrameProgressPanel and QuestFrameProgressPanel:IsShown() then CompleteQuest() end");
            Thread.Sleep(800);
            // "Complete Quest" on the reward screen -> hands it in (GetQuestReward(1) is safe with 0 or 1 reward choices):
            Lua.LuaDoString("if QuestFrameRewardPanel and QuestFrameRewardPanel:IsShown() then GetQuestReward(1) end");
            Thread.Sleep(800);
        }

        // --- pilot the Eye of Acherus for "Death Comes From On High" (12641). Ported 1:1 from the tested EasyQuest
        // reference (Questfiles/DK/Part 1, q12641): take control at the mechanism, then fly the eye to each of the 4
        // analyze objectives and siphon it; once all 4 are done, return the eye so the quest reads complete and we can
        // walk to the turn-in. One objective per pulse (we return after each, so the FSM re-evaluates fresh state).
        private void RunEyeOfAcherus(ScriptedProfileStep step)
        {
            // 1) Not controlling the eye yet: interact the control mechanism, let the scripted intro flight play, take control.
            if (!ObjectManager.Me.HaveBuff(EyeControlBuff))
            {
                Lua.RunMacroText("/script UIErrorsFrame:Hide()");
                Logger.Log("[DK Profile] Taking control of the Eye of Acherus");
                GoToTask.ToPositionAndIntecractWithGameObject(EyeControlGoPos, EyeControlGo);
                Thread.Sleep(45000); // scripted intro flight to New Avalon before control is handed over
                wManager.Wow.Helpers.Keybindings.PressKeybindings(wManager.Wow.Enums.Keybindings.JUMP, 1000);
                return;
            }

            // 2) Move toward the first still-incomplete objective, ONE waypoint per pulse. Re-issuing the SAME target
            // across pulses flies smoothly; issuing several different targets within a single pulse makes the eye
            // stutter (Talamin). The phase is derived fresh from the eye's live position each pulse: climb steeply first,
            // then cruise at altitude to above the target, then drop straight down - staying over the ground mobs that
            // shoot the eye down on a direct low approach.
            int idx = -1;
            for (int i = 0; i < EyeObjectivePoints.Length; i++)
                if (!Quest.IsObjectiveComplete(i + 1, step.QuestId)) { idx = i; break; }

            if (idx < 0)
            {
                // 3) All analyzed but still in the eye: return it (drops the control aura -> quest reads complete).
                Logger.Log("[DK Profile] All objectives analyzed - returning the Eye of Acherus");
                SpellManager.CastSpellByIdLUA(EyeReturnSpell);
                Thread.Sleep(1500);
                return;
            }

            Vector3 target = EyeObjectivePoints[idx];
            Vector3 eye = EyePosition();
            float cruiseZ = target.Z + EyeCruiseClearance;

            // Arrived (close in 3D): target the eye and siphon this objective.
            if (eye.DistanceTo(target) < 12f)
            {
                Lua.RunMacroText("/target Eye of Acherus");
                if (ObjectManager.Target != null && ObjectManager.Target.Position.DistanceTo2D(target) < 15)
                {
                    Logger.Log($"[DK Profile] Siphoning objective {idx + 1}/4 of '{step.QuestName}'");
                    SpellManager.CastSpellByIdLUA(EyeSiphonSpell);
                    Usefuls.WaitIsCasting();
                    while (ObjectManager.Me.IsCast)
                        MovementManager.StopMove();
                    Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 3000);
                }
                return;
            }

            // Pick this pulse's single waypoint by phase.
            Vector3 wp;
            if (eye.DistanceTo2D(target) <= 12f)
            {
                wp = target;                                              // above the target -> descend straight onto it
            }
            else if (eye.Z < cruiseZ - 5f)
            {
                // low and still en route -> climb steeply: command full cruise altitude but only a short step toward the
                // target (a pure-vertical CTM is unreliable, so keep a small horizontal component to make it move).
                float dx = target.X - eye.X, dy = target.Y - eye.Y;
                float len = (float)System.Math.Sqrt(dx * dx + dy * dy);
                float sx = len > 0.01f ? eye.X + dx / len * 15f : eye.X;
                float sy = len > 0.01f ? eye.Y + dy / len * 15f : eye.Y;
                wp = new Vector3(sx, sy, cruiseZ);
            }
            else
            {
                wp = new Vector3(target.X, target.Y, cruiseZ);           // at altitude -> cruise to above the target
            }

            ClickToMove.CGPlayer_C__ClickToMove(wp.X, wp.Y, wp.Z, 0, (int)ClickToMoveType.Move, 0.5f);
            Thread.Sleep(250);
        }

        // Current position of the eye (we ARE it, but ObjectManager.Me is the parked body) - read via targeting it.
        private static Vector3 EyePosition()
        {
            Lua.RunMacroText("/target Eye of Acherus");
            Thread.Sleep(100);
            var eye = ObjectManager.Target;
            return eye != null && eye.IsValid ? eye.Position : ObjectManager.Me.Position;
        }

        // --- walk into the glowing transporter behind the Lich King ("The Might Of The Scourge", 12657). Stepping into
        // it (no interact) teleports us down to the Hall of Command; the step is satisfied once we're below LowerLevelZ.
        // We pause WRobot's teleport-detection first so the sudden drop doesn't stop the bot (restored in RunGossip when
        // we walk to the turn-in). Path ported from the tested reference (Part 1, q12657).
        private void RunRideTransporter(ScriptedProfileStep step)
        {
            // Still on the origin level: pause teleport-detection (the sudden level change would otherwise stop the bot)
            // and walk into the glowing transporter (no interact - stepping into it teleports us across). Once we're on
            // the destination level the step is satisfied (TargetZ) and RestoreTeleportGuard stops the residual movement
            // so we don't run back to the pad. Path + TargetZ come from the step data, so the reverse ride is just
            // another step with its own values - no code change (Talamin).
            if (!_teleportGuardActive)
            {
                _savedCloseIfPlayerTeleported = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
                wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
                _teleportGuardActive = true;
                Logger.Log("[DK Profile] Paused teleport-detection for the transporter");
            }

            if (MovementManager.InMovement)
                return;

            List<Vector3> path = step.GetPath();
            Logger.Log($"[DK Profile] Walking into the transporter for '{step.QuestName}'");
            MovementManager.Go(path.Count > 0 ? path : new List<Vector3> { step.GetPosition });
        }

        // --- ride a taxi (the Scourge gryphon for "The Scarlet Harvest", 12670) down to the next staging area. Walk to
        // the taxi NPC and pick its flight gossip option; the server flies us. We wait out the flight; arrival is a
        // TargetZ match (destination level). A taxi is a smooth flight (no position jump) so it needs no teleport guard.
        // Ported from the tested reference (Part 1, q12670).
        private void RunTakeTaxi(ScriptedProfileStep step)
        {
            Logger.Log($"[DK Profile] Taking the taxi (npc {step.Npc}, gossip {step.GossipOption}) for '{step.QuestName}'");
            MountTask.DismountMount(); // get off any ground mount before boarding the taxi (no-op if not mounted)
            GoToTask.ToPositionAndIntecractWithNpc(step.GetPosition, step.Npc, step.GossipOption);
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 10000);
        }

        // --- "Grand Theft Palomino" (12680): steal a Havenshire Stallion (mount it -> aura 52263), then ride it to
        // Salanar and cast "Deliver Stolen Horse". Ported from the tested reference (Part 2, q12680). One horse (the
        // objective count is 1); done = quest complete-in-log after the delivery.
        private void RunStealHorse(ScriptedProfileStep step)
        {
            if (Fight.InFight)
                Fight.StopFight(); // this step runs NoCombat - break any lingering fight so we just ride in and out

            if (!ObjectManager.Me.HaveBuff(StolenHorseBuff))
            {
                // Head for the NEAREST live Havenshire Stallion and interact it (its spellclick 52263 mounts us). Fall
                // back to the stables spot to bring one into view. Combat is cancelled for this step, so we run straight
                // in without stopping to fight the guards.
                WoWUnit stallion = ObjectManager.GetObjectWoW()
                    .OfType<WoWUnit>()
                    .Where(u => u.IsValid && u.IsAlive && u.Entry == HavenshireStallion)
                    .OrderBy(u => u.Position.DistanceTo(ObjectManager.Me.Position))
                    .FirstOrDefault();

                if (stallion != null)
                {
                    Logger.Log($"[DK Profile] Stealing Havenshire Stallion {stallion.Guid} for '{step.QuestName}'");
                    GoToTask.ToPositionAndIntecractWithNpc(stallion.Position, HavenshireStallion);
                }
                else
                {
                    Logger.Log($"[DK Profile] Heading to the Havenshire Stables for '{step.QuestName}'");
                    GoToTask.ToPosition(StallionStealSpot);
                }
                return;
            }

            // Mounted on the stolen horse -> ride to Salanar and deliver it.
            WoWUnit salanar = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(SalanarEntry));
            Vector3 rideTo = (salanar != null && salanar.IsValid) ? salanar.Position : HorseDeliverSpot;

            // "Deliver Stolen Horse" is the stolen horse's VEHICLE ability on ACTION BUTTON 1 - usable only within ~5y of
            // Salanar (Talamin: it must be pressed on button bar 1 inside a 5y radius). Press button 1 there, THEN dismount.
            if (salanar != null && salanar.IsValid && salanar.GetDistance <= 5f)
            {
                MovementManager.StopMove();
                Logger.Log("[DK Profile] Delivering the stolen horse (action button 1) to Salanar");
                wManager.Wow.Helpers.Keybindings.PressKeybindings(wManager.Wow.Enums.Keybindings.ACTIONBUTTON1, 300);
                Thread.Sleep(1200);
                Lua.RunMacroText("/leavevehicle"); // dismount off the horse
                Usefuls.EjectVehicle();
                Thread.Sleep(1200);
                return;
            }

            GoToTask.ToPosition(rideTo); // ride up to Salanar (into the 5y delivery radius)
        }

        // --- "Into the Realm of Shadows" (12687): accepting it transports us to the Realm of Shadows (aura 52693).
        // There, slay the Dark Rider (the fightclass does it) and take his riderless Acherus Deathcharger (28782 -> aura
        // 52280), then ride to the summon spot and cast "Horseman's Call" to summon Salanar, completing the quest.
        // Ported 1:1 from the tested reference (Part 2, q12687). Done = quest complete-in-log.
        private const uint ShadowRealmBuff = 52693;      // in the Realm of Shadows (granted on quest accept)
        private const uint DeathchargerBuff = 52280;     // mounted on the taken Acherus Deathcharger
        private const int AcherusDeathcharger = 28782;   // the riderless deathcharger to mount
        private static readonly Vector3 DeathchargerSpot = new Vector3(2286.949f, -5835.192f, 100.9344f); // roam anchor near where the chargers stand
        private static readonly Vector3 HorsemanCallSpot = new Vector3(2433.466f, -5821.794f, 119.5154f);

        private void RunIntoRealm(ScriptedProfileStep step)
        {
            if (!ObjectManager.Me.HaveBuff(ShadowRealmBuff))
            {
                // not (yet) transported to the shadow realm - accepting the quest should have done it; wait it out.
                Thread.Sleep(1000);
                return;
            }

            if (ObjectManager.Me.PlayerUsingVehicle)
            {
                // riding the taken deathcharger (the server doesn't reliably set aura 52280, but PlayerUsingVehicle is
                // true once we're on it) -> return to the summon spot and call Salanar.
                GoToTask.ToPosition(HorsemanCallSpot);
                Logger.Log("[DK Profile] On the Deathcharger - summoning Salanar with the Horseman's Call");
                Lua.RunMacroText("/cast Horseman's Call");
                Thread.Sleep(3000);
                return;
            }

            // In the realm: the fightclass slays the Dark Rider first, and only THEN does his Acherus Deathcharger
            // appear (Talamin). Take the NEAREST one that has spawned; if none is up yet, stay out in the fields so the
            // fightclass keeps killing the rider (do NOT /stopattack here - that would stop the kill).
            WoWUnit charger = ObjectManager.GetObjectWoW()
                .OfType<WoWUnit>()
                .Where(u => u.IsValid && u.IsAlive && u.Entry == AcherusDeathcharger)
                .OrderBy(u => u.Position.DistanceTo(ObjectManager.Me.Position))
                .FirstOrDefault();

            if (charger != null)
            {
                Lua.RunMacroText("/stopattack"); // riderless charger is up -> stop fighting so we can mount it
                Logger.Log($"[DK Profile] Taking the nearest Acherus Deathcharger ({charger.Guid})");
                GoToTask.ToPositionAndIntecractWithNpc(charger.Position, AcherusDeathcharger);
            }
            else
            {
                GoToTask.ToPosition(DeathchargerSpot); // no charger up yet (rider not dead) - go to the fields, keep fighting
            }
        }

        // --- "The Gift That Keeps On Giving" (12698): raise 5 Scarlet Ghoul pets by using "Gift of the Harvester"
        // (item 39253) on Scarlet Miners (28819) at the mine. Ported 1:1 from the tested reference (Part 2, q12698).
        // Done when 5 "Scarlet Ghoul" pets exist (the reference checks pets, not the quest log). COMBAT step (fightclass
        // kills the miners; we raise the corpses). The item name is on the JSON step so it's sell/delete-protected.
        private const uint GiftOfTheHarvester = 39253;
        private const int ScarletMiner = 28819;
        private static readonly Vector3 ScarletMineSpot = new Vector3(2436.31f, -5912.188f, 103.6842f);

        private static int ScarletGhoulCount() =>
            ObjectManager.GetObjectWoWUnit().Count(u => u.IsMyPet && u.Name == "Scarlet Ghoul");

        private void RunRaiseGhouls(ScriptedProfileStep step)
        {
            WoWUnit miner = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(ScarletMiner));
            if (miner != null && miner.IsValid)
            {
                if (miner.GetDistance > 20)
                {
                    GoToTask.ToPosition(miner.Position, 18);
                    return;
                }
                if (TraceLine.TraceLineGo(miner.Position))
                {
                    wManagerSetting.AddBlackList(miner.Guid, 60 * 1000, true); // no line of sight - skip this miner
                    return;
                }
                Logger.Log($"[DK Profile] Raising a Scarlet Ghoul ({ScarletGhoulCount()}/5) for '{step.QuestName}'");
                ItemsManager.UseItem(GiftOfTheHarvester);
                Thread.Sleep(Usefuls.Latency * 2);
                ClickOnTerrain.Pulse(miner.Position);
                Thread.Sleep(5000);
                return;
            }
            GoToTask.ToPosition(ScarletMineSpot); // no miner in view -> head to the Scarlet mine
        }

        // --- "Massacre At Light's Point" (12701): board a cannon and blast the Scarlet soldiers (kill 100 = objective
        // 1), then fire the finisher. Ported 1:1 from the tested reference (Part 2, q12701). ForceIgnoreIsAttacked keeps
        // us on the cannon instead of breaking off to fight. Done = quest complete-in-log (after the finisher).
        private const int CannonNpc = 28833;
        private const int CannonGo = 190767;
        private const uint CannonFireSpell = 52435;     // ability 1 - the blast that mows down the fleet
        private const uint CannonAbility2 = 52576;      // ability 2 - clears Scarlet Fleet Defenders that BOARD the cannon
        private const uint CannonFinishSpell = 52588;   // the finisher, once 100 are dead
        private const uint CannonBoardingBuff = 46598;  // riding the mine cart to the boat
        private bool _cartPosKnown;
        private Vector3 _cartPos;

        // A Scarlet Fleet Defender (28834 obj mob / 28886 boarder) has climbed onto the cannon (very close) - use ability 2.
        private static bool ScarletDefenderBoarded() =>
            ObjectManager.GetObjectWoWUnit()
                .Any(u => u.IsValid && u.IsAlive && (u.Entry == 28834 || u.Entry == 28886) && u.GetDistance < 10);
        private static readonly Vector3 CannonGoSpot = new Vector3(2391.104f, -5900.058f, 109.1245f);
        private static readonly Vector3 MassacreArea1 = new Vector3(2112.3f, -6185.166f, 13.02931f);
        private static readonly Vector3 MassacreArea2 = new Vector3(2264.897f, -6190.883f, 12.98232f);
        private static readonly Vector3[] MassacreArea1Fire =
        {
            new Vector3(2100.54f, -6132.07f, 5.811379f), new Vector3(2119.31f, -6128.9f, 6.377141f),
            new Vector3(2147.53f, -6121.4f, 1.233099f), new Vector3(2112.57f, -6116.15f, 7.255665f),
            new Vector3(2135.75f, -6093.2f, 6.055835f),
        };
        private static readonly Vector3[] MassacreArea2Fire =
        {
            new Vector3(2210.97f, -6152.05f, 3.054736f), new Vector3(2247.07f, -6140.6f, 2.251676f),
            new Vector3(2266.38f, -6130.26f, 2.323593f), new Vector3(2289.84f, -6133.05f, 4.13626f),
            new Vector3(2215.17f, -6122.65f, 5.375985f), new Vector3(2239.59f, -6096.08f, 5.8311f),
            new Vector3(2268.34f, -6109.25f, 6.246144f),
        };

        private void RunMassacre(ScriptedProfileStep step)
        {
            if (!_teleportGuardActive)
            {
                // completing this quest teleports us back up to the giver (Talamin) - pause WRobot's teleport-detection
                // so the jump doesn't stop the bot. Restored at the turn-in (RunGossip -> RestoreTeleportGuard).
                _savedCloseIfPlayerTeleported = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
                wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
                _teleportGuardActive = true;
                Logger.Log("[DK Profile] Paused teleport-detection for Massacre At Light's Point");
            }

            Conditions.ForceIgnoreIsAttacked = true; // man the cannon without breaking off to fight the mobs

            // Riding the mine cart to the boat. This server does NOT auto-drop us at the boat, so once the cart stops
            // moving we leave it ourselves (Talamin), then board the cannon.
            if (ObjectManager.Me.HaveBuff(CannonBoardingBuff))
            {
                Vector3 cart = ObjectManager.Me.Position;
                bool stopped = _cartPosKnown && cart.DistanceTo(_cartPos) < 2f;
                _cartPos = cart;
                _cartPosKnown = true;
                if (stopped)
                {
                    Logger.Log("[DK Profile] Mine cart arrived at the boat - leaving it");
                    Lua.RunMacroText("/leavevehicle");
                    Usefuls.EjectVehicle();
                    _cartPosKnown = false;
                    Thread.Sleep(1500);
                    return;
                }
                Thread.Sleep(2000); // still riding the rails
                return;
            }

            if (ObjectManager.Me.PlayerUsingVehicle)
            {
                if (Quest.IsObjectiveComplete(1, step.QuestId))
                {
                    Conditions.ForceIgnoreIsAttacked = false;
                    Logger.Log("[DK Profile] Massacre complete - firing the finisher (52588)");
                    SpellManager.CastSpellByIdLUA(CannonFinishSpell);
                    Thread.Sleep(10000);
                    return;
                }

                if (ScarletDefenderBoarded())
                {
                    Logger.Log("[DK Profile] Scarlet Fleet Defender boarded - firing ability 2 (52576)");
                    SpellManager.CastSpellByIdLUA(CannonAbility2);
                    Thread.Sleep(1000);
                    return;
                }

                Vector3 me = ObjectManager.Me.Position;
                if (me.DistanceTo2D(MassacreArea1) < 25f)
                {
                    FireCannonSequence(step, MassacreArea1Fire);
                    return;
                }
                if (me.DistanceTo2D(MassacreArea2) < 25f)
                {
                    FireCannonSequence(step, MassacreArea2Fire);
                    return;
                }
                return; // on the cannon but between the two staging areas - hold
            }

            // not on the cannon -> board it (a live cannon NPC, or the cannon gameobject that spawns/mounts one)
            WoWUnit cannon = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(CannonNpc));
            if (cannon != null && cannon.IsValid)
            {
                Logger.Log("[DK Profile] Boarding the cannon (28833)");
                GoToTask.ToPositionAndIntecractWithNpc(cannon.Position, cannon.Entry);
                return;
            }
            Logger.Log("[DK Profile] Activating the cannon gameobject (190767)");
            GoToTask.ToPositionAndIntecractWithGameObject(CannonGoSpot, CannonGo);
        }

        private void FireCannonSequence(ScriptedProfileStep step, Vector3[] waypoints)
        {
            foreach (Vector3 wp in waypoints)
            {
                if (Quest.IsObjectiveComplete(1, step.QuestId) || !ObjectManager.Me.PlayerUsingVehicle)
                    return; // done, or knocked off the cannon
                ClickToMove.CGPlayer_C__ClickToMove(wp.X, wp.Y, wp.Z, 0, (int)ClickToMoveType.Move, 0.5f);
                SpellManager.CastSpellByIdLUA(CannonFireSpell);
                Thread.Sleep(1000);
            }
        }

        // --- hard-coded training: whenever we're UP TOP in Acherus at a defined point (the DK trains SEVERAL times as it
        // levels - once before flying down for "The Will of the Lich King", again after the portal back up, ...), visit
        // the DK trainer and learn every available spell. The WAQ Trainers state is disabled, so this is where the DK
        // trains. Each point has its own TrainId so it's tracked independently (a single flag would skip later points).
        private const int DkTrainer = 28474;
        private static readonly Vector3 DkTrainerSpot = new Vector3(2413.92f, -5524.47f, 377.0429f);
        private static readonly System.Collections.Generic.HashSet<int> _trainedIds = new System.Collections.Generic.HashSet<int>();

        private void RunTrain(ScriptedProfileStep step)
        {
            if (GoToTask.ToPositionAndIntecractWithNpc(DkTrainerSpot, DkTrainer, 1))
            {
                Usefuls.SelectGossipOption(GossipOptionsType.trainer);
                Thread.Sleep(500);
                Trainer.TrainingSpell();
                Thread.Sleep(3000);
                _trainedIds.Add(step.TrainId);
                Logger.Log($"[DK Profile] Trained all available spells at the DK trainer (28474) [point {step.TrainId}]");
            }
        }

        // --- one-shot: point WRobot at a ground mount (once we own it) so it rides everywhere from here on. Set from
        // step.ItemName (the mount's spell name). Satisfied once the setting sticks, so it only writes + saves once.
        private static bool _groundMountSet; // one-shot guard so set-ground-mount never flips back to current (see IsStepSatisfied)

        private void RunSetGroundMount(ScriptedProfileStep step)
        {
            Logger.Log($"[DK Profile] Setting WRobot ground mount to '{step.ItemName}' (UseGroundMount=true)");
            wManagerSetting.CurrentSetting.GroundMountName = step.ItemName;
            wManagerSetting.CurrentSetting.UseGroundMount = true;
            wManagerSetting.CurrentSetting.Save();
            _groundMountSet = true;
            Thread.Sleep(200);
        }

        // --- placeholder for a CUSTOM quest whose bespoke behaviour isn't built yet. The whole DK chain is laid out in
        // the profile so it's enumerated once; the doable (pickup/turnin/patrol) quests run automatically, and the
        // executor parks HERE at the next custom quest, logging what it needs, until we build the real action.
        private void RunTodo(ScriptedProfileStep step)
        {
            Logger.Log($"[DK Profile] >>> NEXT TO BUILD: '{step.QuestName}' ({step.QuestId}) needs a custom behaviour — {step.Comment}");
            Thread.Sleep(5000);
        }

        // --- "Death's Challenge" (12733): win 5 duels against Death Knight Initiates (28406). Find the nearest one near
        // the Death's Breach centre, challenge it (interact + gossip option 1 = "I challenge you to a duel"), dismount,
        // let the duel start, then Fight it - the fightclass wins it. Repeat until the quest is complete-in-log (5 wins).
        // Ported from the tested reference (Part 2, q12733). This is a COMBAT step - the fightclass stays loaded.
        private static readonly Vector3 DuelCentre = new Vector3(2371.366f, -5701.044f, 153.9222f);

        private void RunDuel(ScriptedProfileStep step)
        {
            WoWUnit initiate = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(step.Npc));
            if (initiate == null || !initiate.IsValid)
            {
                GoToTask.ToPosition(DuelCentre); // none in view -> head to where the initiates stand
                return;
            }
            if (initiate.Position.DistanceTo2D(DuelCentre) > 100f)
            {
                wManagerSetting.AddBlackList(initiate.Guid, 5 * 60 * 1000, true); // stray far-away spawn - ignore it
                return;
            }

            Logger.Log($"[DK Profile] Challenging a Death Knight Initiate to a duel for '{step.QuestName}'");
            if (GoToTask.ToPositionAndIntecractWithNpc(initiate.Position, initiate.Entry, step.GossipOption))
            {
                MountTask.DismountMount();
                Thread.Sleep(7000); // accept + 3s countdown + extra ~3s: the initiate needs a moment to flip from
                                    // friendly to hostile before we engage (Talamin), else Fight.StartFight finds no enemy
                Interact.InteractGameObject(initiate.GetBaseAddress);
                Fight.StartFight(initiate.Guid);
            }
        }

        // --- combat kill-switch for NoCombat steps. Subscribing to FightEvents and cancelling every fight start/loop
        // makes the bot ignore ALL npcs - it won't react to or attack anything, just does the step ("rein und raus",
        // Talamin). Same mechanism the Wholesome Dungeon Crawler uses for IgnoreFightsDuringPath. Toggled by the step's
        // NoCombat flag; the hooks are torn down again as soon as a normal (combat) step becomes current.
        private void ApplyNoCombat(bool on)
        {
            if (on == _noCombatHooked)
                return;

            if (on)
            {
                // Fully UNLOAD the fightclass (e.g. AIO3) for this step so it cannot cast at all (Talamin prefers a real
                // load/unload over any in-game Lua flag). The fightclass runs its own thread, so nothing else reliably
                // silences it. WRobot's own fight task is cancelled via the FightEvents hooks too.
                CustomClass.DisposeCustomClass();
                FightEvents.OnFightLoop += CancelFightHandler;
                FightEvents.OnFightStart += CancelFightHandler;
                if (Fight.InFight)
                    Fight.StopFight();
                Logger.Log("[DK Profile] Combat off - fightclass unloaded for this step (rein und raus)");
            }
            else
            {
                FightEvents.OnFightLoop -= CancelFightHandler;
                FightEvents.OnFightStart -= CancelFightHandler;
                CustomClass.LoadCustomClass(); // combat allowed again -> bring the fightclass back
                Logger.Log("[DK Profile] Combat on - fightclass reloaded");
            }
            _noCombatHooked = on;
        }

        private void CancelFightHandler(WoWUnit currentTarget, CancelEventArgs cancelable) =>
            cancelable.Cancel = true;

        // --- plugin kill-switch for DisablePlugins steps. Some quests hand out gear we must keep worn (e.g. a quest
        // weapon we "use" on mobs); the Wholesome_Inventory_Manager's AutoEquip would immediately swap it back for the
        // better item, so we UNLOAD all WRobot plugins for the step (Talamin: better to kill all running plugins than
        // just one). PluginsManager has no per-plugin runtime pause, so it's a real dispose/reload - the analog of
        // ApplyNoCombat. DisposeAllPlugins leaves each plugin's Actif flag untouched, so LoadAllPlugins reloads exactly
        // the set the user had enabled. Nothing is persisted (no CurrentSetting.Save), so a missed restore can't leave
        // plugins off in the user's config.
        private bool _pluginsDisabled;

        private void ApplyNoPlugins(bool on)
        {
            if (on == _pluginsDisabled)
                return;

            if (on)
            {
                wManager.Plugin.PluginsManager.DisposeAllPlugins();
                Logger.Log("[DK Profile] Plugins off - all WRobot plugins unloaded for this step (AutoEquip won't swap the quest weapon)");
            }
            else
            {
                Conditions.ForceIgnoreIsAttacked = false; // persuade-type steps set this true; clear it on the way out
                wManager.Plugin.PluginsManager.LoadAllPlugins(); // reloads exactly the plugins the user had enabled
                Logger.Log("[DK Profile] Plugins on - WRobot plugins reloaded");
            }
            _pluginsDisabled = on;
        }

        // --- persuade: equip a quest weapon and INTERACT with (not kill) target mobs until an objective ticks over.
        // For 12720 "How To Win Friends...": open the Ornately Jeweled Box (ItemId) to get "Keleseth's Persuader"
        // (ResultItemId), equip it, then interact the New Avalon soldiers (TargetEntries) - one eventually "cracks".
        // Runs with NoCombat + DisablePlugins so the fightclass doesn't kill the targets and AutoEquip doesn't unequip
        // the persuader. Done = objective 1 complete. Recipe from the tested reference Part 3 (FullCSharpCode).
        private void RunPersuade(ScriptedProfileStep step)
        {
            Conditions.ForceIgnoreIsAttacked = true; // don't break off to react to the soldiers hitting us

            // 1) Open the box to get the persuaders, then loot it.
            if (step.ItemId > 0 && ItemsManager.HasItemById((uint)step.ItemId))
            {
                Logger.Log($"[DK Profile] Opening '{step.ItemName}' ({step.ItemId}) for '{step.QuestName}'");
                ItemsManager.UseItem((uint)step.ItemId);
                Thread.Sleep(2500);
                Lua.LuaDoString("for i=1,GetNumLootItems() do LootSlot(i) end");
                Thread.Sleep(2500);
                return;
            }

            // 2) Equip the persuader if it isn't already WORN. NO early return - fall through to persuade in the same
            // pulse (like the reference). HasItemById counts EQUIPPED items too, so a "still in bags?" gate loops
            // forever (Talamin: bot spammed 'Equipping ...' and never persuaded). Gate on the equipped set instead.
            bool equipped = EquippedItems.GetEquippedItems()
                .Any(i => i != null && i.IsValid
                          && ((step.ResultItemId > 0 && (int)i.Entry == step.ResultItemId)
                              || (!string.IsNullOrEmpty(step.ResultItemName) && i.Name == step.ResultItemName)));
            if (!equipped)
            {
                Logger.Log($"[DK Profile] Equipping '{step.ResultItemName}' for '{step.QuestName}'");
                ItemsManager.EquipItemByName(step.ResultItemName);
                Thread.Sleep(250);
                ItemsManager.EquipItemByName(step.ResultItemName); // second hand
                Thread.Sleep(400);
            }

            // 3) Persuade the nearest target: INTERACT with it (NOT Fight.StartFight).
            Vector3 myPos = ObjectManager.Me.Position;
            WoWUnit mob = ObjectManager.GetObjectWoW()
                .OfType<WoWUnit>()
                .Where(u => u.IsValid && u.IsAlive && step.TargetEntries != null && step.TargetEntries.Contains((int)u.Entry)
                            && u.Position.DistanceTo(myPos) < GrindSearchRange)
                .OrderBy(u => u.Position.DistanceTo(myPos))
                .FirstOrDefault();
            if (mob != null && mob.IsValid)
            {
                Lua.RunMacroText("/petpassive"); // the ghoul (if any) must not kill the target
                if (mob.Position.DistanceTo(myPos) > 5f)
                {
                    GoToTask.ToPositionAndIntecractWithNpc(mob.Position, (int)mob.Entry);
                    return;
                }
                // "Persuade" = beat them with the equipped Persuader using WEAPON SWINGS ONLY, no abilities (the
                // fightclass is unloaded via NoCombat). Right-click targets + starts melee auto-attack; /startattack
                // makes sure the white swings engage. One soldier eventually "cracks" (Talamin: you HIT them with the
                // weapon till they lose health, you don't just talk to them).
                MovementManager.Face(mob); // turn to the target, else the swings don't land and it just beats on us (Talamin)
                Interact.InteractGameObject(mob.GetBaseAddress);
                Lua.RunMacroText("/startattack");
                Thread.Sleep(1500);
                return;
            }

            // 4) No target nearby -> roam the hotspots.
            List<Vector3> spots = step.GetPath();
            if (spots.Count > 0)
            {
                Vector3 spot = spots[_grindHotspotIndex % spots.Count];
                _grindHotspotIndex++;
                GoToTask.ToPosition(spot);
            }
        }

        // --- "A Special Surprise" (race-gated: 12739 / 12742-12750). Each race relives a memory: pick the race's quest
        // up from Plaguefist (29053), go to the memory spot (1326.98,-5764.5,137.82) and approach/interact - or, if it
        // spawns hostile, kill - the race-specific memory NPC, then hand the quest back in at Plaguefist. Recipe from
        // the tested reference Part 3 (FullCSharpCode); race->quest from quest_template RequiredRaces, race->npc from
        // the reference. The step's own QuestId is 0 (unknown until we read the race), so everything resolves at runtime.
        private const int SpecialSurpriseGiver = 29053; // Plaguefist (giver AND ender of every race variant)

        private void RunSpecialSurprise(ScriptedProfileStep step)
        {
            int questId = SpecialSurpriseQuestId();
            if (questId == 0)
                return; // race we have no mapping for (not a WotLK DK race)
            int npcId = SpecialSurpriseNpcId();

            // complete in the log -> hand it back in to Plaguefist.
            if (IsQuestCompleteInLog(questId))
            {
                RunGossip(SurpriseGossipStep("turnin", questId));
                return;
            }
            // not accepted yet -> pick it up from Plaguefist.
            if (!Quest.HasQuest(questId))
            {
                RunGossip(SurpriseGossipStep("pickup", questId));
                return;
            }
            // accepted -> go to the race's memory NPC and trigger the memory (kill it if it's hostile).
            WoWUnit npc = ObjectManager.GetObjectWoW()
                .OfType<WoWUnit>()
                .FirstOrDefault(u => u != null && u.IsValid && u.IsAlive && u.Entry == npcId);
            if (npc != null && npc.IsValid)
            {
                if (npc.IsAttackable)
                {
                    Interact.InteractGameObject(npc.GetBaseAddress);
                    Fight.StartFight(npc.Guid);
                    return;
                }
                GoToTask.ToPosition(npc.Position);
                if (npc.Position.DistanceTo(ObjectManager.Me.Position) < 5f)
                {
                    Interact.InteractGameObject(npc.GetBaseAddress);
                    Thread.Sleep(2000);
                }
                return;
            }
            GoToTask.ToPosition(new Vector3(1326.98f, -5764.499f, 137.8199f));
        }

        private ScriptedProfileStep SurpriseGossipStep(string action, int questId) =>
            new ScriptedProfileStep
            {
                Action = action,
                Npc = SpecialSurpriseGiver,
                QuestId = questId,
                QuestName = "A Special Surprise",
                Map = EbonHoldMapId,
                X = 1369.60f,
                Y = -5720.82f,
                Z = 136.42f,
            };

        // race -> the race's "A Special Surprise" quest id (from quest_template RequiredRaces).
        private static int SpecialSurpriseQuestId()
        {
            switch (ObjectManager.Me.PlayerRace)
            {
                case PlayerFactions.Human:    return 12742;
                case PlayerFactions.Orc:      return 12748;
                case PlayerFactions.Dwarf:    return 12744;
                case PlayerFactions.NightElf: return 12743;
                case PlayerFactions.Undead:   return 12750;
                case PlayerFactions.Tauren:   return 12739;
                case PlayerFactions.Gnome:    return 12745;
                case PlayerFactions.Troll:    return 12749;
                case PlayerFactions.BloodElf: return 12747;
                case PlayerFactions.Draenei:  return 12746;
                default:                      return 0;
            }
        }

        // race -> the race's memory NPC to approach/kill (from the tested reference).
        private static int SpecialSurpriseNpcId()
        {
            switch (ObjectManager.Me.PlayerRace)
            {
                case PlayerFactions.Human:    return 29061;
                case PlayerFactions.Orc:      return 29072;
                case PlayerFactions.Dwarf:    return 29067;
                case PlayerFactions.NightElf: return 29065;
                case PlayerFactions.Undead:   return 29071;
                case PlayerFactions.Tauren:   return 29032;
                case PlayerFactions.Gnome:    return 29068;
                case PlayerFactions.Troll:    return 29073;
                case PlayerFactions.BloodElf: return 29074;
                case PlayerFactions.Draenei:  return 29070;
                default:                      return 29061;
            }
        }

        // --- "Ambush At The Overlook" (12754): walk to the overlook, use the Makeshift Cover (ItemId 39645) to spring
        // the scripted ambush, then wait it out. The quest has NO standard DB objective (RequiredNpcOrGo all 0) - the
        // event grants credit, and the fightclass (its own thread, unaffected by the sleep) kills the Scarlet Courier
        // if it aggros. Done = complete-in-log. Recipe from the tested reference Part 3 (UseItem + 30s wait).
        private void RunAmbush(ScriptedProfileStep step)
        {
            GoToTask.ToPosition(step.GetPosition);
            if (MovementManager.InMovement)
                return;

            MovementManager.StopMove();
            Logger.Log($"[DK Profile] Using '{step.ItemName}' ({step.ItemId}) to spring the ambush for '{step.QuestName}'");
            ItemsManager.UseItem((uint)step.ItemId);
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 30000);
        }

        // --- interact a portal gameobject that TELEPORTS us (12757 "Scarlet Armies Approach...": the Portal to Acherus
        // GO 191155 warps us from New Avalon up to Acherus). Pause teleport-detection around the interact so the sudden
        // position jump doesn't stop the bot (restored at the turn-in via RunGossip -> RestoreTeleportGuard). Done via
        // TargetZ (we're above the Acherus level). Recipe from the tested reference Part 3 (OverridePulse).
        private void RunPortal(ScriptedProfileStep step)
        {
            if (!_teleportGuardActive)
            {
                _savedCloseIfPlayerTeleported = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
                wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
                _teleportGuardActive = true;
                Logger.Log("[DK Profile] Paused teleport-detection for the portal");
            }

            GoToTask.ToPosition(step.GetPosition);
            if (MovementManager.InMovement)
                return;

            MovementManager.StopMove();
            MountTask.DismountMount(); // get off the ground mount so we can interact the portal

            WoWGameObject go = ObjectManager.GetWoWGameObjectByEntry(step.GoEntry).FirstOrDefault();
            if (go == null)
            {
                // the portal only spawns a few seconds AFTER the quest is accepted - wait for it at the spot (Talamin).
                Logger.Log($"[DK Profile] At the portal spot for '{step.QuestName}', waiting for GO {step.GoEntry} to spawn");
                Thread.Sleep(1000);
                return;
            }
            Logger.Log($"[DK Profile] Interacting portal GO {step.GoEntry} for '{step.QuestName}'");
            Interact.InteractGameObject(go.GetBaseAddress);
            Thread.Sleep(step.WaitMs > 0 ? step.WaitMs : 5000);
        }

        // --- "An End To All Things..." (12779): ride the Frost Wyrm (summoned by the Horn of the Frostbrood 39700) and
        // blast Scarlet soldiers (29102/29103, obj1 = 150) + ballistae (29104, obj2 = 10) with the vehicle's frost
        // breath (VehicleMenuBarActionButton1), flying between ~18 hotspots. Retreat + eject when the wyrm gets low or
        // both objectives are done. Ported 1:1 from the tested reference (Part 4, FullCSharpCode). Also reused later by
        // 13166 "Battle For Ebon Hold" (same wyrm). Runs as a VehicleWanted step so WAQExitVehicle leaves the wyrm alone.
        private const int WyrmHornItem = 39700;
        private static readonly Vector3 WyrmSafeSpot = new Vector3(2330.405f, -5723.773f, 169.7132f);
        private static readonly System.Collections.Generic.List<int> WyrmMobs1 = new System.Collections.Generic.List<int> { 29102, 29103 };
        private static readonly System.Collections.Generic.List<int> WyrmMobs2 = new System.Collections.Generic.List<int> { 29104 };
        private static readonly System.Collections.Generic.List<Vector3> WyrmHotspots = new System.Collections.Generic.List<Vector3>
        {
            new Vector3(1760.304f, -5813.95f, 185.7857f), new Vector3(1720.478f, -5802.029f, 186.664f),
            new Vector3(1686.54f, -5791.31f, 187.5138f),  new Vector3(1651.945f, -5782.63f, 186.0213f),
            new Vector3(1616.954f, -5775.974f, 186.7899f), new Vector3(1581.76f, -5771.152f, 188.3408f),
            new Vector3(1546.357f, -5769.87f, 188.1616f),  new Vector3(1518.384f, -5789.091f, 187.4943f),
            new Vector3(1509.293f, -5823.416f, 187.9689f), new Vector3(1520.269f, -5856.526f, 189.0811f),
            new Vector3(1546.654f, -5880.152f, 187.3205f), new Vector3(1574.685f, -5902.087f, 183.7643f),
            new Vector3(1609.05f, -5908.018f, 181.7404f),  new Vector3(1644.768f, -5904.485f, 181.2636f),
            new Vector3(1678.795f, -5895.87f, 182.0125f),  new Vector3(1713.193f, -5885.437f, 180.5251f),
            new Vector3(1741.257f, -5864.352f, 179.5802f), new Vector3(1759.878f, -5833.864f, 178.0917f),
        };

        private void RunFrostWyrm(ScriptedProfileStep step)
        {
            Conditions.ForceIgnoreIsAttacked = true;
            wManagerSetting.CurrentSetting.UseMount = false;

            // 1) Not on the wyrm yet -> summon it (Horn of the Frostbrood) and JUMP to take off.
            if (!ObjectManager.Me.PlayerUsingVehicle)
            {
                Logger.Log($"[DK Profile] Summoning the Frost Wyrm for '{step.QuestName}'");
                ItemsManager.UseItem((uint)WyrmHornItem);
                Usefuls.WaitIsCasting();
                Thread.Sleep(2500);
                wManager.Wow.Helpers.Keybindings.PressKeybindings(wManager.Wow.Enums.Keybindings.JUMP, 1000);
                return;
            }

            int q = step.QuestId;
            bool objectivesDone = Quest.IsObjectiveComplete(1, q) && Quest.IsObjectiveComplete(2, q);

            // 2) Wyrm low on health/mana, or both objectives done -> fly to the safe spot and eject. Guard on a VALID
            // pet: right after mounting, ObjectManager.Pet may not be the wyrm yet and would report 0% -> false retreat.
            bool wyrmLow = ObjectManager.Pet != null && ObjectManager.Pet.IsValid
                           && (ObjectManager.Pet.HealthPercent < 50 || ObjectManager.Pet.ManaPercentage < 10);
            if (wyrmLow || objectivesDone)
            {
                if (ObjectManager.Me.Position.DistanceTo2D(WyrmSafeSpot) < 5f)
                {
                    MovementManager.StopMove();
                    Thread.Sleep(Usefuls.Latency);
                    Usefuls.EjectVehicle();
                }
                else
                {
                    ClickToMove.CGPlayer_C__ClickToMove(WyrmSafeSpot.X, WyrmSafeSpot.Y, WyrmSafeSpot.Z, 0, (int)ClickToMoveType.Move, 0);
                }
                return;
            }

            // 3) Find the nearest still-needed target (soldiers for obj1, ballistae for obj2) and blast it.
            System.Collections.Generic.List<int> targets = new System.Collections.Generic.List<int>();
            if (!Quest.IsObjectiveComplete(1, q)) targets.AddRange(WyrmMobs1);
            if (!Quest.IsObjectiveComplete(2, q)) targets.AddRange(WyrmMobs2);
            WoWUnit mob = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(targets));

            if (mob != null && mob.IsValid && mob.IsAlive && mob.IsAttackable)
            {
                if (mob.GetDistance2D < 13f)
                {
                    long h = mob.Health;
                    // aim the wyrm's frost breath at the target - finicky vehicle aiming, ported 1:1 from the reference.
                    Move.Forward();
                    Move.Backward();
                    MovementManager.StopMove();
                    Thread.Sleep(Usefuls.Latency);
                    MovementManager.Face(mob);
                    Move.Forward();
                    Move.Backward();
                    Lua.LuaDoString("MoveViewUpStart()");
                    Thread.Sleep(1000);
                    Lua.LuaDoString("MoveViewUpStop()");
                    Lua.LuaDoString("MoveAndSteerStart()");
                    Lua.LuaDoString("MoveAndSteerStop()");
                    Thread.Sleep(Usefuls.Latency);
                    Lua.RunMacroText("/click VehicleMenuBarActionButton1"); // fire the frost breath
                    Usefuls.WaitIsCasting();
                    Thread.Sleep(500);
                    if (mob.IsAlive && mob.Health >= h) // took no damage -> bad angle, skip it a while
                        wManagerSetting.AddBlackList(mob.Guid, 120 * 1000);
                }
                else
                {
                    Vector3 above = mob.Position + new Vector3(0, 0, 20); // approach from above (dive-bomb)
                    ClickToMove.CGPlayer_C__ClickToMove(above.X, above.Y, above.Z, 0, (int)ClickToMoveType.Move, 0);
                    Thread.Sleep(50);
                }
                return;
            }

            // 4) Nothing in sight -> fly to the next hotspot.
            Vector3 spot = WyrmHotspots[_grindHotspotIndex % WyrmHotspots.Count];
            _grindHotspotIndex++;
            ClickToMove.CGPlayer_C__ClickToMove(spot.X, spot.Y, spot.Z, 0, (int)ClickToMoveType.Move, 0);
            Thread.Sleep(1500);
        }

        // --- "The Light of Dawn" (12801): a big scripted escort battle. Talk to the escort NPC (step.Npc = Darion
        // 29173) at the start spot (step position, gossip step.GossipOption) to kick it off, then FOLLOW him (within
        // 10y) and, whenever he's in combat, kill any attacker within 30y (defend the escort - the fightclass does the
        // killing). If he's out of sight, move toward the escort path (step.Path[0]). Ported from reference Part 4.
        private static bool _escortStarted;

        private void RunEscortBattle(ScriptedProfileStep step)
        {
            List<Vector3> path = step.GetPath();
            Vector3 endSpot = path.Count > 0 ? path[0] : step.GetPosition;
            bool nearEnd = ObjectManager.Me.Position.DistanceTo(endSpot) < 30f;

            // 1) Start the escort by talking to the escort NPC - unless we're already at the end area (e.g. a restart
            // during the end cinematic), in which case we must NOT re-talk, just wait it out below.
            if (!_escortStarted && !nearEnd)
            {
                Logger.Log($"[DK Profile] Starting the escort battle (talk npc {step.Npc}) for '{step.QuestName}'");
                if (GoToTask.ToPositionAndIntecractWithNpc(step.GetPosition, step.Npc, step.GossipOption))
                    _escortStarted = true;
                return;
            }
            _escortStarted = true;

            // The battle only kicks off on a ~5 min server timer while we wait next to Darion - keep the client awake.
            wManager.Wow.Bot.States.AntiAfk.Pulse();

            WoWUnit escort = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(step.Npc));

            // 2) Reached the end area: finish off any remaining attackers, then just WAIT at the spot until the quest
            // completes. Talamin: after the battle the Lich King cinematic plays and DARION DESPAWNS, so do NOT chase
            // him or re-talk - hold the end spot and wait for complete-in-log. The step stays current till then.
            if (nearEnd)
            {
                WoWUnit att = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitAttackables(30));
                if (att != null && att.IsValid && att.IsAlive && att.IsAttackable)
                {
                    Interact.InteractGameObject(att.GetBaseAddress);
                    Fight.StartFight(att.Guid);
                    return;
                }
                if (ObjectManager.Me.Position.DistanceTo(endSpot) > 5f)
                    GoToTask.ToPosition(endSpot, 3);
                else
                    MovementManager.StopMove();
                Thread.Sleep(500);
                return;
            }

            // 3) Still escorting: follow Darion (10y); while he's fighting, kill attackers within 30y.
            if (escort != null && escort.IsValid)
            {
                if (escort.InCombat)
                {
                    WoWUnit mob = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitAttackables(30));
                    if (mob != null && mob.IsValid && mob.IsAlive && mob.IsAttackable)
                    {
                        Interact.InteractGameObject(mob.GetBaseAddress);
                        Fight.StartFight(mob.Guid);
                        return;
                    }
                }
                GoToTask.ToPosition(escort.Position, 10);
                return;
            }

            // 4) Darion out of sight mid-escort -> move toward the destination.
            GoToTask.ToPosition(endSpot);
        }

        // --- "Taking Back Acherus" (13165): cast Death Gate (spell 50977), which opens a portal gameobject (step.GoEntry
        // = 190942) that we created; step into it to teleport up to Acherus. Pause teleport-detection (the gate warps us).
        // Done = we're in "Acherus: The Ebon Hold". Ported from reference Part 4.
        private const uint DeathGateSpell = 50977;

        private void RunDeathGate(ScriptedProfileStep step)
        {
            if (!_teleportGuardActive)
            {
                _savedCloseIfPlayerTeleported = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
                wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
                _teleportGuardActive = true;
                Logger.Log("[DK Profile] Paused teleport-detection for the Death Gate");
            }

            // gate already open (created by us)? -> step into it to teleport to Acherus.
            WoWGameObject gate = ObjectManager.GetNearestWoWGameObject(ObjectManager.GetWoWGameObjectByEntry(step.GoEntry));
            if (gate != null && gate.IsValid && gate.CreatedBy == ObjectManager.Me.Guid)
            {
                Logger.Log($"[DK Profile] Stepping into the Death Gate for '{step.QuestName}'");
                GoToTask.ToPositionAndIntecractWithGameObject(gate.Position, gate.Entry);
                return;
            }

            // else cast Death Gate to open it.
            Logger.Log($"[DK Profile] Casting Death Gate for '{step.QuestName}'");
            SpellManager.CastSpellByIdLUA(DeathGateSpell);
            Usefuls.WaitIsCasting();
            Thread.Sleep(1500);
        }

        // --- "The Battle For The Ebon Hold" (13166): Acherus has teleport pads between the lower level (~Z378) and the
        // upper battle level (~Z421). Ride the bottom pad UP, kill the obj1 boss (31099) then the obj2 adds (31095/96/98)
        // at the top hotspot, then ride the top pad back DOWN. Done = both objectives + back at the bottom. Guard the
        // teleport just in case (Talamin: remember teleports). Ported from reference Part 4.
        private static readonly Vector3 AcherusBottomTele = new Vector3(2390.11f, -5640.988f, 378.1047f);
        private static readonly Vector3 AcherusTopTele = new Vector3(2383.363f, -5644.801f, 421.68f);
        private static readonly Vector3 AcherusFightHotspot = new Vector3(2408.285f, -5627.606f, 420.6619f);

        private void RunAcherusBattle(ScriptedProfileStep step)
        {
            if (!_teleportGuardActive)
            {
                _savedCloseIfPlayerTeleported = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
                wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
                _teleportGuardActive = true;
                Logger.Log("[DK Profile] Paused teleport-detection for the Acherus teleporters");
            }

            int q = step.QuestId;
            bool done = Quest.IsObjectiveComplete(1, q) && Quest.IsObjectiveComplete(2, q);

            if (done)
            {
                GoToTask.ToPosition(AcherusTopTele); // the top pad takes us back down
                return;
            }
            if (ObjectManager.Me.Position.DistanceZ(AcherusTopTele) > 30f)
            {
                GoToTask.ToPosition(AcherusBottomTele); // ride the bottom pad up to the battle
                return;
            }

            // up top -> fight the current objective's targets.
            List<int> targets = Quest.IsObjectiveComplete(1, q)
                ? new List<int> { 31095, 31096, 31098 }
                : new List<int> { 31099 };
            WoWUnit mob = ObjectManager.GetNearestWoWUnit(ObjectManager.GetWoWUnitByEntry(targets));
            if (mob != null && mob.IsValid && mob.IsAlive && mob.IsAttackable)
            {
                Interact.InteractGameObject(mob.GetBaseAddress);
                Fight.StartFight(mob.Guid);
                return;
            }
            GoToTask.ToPosition(AcherusFightHotspot);
        }

        // --- faction finale: Warchief's Blessing (Horde, quest 13189, ender Thrall 4949 @ Orgrimmar) / Where Kings Walk
        // (Alliance, 13188, ender King Varian 29611 @ Stormwind). Pick the quest up from Darion (31084) in the relocated
        // Acherus, take the faction PORTAL gameobject (193052 Horde / 193053 Alliance) to teleport to the capital, then
        // turn in to the leader. NeedToRun also allows HasQuest(13188/13189) so the profile keeps running off-map (in the
        // capital) for the hand-in. Recipe from reference Part 4 (the portal OverridePulse + the DB giver/ender).
        private void RunFactionFinale(ScriptedProfileStep step)
        {
            bool horde = IsHorde();
            int questId = horde ? 13189 : 13188;
            string questName = horde ? "Warchief's Blessing" : "Where Kings Walk";
            int portalGo = horde ? 193052 : 193053;
            int leaderNpc = horde ? 4949 : 29611; // Thrall / King Varian Wrynn
            Vector3 portalSpot = horde ? new Vector3(2347.855f, -5695.505f, 382.241f)
                                       : new Vector3(2324.651f, -5659.718f, 382.2408f);

            // note: this is a "report" quest - it goes complete-in-log the instant it's accepted, and Quest.HasQuest
            // then returns FALSE, so everything keys off HasQuest OR complete-in-log.
            bool haveIt = Quest.HasQuest(questId) || IsQuestCompleteInLog(questId);

            // 1) don't have it at all -> pick it up from Darion (31084) in the relocated Acherus.
            if (!haveIt && !IsQuestDone(questId))
            {
                RunGossip(FinaleGossipStep("pickup", 31084, questId, questName, new Vector3(2375.38f, -5650.72f, 382.44f)));
                return;
            }

            // 2) have it and we've already teleported to the capital (not in Acherus) -> walk to the faction leader's
            // known spot and turn in. RunGossip heads for the NPC (finds it once in range) or its position otherwise -
            // the portal can drop us far from Thrall, so we must PATH there, not just wait for him to load.
            if (!(Usefuls.ContinentId == EbonHoldMapId || OnRelocatedAcherus()))
            {
                Vector3 leaderSpot = horde ? new Vector3(1920.01f, -4123.95f, 43.3733f)   // Thrall @ Orgrimmar
                                           : new Vector3(-8441.42f, 333.102f, 122.579f);   // King Varian @ Stormwind
                RunGossip(FinaleGossipStep("turnin", leaderNpc, questId, questName, leaderSpot));
                return;
            }

            // 3) have it, still in Acherus -> take the faction portal to the capital.
            {
                if (!_teleportGuardActive)
                {
                    _savedCloseIfPlayerTeleported = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
                    wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
                    _teleportGuardActive = true;
                    Logger.Log("[DK Profile] Paused teleport-detection for the faction portal");
                }
                GoToTask.ToPosition(portalSpot);
                if (MovementManager.InMovement)
                    return;
                MovementManager.StopMove();
                WoWGameObject portal = ObjectManager.GetWoWGameObjectByEntry(portalGo).FirstOrDefault();
                if (portal != null && portal.IsValid)
                {
                    Logger.Log($"[DK Profile] Taking the faction portal (GO {portalGo}) to the capital for '{questName}'");
                    Interact.InteractGameObject(portal.GetBaseAddress);
                    Thread.Sleep(3000);
                }
                else
                {
                    Thread.Sleep(500);
                }
            }
        }

        private ScriptedProfileStep FinaleGossipStep(string action, int npc, int questId, string questName, Vector3 pos) =>
            new ScriptedProfileStep
            {
                Action = action, Npc = npc, QuestId = questId, QuestName = questName,
                Map = 0, X = pos.X, Y = pos.Y, Z = pos.Z,
            };

        private static bool IsHorde()
        {
            switch (ObjectManager.Me.PlayerRace)
            {
                case PlayerFactions.Orc:
                case PlayerFactions.Undead:
                case PlayerFactions.Tauren:
                case PlayerFactions.Troll:
                case PlayerFactions.BloodElf:
                case PlayerFactions.Goblin:
                    return true;
                default:
                    return false;
            }
        }

        // --- patrol an area doing kill and/or gather objectives together: roam the hotspots, engage the nearest target
        // mob (TargetEntries) and loot the nearest objective gameobject (GoEntries). The combat fightclass (above us in
        // the FSM) does the actual killing + looting. Serves several quests at once (QuestIds), done when ALL of them
        // read complete-in-log. All data comes from the step, so any DK kill/gather area is just another patrol step.
        private void RunPatrol(ScriptedProfileStep step)
        {
            Vector3 myPos = ObjectManager.Me.Position;

            // 1) Nearest objective MOB in range -> engage it (the fightclass moves in and kills it).
            if (step.TargetEntries != null && step.TargetEntries.Count > 0)
            {
                WoWUnit mob = ObjectManager.GetObjectWoW()
                    .OfType<WoWUnit>()
                    .Where(u => u.IsValid && u.IsAlive && u.IsAttackable
                                && step.TargetEntries.Contains((int)u.Entry)
                                && u.Position.DistanceTo(myPos) < GrindSearchRange)
                    .OrderBy(u => u.Position.DistanceTo(myPos))
                    .FirstOrDefault();
                if (mob != null)
                {
                    Fight.StartFight(mob.Guid);
                    return;
                }
            }

            // 2) Nearest objective GAMEOBJECT in range -> loot it.
            if (step.GoEntries != null && step.GoEntries.Count > 0)
            {
                WoWGameObject go = ObjectManager.GetObjectWoW()
                    .OfType<WoWGameObject>()
                    .Where(o => o.IsValid && step.GoEntries.Contains((int)o.Entry)
                                && o.Position.DistanceTo(myPos) < GrindSearchRange)
                    .OrderBy(o => o.Position.DistanceTo(myPos))
                    .FirstOrDefault();
                if (go != null)
                {
                    Logger.Log($"[DK Profile] Looting GO {go.Entry} for '{step.QuestName}'");
                    GoToTask.ToPositionAndIntecractWithGameObject(go.Position, (int)go.Entry);
                    // Some of these gather chests loot via a CHANNELED "Stealing" cast (e.g. 12716's "Empty Cauldron"
                    // 190937 / "Iron Chain" 190938, both gameobject_template type 3 with castBarCaption 'Stealing').
                    // If we pulse a fresh interact or roam off before the channel finishes it cancels and nothing is
                    // looted -> the chest never despawns and we loop on it forever (Talamin: idles 1.5y from the cauldron,
                    // never loots, even with no mobs). Stand still and wait the channel out so the loot registers.
                    Thread.Sleep(Usefuls.Latency + 400);
                    Usefuls.WaitIsCasting();
                    return;
                }
            }

            // 3) Nothing nearby -> roam to the next hotspot.
            List<Vector3> spots = step.GetPath();
            if (spots.Count == 0)
                return;
            if (_grindHotspotIndex >= spots.Count)
                _grindHotspotIndex = 0;

            Vector3 spot = spots[_grindHotspotIndex];
            if (myPos.DistanceTo(spot) < 10f)
            {
                _grindHotspotIndex = (_grindHotspotIndex + 1) % spots.Count;
                return;
            }
            if (!MovementManager.InMovement)
                MovementManager.Go(PathFinder.FindPath(spot));
        }

        // Restore WRobot's teleport-detection to whatever it was before we rode the transporter (no-op if never paused).
        private void RestoreTeleportGuard()
        {
            if (!_teleportGuardActive)
                return;
            // We've reached the destination level (this runs on the first gossip pulse after the teleport). Stop the
            // leftover path movement so WRobot doesn't run us back to the pad and re-trigger the ride, then restore
            // teleport-detection to whatever it was.
            MovementManager.StopMove();
            wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = _savedCloseIfPlayerTeleported;
            _teleportGuardActive = false;
            Logger.Log("[DK Profile] Arrived across - stopped residual movement and restored teleport-detection");
        }
    }
}
