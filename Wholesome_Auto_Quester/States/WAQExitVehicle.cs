using robotManager.FiniteStateMachine;
using System.Threading;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using Timer = robotManager.Helpful.Timer;

namespace Wholesome_Auto_Quester.States
{
    class WAQExitVehicle : State, IWAQState
    {
        private Timer _stateTimer = new Timer();

        public override string DisplayName { get; set; } = "WAQ Exit Vehicle";

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid
                    || !_stateTimer.IsReady)
                    return false;

                _stateTimer = new Timer(3000);

                if (!ObjectManager.Me.IsOnTaxi && ObjectManager.Me.PlayerUsingVehicle)
                {
                    // A DK-profile step may deliberately own a vehicle (the stolen horse for Grand Theft Palomino, or
                    // the Eye of Acherus) - don't yank us out of it mid-quest.
                    if (WAQStateDeathKnightStart.VehicleWanted)
                        return false;
                    return true;
                }

                return false;
            }
        }

        public override void Run()
        {
            Thread.Sleep(1000);
            Logger.Log($"Exiting vehicle");
            Lua.LuaDoString("VehicleExit()");
        }
    }
}
