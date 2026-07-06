namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
using System.Linq;
using System.Threading;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

public class WFMMoveInteract
{
    private static void ResetFlags()
    {
        FlightMasterManager.from = null;
        FlightMasterManager.to = null;
        FlightMasterManager.flightMasterToDiscover = null;
        FlightMasterManager.shouldTakeFlight = false;
    }

    public static bool GoInteractwithFM(FlightMaster fm, bool openMapRequired = false)
    {
        if (GoToTask.ToPosition(fm.Position, 5f))
        {
            if (!FlightMasterManager.isLaunched 
                || !Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                || ObjectManager.Me.Position.DistanceTo(fm.Position) > 10f)
                return false;

            // We're in fight (possibly on mount)
            if (ObjectManager.Me.InCombatFlagOnly)
            {
                if (ObjectManager.Me.IsMounted)
                    MountTask.DismountMount();
                return false;
            }

            // We have reached the FM
            if (ObjectManager.Me.IsMounted)
                MountTask.DismountMount();

            // Check if FM is here or dead
            WoWUnit nearbyFM = FindNearbyAliveFM(fm);
            if (nearbyFM == null)
            {
                fm.Disable("FlightMaster is absent or dead.");
                ResetFlags();
                return false;
            }

            // Approach FM
            if (!GoToTask.ToPosition(nearbyFM.Position, 1f))
                return false;
                
            Interact.InteractGameObject(ObjectManager.GetWoWUnitByEntry(fm.NPCId).First().GetBaseAddress);

            // Check if interaction successful
            if (!InteractWithFm(fm))
            {
                fm.Disable("Unable to interact with NPC");
                ResetFlags();
                return false;
            }

            Usefuls.SelectGossipOption(GossipOptionsType.taxi);

            // Check if map open
            if (openMapRequired && !FmMapIsOpen(fm))
            {
                fm.Disable("Unable to open FM map");
                ResetFlags();
                return false;
            }

            return true;
        }
        // We haven't reach the FM yet
        return false;
    }

    private static bool FmMapIsOpen(FlightMaster fm)
    {
        Usefuls.SelectGossipOption(GossipOptionsType.taxi);
        for (int i = 1; i <= 5; i++)
        {
            if (!FlightMasterManager.isFMMapOpen)
            {
                WFMLogger.LogDebug($"Failed to open FM map, retrying ({i}/5)");
                Lua.LuaDoString("CloseGossip()");
                Thread.Sleep(500);
                if (InteractWithFm(fm))
                {
                    Usefuls.SelectGossipOption(GossipOptionsType.taxi);
                    Thread.Sleep(500);
                }
            }
            else
                return true;
        }
        return false;
    }

    private static bool InteractWithFm(FlightMaster fm)
    {
        Interact.InteractGameObject(ObjectManager.GetWoWUnitByEntry(fm.NPCId).First().GetBaseAddress);
        for (int i = 1; i <= 5; i++)
        {
            if (ObjectManager.Target.Entry != fm.NPCId)
            {
                WFMLogger.Log($"Failed to interact with NPC, retrying ({i}/5)");
                Interact.InteractGameObject(ObjectManager.GetWoWUnitByEntry(fm.NPCId).First().GetBaseAddress);
                Thread.Sleep(500);
            }
            else
                return true;
        }
        return false;
    }

    private static WoWUnit FindNearbyAliveFM(FlightMaster fm)
    {
        for (int i = 1; i <= 3; i++)
        {
            WoWUnit nearbyFm = ObjectManager.GetObjectWoWUnit().Find(unit => unit.Entry == fm.NPCId && unit.IsAlive);
            if (nearbyFm != null)
                return nearbyFm;
            else
            {
                WFMLogger.Log($"FM detection failed, retrying ({i}/3)");
                Thread.Sleep(1000);
            }
        }
        return null;
    }
}

}

