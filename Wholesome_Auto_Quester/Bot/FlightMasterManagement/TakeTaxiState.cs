namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
using robotManager.FiniteStateMachine;
using System.Collections.Generic;
using System.Threading;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using WholesomeToolbox;

public class TakeTaxiState : State
{
    public override string DisplayName => "WFM Taking Taxi";

    public override bool NeedToRun
    {
        get
        {
            if (Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                && FlightMasterManager.shouldTakeFlight
                && FlightMasterManager.to != null
                && FlightMasterManager.from != null
                && !ObjectManager.Me.InTransport
                && !FlightMasterManager.from.IsDisabledByPlugin()
                && WFMToolBox.ExceptionConditionsAreMet(FlightMasterManager.from)
                && !ObjectManager.Me.IsOnTaxi)
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
        MovementManager.StopMoveNewThread();
        MovementManager.StopMoveToNewThread();

        FlightMaster flightmasterFrom = FlightMasterManager.from;
        FlightMaster flightmasterTo = FlightMasterManager.to;

        if (WFMMoveInteract.GoInteractwithFM(flightmasterFrom, true))
        {
            if (FlightMasterDB.UpdateKnownFMs(flightmasterFrom))
            {
                WFMLogger.Log("Flightmaster list has changed. Trying to find a new path.");
                FlightMasterManager.to = null;
                FlightMasterManager.shouldTakeFlight = false;
                return;
            }

            List<string> reachableTaxis = new List<string>();
            // Look for current To and record reachables in case we can't find it
            for (int i = 0; i < 120; i++)
            {
                string nodeStatus = WTTaxi.GetTaxiNodeType(i);
                string nodeName = WTTaxi.GetTaxiNodeName(i);

                if (nodeStatus == "REACHABLE")
                {
                    if (nodeName == flightmasterTo.Name)
                    {
                        TakeTaxi(flightmasterFrom, nodeName);
                        return;
                    }
                    reachableTaxis.Add(nodeName);
                }
            }

            // Find an alternative
            WFMLogger.Log($"{flightmasterTo.Name} is unreachable, trying to find an alternative");
            FlightMaster alternativeFm = FlightMasterManager.GetBestAlternativeTo(reachableTaxis);
            if (alternativeFm != null)
            {
                WFMLogger.Log($"Found an alternative flight : {alternativeFm.Name}");
                TakeTaxi(flightmasterFrom, alternativeFm.Name);
                return;
            }
            else
            {
                FlightMasterManager.shouldTakeFlight = false;
                WFMToolBox.PausePlugin("Couldn't find an alternative flight");
            }
        }
    }

    private void TakeTaxi(FlightMaster fm, string taxiNodeName)
    {
        WTTaxi.TakeTaxi(taxiNodeName);
        Thread.Sleep(500);

        // 5 tries to click on node if it failed
        for (int i = 1; i <= 5; i++)
        {
            if (ObjectManager.Me.IsCast)
            {
                Usefuls.WaitIsCasting();
                i = 1;
                WFMLogger.Log("You're casting, wait");
                continue;
            }

            if (ObjectManager.Me.IsOnTaxi || FlightMasterManager.inPause)
            {
                break;
            }
            else
            {
                WFMLogger.Log($"Taking taxi failed. Retrying ({i}/5)");
                Lua.LuaDoString($"CloseTaxiMap(); CloseGossip();");
                FlightMasterManager.errorTooFarAwayFromTaxiStand = false;
                Thread.Sleep(500);
                if (WFMMoveInteract.GoInteractwithFM(fm))
                {
                    Thread.Sleep(500);
                }
                Usefuls.SelectGossipOption(GossipOptionsType.taxi);
                Thread.Sleep(500);
                WTTaxi.TakeTaxi(taxiNodeName);
                Thread.Sleep(500);
            }
        }

        if (FlightMasterManager.inPause)
        {
            return;
        }

        if (FlightMasterManager.errorTooFarAwayFromTaxiStand)
        {
            WFMToolBox.PausePlugin("Taking taxi failed (error clicking node)");
        }
        else
        {
            WFMLogger.Log($"Flying to {taxiNodeName}");
        }

        Thread.Sleep(Usefuls.Latency + 500);
        FlightMasterManager.shouldTakeFlight = false;
        FlightMasterManager.errorTooFarAwayFromTaxiStand = false;
        Thread.Sleep(Usefuls.Latency + 500);

        if (!ObjectManager.Me.IsOnTaxi)
        {
            WFMToolBox.PausePlugin("Taking taxi failed");
        }
    }
}

}

