using robotManager.Helpful;
using robotManager.Products;
using System.Collections.Generic;
using System.Threading;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using static wManager.Wow.Helpers.PathFinder;

namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
    /// <summary>Helpers for the integrated flight-master system (ported from the plugin's ToolBox; renamed to avoid
    /// clashing with <see cref="Wholesome_Auto_Quester.Helpers.ToolBox"/>).</summary>
    public static class WFMToolBox
    {
        // BANNED points
        private static readonly Vector3 TBCenter = new Vector3(-1190.982f, 6.03807f, 165.4799f, "None");

        public static bool GetIsHorde()
        {
            if (ObjectManager.Me.Faction == 35)
            {
                WFMLogger.LogWarning("Wholesome FlightMaster won't work in GM Mode. Please turn GM mode off. " +
                    "Once WRobot is started in normal mode, you can turn back GM mode on during runtime.");
                Products.ProductStop();
            }
            bool isHorde = ObjectManager.Me.Faction == (uint)PlayerFactions.Orc
                || ObjectManager.Me.Faction == (uint)PlayerFactions.Tauren
                || ObjectManager.Me.Faction == (uint)PlayerFactions.Undead
                || ObjectManager.Me.Faction == (uint)PlayerFactions.BloodElf
                || ObjectManager.Me.Faction == (uint)PlayerFactions.Troll;
            return isHorde;
        }

        public static void SoftRestart()
        {
            Products.InPause = true;
            Thread.Sleep(100);
            Products.InPause = false;
        }

        public static void MessageHandler(string id, List<string> args)
        {
            if (FlightMasterManager.isLaunched)
            {
                string eventName = id.ToString();
                if (eventName == "UI_INFO_MESSAGE")
                {
                    if (args[0] == "There is no direct path to that destination!")
                        PausePlugin("Unconnected flight");
                    else if (args[0] == "You don't have enough money!")
                        PausePlugin("Not enough money");
                    else if (args[0] == "You are too far away from the taxi stand!")
                        FlightMasterManager.errorTooFarAwayFromTaxiStand = true;
                }
            }
        }

        public static void PausePlugin(string reason)
        {
            WFMLogger.Log($"Pausing flight master for {WFMSettings.CurrentSettings.PauseLengthInSeconds} seconds ({reason})");
            FlightMasterManager.pauseTimer.Restart();
            FlightMasterManager.inPause = true;
            FlightMasterManager.shouldTakeFlight = false;
            FlightMasterManager.flightMasterToDiscover = null;
        }

        public static void UnPausePlugin()
        {
            if (FlightMasterManager.inPause)
            {
                WFMLogger.Log("Unpausing flight master");
                FlightMasterManager.pauseTimer.Reset();
                FlightMasterManager.inPause = false;
            }
        }

        public static bool PlayerInBloodElfStartingZone()
        {
            string zone = Lua.LuaDoString<string>("return GetRealZoneText();");
            return zone == "Eversong Woods" || zone == "Ghostlands" || zone == "Silvermoon City";
        }

        public static bool PlayerInDraneiStartingZone()
        {
            string zone = Lua.LuaDoString<string>("return GetRealZoneText();");
            return zone == "Azuremyst Isle" || zone == "Bloodmyst Isle" || zone == "The Exodar";
        }

        public static bool FMIsOnMyContinent(FlightMaster fm)
        {
            if (PlayerInBloodElfStartingZone())
                return FMIsInBloodElfStartingZone(fm) || fm.Continent == ContinentId.Azeroth;

            if (PlayerInDraneiStartingZone())
                return FMIsInDraneiStartingZone(fm);

            return !FMIsInDraneiStartingZone(fm)
                && (fm.Continent == (ContinentId)Usefuls.ContinentId || (FMIsInBloodElfStartingZone(fm) && (ContinentId)Usefuls.ContinentId == ContinentId.Azeroth));
        }

        private static bool FMIsInBloodElfStartingZone(FlightMaster fm) =>
            fm.NPCId == 16189 || fm.NPCId == 16192 || fm.NPCId == 24851 || fm.NPCId == 26560;

        private static bool FMIsInDraneiStartingZone(FlightMaster fm) =>
            fm.NPCId == 17555 || fm.NPCId == 17554;

        public static bool ExceptionConditionsAreMet(FlightMaster fm)
        {
            return (fm != null
                && (fm.NPCId != 20234 || fm.Position.DistanceTo(ObjectManager.Me.Position) < 100) // Shatter Point, Hellfire Peninsula
                && (fm.NPCId != 19581 || GetReputation("The Aldor") > 0) // Altar of Sha'tar, Shadowmoon Valley
                && (fm.NPCId != 21766 || GetReputation("The Scryers") > 0) // Sanctum of the Stars, Shadowmoon Valley
                && (fm.NPCId != 30314 || Quest.GetLogQuestIsComplete(12896) || Quest.GetLogQuestIsComplete(12897)) // The Shadow Vault, Icecrown
                && (fm.NPCId != 28037 || Quest.GetLogQuestIsComplete(12523)) // Nesingwary Base Camp, Sholazar Basin
                && (fm.NPCId != 31069 || Quest.GetLogQuestIsComplete(13141)) // Crusaders' Pinnacle, Icecrown
                && (fm.NPCId != 32571 || Quest.GetLogQuestIsComplete(12956)) // Dun Nifflelem, The Storm Peaks
                && (fm.NPCId != 37915 || GetWoWVersion().Equals("3.3.5")) // The Bulwark, Tirisfal
                && (fm.NPCId != 37888 || GetWoWVersion().Equals("3.3.5")) // Thondoril River, Western Plaguelands
                && (fm.NPCId != 29480 || ObjectManager.Me.WowClass == WoWClass.DeathKnight) // Acherus: The Ebon Hold
            );
        }

        private static int GetReputation(string faction)
        {
            return Lua.LuaDoString<int>($@"
                for i=1, 25 do
                    local name, _, _, _, _, earnedValue, _, _, _, _, _, _, _ = GetFactionInfo(i);
                        if name == '{faction}' then
                            return earnedValue
                    end
                end");
        }

        public static string GetWoWVersion() =>
            Lua.LuaDoString<string>("v, b, d, t = GetBuildInfo(); return v");

        // Count the amount of the specified item stacks in your bags
        public static int CountItemStacks(string itemName) =>
            Lua.LuaDoString<int>("local count = GetItemCount('" + itemName + "'); return count");

        // Calculate real walking distance
        public static float CalculatePathTotalDistance(Vector3 from, Vector3 to)
        {
            float distance = 0.0f;
            List<Vector3> path = FindPath(from, to, false);

            for (int i = 0; i < path.Count - 1; ++i)
            {
                distance += path[i].DistanceTo2D(path[i + 1]);

                // FIX FOR TB JUMP OFF
                if (path[i].DistanceTo(TBCenter) < 400 && path[i + 1].DistanceTo(path[i]) > 200)
                {
                    WFMLogger.Log($"Jump off Thunder Bluff detected {path[i]} to {path[i + 1]}, Trying to find an alternative. Please wait.");
                    return 100000;
                }
            }
            return distance;
        }
    }
}
