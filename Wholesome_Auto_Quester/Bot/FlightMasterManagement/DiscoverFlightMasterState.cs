namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
using robotManager.FiniteStateMachine;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

public class DiscoverFlightMasterState : State
{
    public override string DisplayName => "WFM Discovering Flight Master";

    public override bool NeedToRun
    {
        get
        {
            if (Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                && FlightMasterManager.isLaunched
                && FlightMasterManager.flightMasterToDiscover != null
                && !FlightMasterManager.flightMasterToDiscover.IsDisabledByPlugin()
                && !ObjectManager.Me.InTransport)
                return true;
            else
                return false;
        }
    }

    public override void Run()
    {
        MovementManager.StopMoveNewThread();
        MovementManager.StopMoveToNewThread();
        FlightMaster fmToDiscover = FlightMasterManager.flightMasterToDiscover;
        WFMLogger.Log($"Discovering flight master {fmToDiscover.Name}");

        // We go to the position
        if (WFMMoveInteract.GoInteractwithFM(fmToDiscover))
        {
            FlightMasterDB.SetFlightMasterToKnown(fmToDiscover.NPCId);
            FlightMasterManager.flightMasterToDiscover = null;
            WFMToolBox.UnPausePlugin();
            FlightMasterManager.shouldTakeFlight = false;

            FlightMasterDB.UpdateKnownFMs(fmToDiscover);
            MovementManager.StopMove(); // reset path
        }
    }
}

}

