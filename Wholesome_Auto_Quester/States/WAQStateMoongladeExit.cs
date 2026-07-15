using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Helpers;
using WholesomeToolbox;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using Timer = robotManager.Helpful.Timer;

namespace Wholesome_Auto_Quester.States
{
    /// <summary>
    /// Reliable way OUT of Moonglade. The regular flight-master taxi map (Sindrayl / Faustron) needs the outbound
    /// Moonglade flight path to be LEARNED, which a character that teleported in doesn't have - so the integrated flight
    /// master finds the route but can't actually take off, and the bot ends up walking toward the destination and dying
    /// to the entrance gauntlet (Talamin). The Cenarion Circle courtesy NPCs fly you out FOR FREE via a gossip, even
    /// without the path: Bunthen Plainswind (11798 -> Thunder Bluff) for the Horde, Silva Fil'naveth (11800 ->
    /// Darnassus) for the Alliance. Both stand safely in Nighthaven.
    ///
    /// So whenever we're in Moonglade with a task elsewhere, walk to the faction's Cenarion NPC and pick its flight
    /// gossip. Sits ABOVE the integrated flight-master + travel states (so we never taxi-map or walk out of Moonglade)
    /// but below combat/loot/regen. Once on the taxi it stands down and the flight is waited out; from the capital the
    /// normal flight master handles the rest of the trip.
    /// </summary>
    internal class WAQStateMoongladeExit : State, IWAQState
    {
        private readonly ITaskManager _taskManager;
        private readonly IContinentManager _continentManager;

        // Moonglade's world-map rectangle (map 1). We test the raw position against it instead of asking the continent
        // manager for the area id: Moonglade's rectangle overlaps its neighbours (Felwood/Winterspring), and
        // GetWorldMapAreaFromPoint can resolve an overlapping area first, so areaID==493 wrongly comes back false while
        // we're standing IN Moonglade (Talamin's log: the exit never fired). The box is reliable and cheap.
        private const float MgXMin = 6952f, MgXMax = 8491f, MgYMin = -3689f, MgYMax = -1381f;
        private static bool IsMoongladePoint(int mapId, Vector3 p) =>
            mapId == 1 && p.X >= MgXMin && p.X <= MgXMax && p.Y >= MgYMin && p.Y <= MgYMax;

        // Cenarion Circle courtesy-flight NPCs in Nighthaven (entries + spawns from the world DB).
        private const int HordeFlightNpc = 11798;    // Bunthen Plainswind -> Thunder Bluff
        private const int AllianceFlightNpc = 11800; // Silva Fil'naveth   -> Darnassus
        private static readonly Vector3 HordeFlightSpot = new Vector3(7785.46f, -2403.46f, 489.626f);
        private static readonly Vector3 AllianceFlightSpot = new Vector3(7795.36f, -2400.29f, 489.52f);

        private readonly Timer _throttle = new Timer();

        public override string DisplayName { get; set; } = "WAQ Moonglade Exit";

        public WAQStateMoongladeExit(ITaskManager taskManager, IContinentManager continentManager)
        {
            _taskManager = taskManager;
            _continentManager = continentManager;
        }

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid
                    || ObjectManager.Me.WowClass != WoWClass.Druid // the Cenarion courtesy flight is DRUID-ONLY gossip
                    || ObjectManager.Me.IsOnTaxi
                    || !_throttle.IsReady)
                    return false;

                // Only when we're IN Moonglade and the current objective is somewhere else.
                if (!IsMoongladePoint(Usefuls.ContinentId, ObjectManager.Me.Position))
                    return false;

                IWAQTask task = _taskManager.ActiveTask;
                if (task?.WorldMapArea == null || IsMoongladePoint(task.WorldMapArea.mapID, task.Location))
                    return false;

                DisplayName = $"Moonglade: fly out to {(WTPlayer.IsHorde() ? "Thunder Bluff" : "Darnassus")}";
                return true;
            }
        }

        public override void Run()
        {
            _throttle.Reset(10000); // GoToTask below walks to the NPC + gossips; pace retries if it doesn't take off

            // Stand the integrated flight master down for the Moonglade exit: it wants to taxi-map out via Sindrayl/
            // Faustron (needs the learned outbound path), which is exactly what doesn't work here. Clearing its flags
            // also stops it trying to fly back to the (now far) Moonglade node once we've landed in the capital.
            Bot.FlightMasterManagement.FlightMasterManager.shouldTakeFlight = false;
            Bot.FlightMasterManagement.FlightMasterManager.from = null;
            Bot.FlightMasterManagement.FlightMasterManager.to = null;

            MountTask.DismountMount(); // get off any ground mount before boarding (no-op if not mounted)

            bool horde = WTPlayer.IsHorde();
            int npc = horde ? HordeFlightNpc : AllianceFlightNpc;
            Vector3 spot = horde ? HordeFlightSpot : AllianceFlightSpot;

            Logger.Log($"[Moonglade] Flying out via {(horde ? "Bunthen Plainswind" : "Silva Fil'naveth")} (free Cenarion flight)");
            GoToTask.ToPositionAndIntecractWithNpc(spot, npc, 1); // gossip option 1 = the flight (same as the DK taxis)
        }
    }
}
