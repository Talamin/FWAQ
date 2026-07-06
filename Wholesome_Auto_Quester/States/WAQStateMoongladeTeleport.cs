using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Threading;
using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Helpers;
using wManager;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using Timer = robotManager.Helpful.Timer;

namespace Wholesome_Auto_Quester.States
{
    /// <summary>
    /// Druid-only shortcut INTO Moonglade. Moonglade (map 1, area 493) is a mountain-locked zone whose only land
    /// approach runs a gauntlet of high-level mobs that would kill a leveling character, so a Druid heading there for
    /// a quest casts "Teleport: Moonglade" (18960) - the free, one-cast trip straight into Nighthaven - instead of
    /// trying to walk in. Non-druids (or a druid that hasn't learned the spell yet) fall through to normal travel.
    ///
    /// This handles ENTERING only. LEAVING Moonglade is deliberately left to the flight-master travel system (the
    /// Nighthaven flight master flies you out from safely inside the town), since flying is likewise the only safe
    /// way past the entrance gauntlet - Talamin.
    ///
    /// Sits above the flight-master / travel states so the teleport wins over any attempt to walk or taxi in; combat /
    /// loot / regen still interrupt it.
    /// </summary>
    internal class WAQStateMoongladeTeleport : State, IWAQState
    {
        private readonly ITaskManager _taskManager;
        private readonly IContinentManager _continentManager;

        private const int MoongladeAreaId = 493;              // Moonglade zone (world-map-area areaID / AreaTable id)
        private const uint TeleportMoongladeSpellId = 18960;  // Druid "Teleport: Moonglade"

        // Don't hammer the cast if something blocks it (indoors, on cooldown, ...); a full attempt blocks ~cast + the
        // loading screen anyway, so this is just a floor between failed attempts.
        private readonly Timer _throttle = new Timer();

        public override string DisplayName { get; set; } = "WAQ Moonglade Teleport";

        public WAQStateMoongladeTeleport(ITaskManager taskManager, IContinentManager continentManager)
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
                    || ObjectManager.Me.InCombat
                    || ObjectManager.Me.IsOnTaxi
                    || !_throttle.IsReady
                    || ObjectManager.Me.WowClass != WoWClass.Druid
                    || !SpellManager.KnowSpell(TeleportMoongladeSpellId))
                    return false;

                IWAQTask task = _taskManager.ActiveTask;
                if (task?.WorldMapArea == null)
                    return false;

                // Fire only when the current objective is IN Moonglade and we are not already there.
                if (task.WorldMapArea.areaID != MoongladeAreaId
                    || _continentManager.MyMapArea?.areaID == MoongladeAreaId)
                    return false;

                DisplayName = "Moonglade: Teleport in";
                return true;
            }
        }

        public override void Run()
        {
            _throttle.Reset(15000);
            MovementManager.StopMove();
            if (ObjectManager.Me.IsMounted)
                MountTask.DismountMount(); // can't cast Teleport while mounted

            // The teleport is a map change (a big position jump), so pause WRobot's "player teleported" stop-guard
            // around it, then restore it once we've re-based after the loading screen.
            bool savedGuard = wManagerSetting.CurrentSetting.CloseIfPlayerTeleported;
            wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = false;
            try
            {
                Logger.Log("[Moonglade] Casting Teleport: Moonglade");
                SpellManager.CastSpellByIdLUA(TeleportMoongladeSpellId);
                Usefuls.WaitIsCasting();

                // Block out the loading screen: wait until we've actually arrived in Moonglade (or give up after ~20s),
                // so the FSM doesn't re-evaluate mid-teleport.
                Timer arrival = new Timer(20000);
                while (!arrival.IsReady
                       && Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                       && _continentManager.MyMapArea?.areaID != MoongladeAreaId)
                    Thread.Sleep(500);

                Thread.Sleep(1500); // let WRobot re-baseline its position after the loading screen before restoring
            }
            finally
            {
                wManagerSetting.CurrentSetting.CloseIfPlayerTeleported = savedGuard;
            }

            if (_continentManager.MyMapArea?.areaID == MoongladeAreaId)
                Logger.Log("[Moonglade] Arrived in Nighthaven");
        }
    }
}
