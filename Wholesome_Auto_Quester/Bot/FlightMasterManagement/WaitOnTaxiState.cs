namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
using robotManager.FiniteStateMachine;
using System.Threading;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

public class WaitOnTaxiState : State
{
    public override string DisplayName => "WFM Waiting on Taxi";

    public override bool NeedToRun
    {
        get
        {
            if (Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                && FlightMasterManager.isLaunched
                && ObjectManager.Me.IsOnTaxi
                && WFMToolBox.CountItemStacks("Seaforium PU-36 Explosive Nether Modulator") == 0
                && WFMToolBox.CountItemStacks("Area 52 Special") == 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
    }

    public override void Run()
    {
        Thread.Sleep(1000);
        MovementManager.StopMove();
    }
}

}

