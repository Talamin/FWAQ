using robotManager.Events;
using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using wManager;
using wManager.Events;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using Math = System.Math;

namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
    /// <summary>
    /// Integrated flight-master travel system for the WAQ product - ported from the standalone Wholesome-TBC-FlightMaster
    /// plugin (Main) so the product owns and controls the whole taxi logic instead of relying on a separate plugin (and
    /// no longer on the Dungeon Crawler's compiled flight-master states, which are a different product). It hooks
    /// <see cref="MovementEvents.OnMovementPulse"/>: when the product requests a long path, it looks for a taxi that
    /// shortcuts it, cancels the walk, and sets <see cref="shouldTakeFlight"/> so the take-taxi state (registered in the
    /// WAQ FSM) walks to the flight master and flies. A background pulse marks nearby unknown nodes for discovery.
    ///
    /// The plugin's lifecycle (IPlugin), auto-updater, settings WinForm and the runtime state-injection hack are dropped:
    /// the three states are registered directly in <c>WAQBot</c>, and Initialize/Dispose are driven by the product.
    /// </summary>
    public static class FlightMasterManager
    {
        public static bool isLaunched;
        public static FlightMaster nearestFlightMaster = null;
        public static FlightMaster flightMasterToDiscover = null;
        public static Vector3 destinationVector = null;
        private static State currentState = null;
        private static Thread pulseThread = null;

        public static bool inPause;
        public static Stopwatch pauseTimer = new Stopwatch();

        public static FlightMaster from = null;
        public static FlightMaster to = null;
        public static bool shouldTakeFlight = false;
        public static bool isHorde;
        public static bool isFMMapOpen;
        public static bool isGossipOpen;

        // Errors handling
        public static bool errorTooFarAwayFromTaxiStand = false;
        private static int stuckCount = 0;
        private static DateTime lastStuck = DateTime.Now;

        // Saved WRobot settings (so we can restore its built-in taxi on dispose)
        public static bool saveFlightMasterTaxiUse = false;
        public static bool saveFlightMasterTaxiUseOnlyIfNear = false;
        public static float saveFlightMasterDiscoverRange = 1;

        // The FSM states - registered by WAQBot, referenced here for the stuck/discovery hooks.
        public static readonly State discoverFlightMasterState = new DiscoverFlightMasterState();
        public static readonly State takeTaxiState = new TakeTaxiState();
        public static readonly State waitOnTaxiState = new WaitOnTaxiState();

        public static void Initialize()
        {
            if (isLaunched)
                return;

            isLaunched = true;
            isHorde = WFMToolBox.GetIsHorde();
            WFMSettings.Load();
            EnsureExternalPluginOff(); // this logic is now built in - kill the standalone plugin so it can't double-hook

            WFMLogger.Log("Integrated flight master starting");
            MovementManager.StopMoveNewThread();
            MovementManager.StopMoveToNewThread();

            FlightMasterDB.Initialize();
            WFMSetup.SetBlacklistedZonesAndOffMeshConnections();
            WFMSetup.DiscoverDefaultNodes();
            WFMSetup.SetWRobotSettings(); // disable WRobot's own taxi so it doesn't fight us

            pulseThread = new Thread(BackGroundPulse) { IsBackground = true, Name = "WAQ FlightMaster pulse" };
            pulseThread.Start();

            FiniteStateMachineEvents.OnRunState += StateEventHandler;
            MovementEvents.OnMovementPulse += MovementEventsOnMovementPulse;
            MovementEvents.OnSeemStuck += SeemStuckHandler;
            EventsLuaWithArgs.OnEventsLuaStringWithArgs += WFMToolBox.MessageHandler;

            EventsLua.AttachEventLua("TAXIMAP_OPENED", (e) => isFMMapOpen = true);
            EventsLua.AttachEventLua("TAXIMAP_CLOSED", (e) => isFMMapOpen = false);
            EventsLua.AttachEventLua("GOSSIP_SHOW", (e) => isGossipOpen = true);
            EventsLua.AttachEventLua("GOSSIP_CLOSED", (e) => isGossipOpen = false);
        }

        public static void Dispose()
        {
            if (!isLaunched)
                return;

            FiniteStateMachineEvents.OnRunState -= StateEventHandler;
            MovementEvents.OnMovementPulse -= MovementEventsOnMovementPulse;
            MovementEvents.OnSeemStuck -= SeemStuckHandler;
            EventsLuaWithArgs.OnEventsLuaStringWithArgs -= WFMToolBox.MessageHandler;

            isLaunched = false; // stops the background pulse loop
            WFMSetup.RestoreWRobotSettings();
            shouldTakeFlight = false;
            inPause = false;
            WFMLogger.Log("Integrated flight master disposed");
        }

        // The standalone Wholesome-TBC-FlightMaster plugin's logic is now built into this product. If the user still has
        // the plugin installed AND active, both it and this manager hook OnMovementPulse and fight over which taxi to
        // take. So whenever the plugin comes back on, flip its Actif flag off (in-memory only, NO Save - so it's simply
        // re-enabled on the next WRobot start if the user ever wants the standalone again) and reload the plugin set,
        // which drops the plugin. Cheap no-op once it's already off. Mirrors the DK profile's Wholesome_Vendors kill.
        private static void EnsureExternalPluginOff()
        {
            var plugin = wManagerSetting.CurrentSetting.PluginsSettings
                .FirstOrDefault(ps => ps.FileName != null && ps.FileName.Contains("FlightMaster"));
            if (plugin == null || !plugin.Actif)
                return; // already off (or not installed) -> nothing to do
            plugin.Actif = false;
            wManager.Plugin.PluginsManager.DisposeAllPlugins();
            wManager.Plugin.PluginsManager.LoadAllPlugins(); // reloads every Actif plugin EXCEPT the FlightMaster one
            WFMLogger.Log("Disabled the standalone Wholesome-TBC-FlightMaster plugin (its logic is now integrated in the product)");
        }

        private static void SeemStuckHandler()
        {
            if (DateTime.Now.Ticks / 10000000 - lastStuck.Ticks / 10000000 < 10
                && (currentState == discoverFlightMasterState || currentState == takeTaxiState))
            {
                stuckCount++;
                WFMLogger.Log($"You're stuck ({stuckCount}/10)");

                if (stuckCount > 9)
                {
                    if (currentState == discoverFlightMasterState)
                    {
                        MovementManager.StopMove();
                        nearestFlightMaster?.Disable("Unreachable");
                    }
                    if (currentState == takeTaxiState)
                    {
                        MovementManager.StopMove();
                        from?.Disable("Unreachable");
                    }
                }
            }
            else
                stuckCount = 0;

            lastStuck = DateTime.Now;
        }

        // Track the currently-running state (used by the stuck handler + the discovery "stop on tracks" hook). No state
        // injection here anymore - WAQBot registers the states in the FSM directly.
        private static void StateEventHandler(Engine engine, State state, CancelEventArgs canc)
        {
            currentState = state;
        }

        private static void BackGroundPulse()
        {
            while (isLaunched)
            {
                try
                {
                    EnsureExternalPluginOff(); // WRobot can re-enable it at product start after our first pass; re-kill it

                    if (inPause && pauseTimer.ElapsedMilliseconds > WFMSettings.CurrentSettings.PauseLengthInSeconds * 1000)
                    {
                        WFMLogger.Log($"{WFMSettings.CurrentSettings.PauseLengthInSeconds} seconds elapsed in pause");
                        WFMToolBox.UnPausePlugin();
                        MovementManager.StopMoveNewThread();
                        MovementManager.StopMoveToNewThread();
                    }

                    if (Conditions.InGameAndConnectedAndProductStartedNotInPause
                        && !ObjectManager.Me.InCombatFlagOnly
                        && !ObjectManager.Me.IsOnTaxi
                        && ObjectManager.Me.IsAlive)
                    {
                        nearestFlightMaster = GetNearestFlightMaster();

                        // Mark flightmaster as To be discovered
                        if (nearestFlightMaster != null
                            && !ObjectManager.Me.InTransport
                            && !nearestFlightMaster.IsDisabledByPlugin()
                            && WFMToolBox.ExceptionConditionsAreMet(nearestFlightMaster)
                            && !WFMSettings.CurrentSettings.KnownFlightsList.Contains(nearestFlightMaster.Name)
                            && WFMToolBox.CalculatePathTotalDistance(ObjectManager.Me.Position, nearestFlightMaster.Position) < WFMSettings.CurrentSettings.DetectTaxiDistance * 1.2)
                            flightMasterToDiscover = nearestFlightMaster;

                        // Hook for state locks and others
                        if (discoverFlightMasterState.NeedToRun && currentState?.Priority < discoverFlightMasterState.Priority)
                        {
                            WFMLogger.Log("Stop on tracks to ensure discovery");
                            MovementManager.StopMove();
                            MovementManager.StopMoveTo();
                            MovementManager.StopMoveNewThread();
                        }
                    }
                }
                catch (Exception arg)
                {
                    WFMLogger.LogError(string.Concat(arg));
                }
                Thread.Sleep(3000);
            }
        }

        private static FlightMaster GetNearestFlightMaster()
        {
            List<FlightMaster> orderedFMList = FlightMasterDB.FlightMasterList
                .FindAll(fm => ObjectManager.Me.Position.DistanceTo(fm.Position) < (double)WFMSettings.CurrentSettings.DetectTaxiDistance && WFMToolBox.FMIsOnMyContinent(fm))
                .OrderBy(fm => fm.Position.DistanceTo(ObjectManager.Me.Position))
                .ToList();

            return orderedFMList.Count > 0 ? orderedFMList.First() : null;
        }

        public static FlightMaster GetClosestFlightMasterFrom(float maxRadius)
        {
            FlightMaster result = null;

            List<FlightMaster> orderedListFM = FlightMasterDB.FlightMasterList
                .FindAll(fm => (fm.IsDiscovered || WFMSettings.CurrentSettings.TakeUndiscoveredTaxi)
                    && WFMToolBox.FMIsOnMyContinent(fm)
                    && WFMToolBox.ExceptionConditionsAreMet(fm)
                    && !fm.IsDisabledByPlugin())
                .OrderBy(fm => fm.Position.DistanceTo(ObjectManager.Me.Position)).ToList();

            foreach (FlightMaster flightMaster in orderedListFM)
            {
                if (flightMaster.Position.DistanceTo(ObjectManager.Me.Position) < maxRadius)
                {
                    float realDist = WFMToolBox.CalculatePathTotalDistance(ObjectManager.Me.Position, flightMaster.Position);
                    WFMLogger.Log($"[FROM] {flightMaster.Name} is {Math.Round(realDist)} yards away");
                    if (realDist < maxRadius)
                    {
                        maxRadius = realDist;
                        result = flightMaster;
                    }
                }
            }
            return result;
        }

        public static FlightMaster GetClosestFlightMasterTo(float maxRadius)
        {
            FlightMaster result = null;

            List<FlightMaster> orderedListFM = FlightMasterDB.FlightMasterList
                .FindAll(fm => fm.IsDiscovered
                    && fm.NPCId != from.NPCId
                    && WFMToolBox.FMIsOnMyContinent(fm))
                .OrderBy(fm => fm.Position.DistanceTo(destinationVector)).ToList();

            foreach (FlightMaster flightMaster in orderedListFM)
            {
                if (flightMaster.Position.DistanceTo(destinationVector) < maxRadius)
                {
                    float realDist = WFMToolBox.CalculatePathTotalDistance(flightMaster.Position, destinationVector);
                    WFMLogger.Log($"[TO] {flightMaster.Name} is {Math.Round(realDist)} yards away from destination");
                    if (realDist < maxRadius)
                    {
                        maxRadius = realDist;
                        result = flightMaster;
                    }
                }
            }
            return result;
        }

        // Requires FM map open
        public static FlightMaster GetBestAlternativeTo(List<string> reachableTaxis)
        {
            float num = ObjectManager.Me.Position.DistanceTo(destinationVector);
            FlightMaster resultFM = null;

            List<FlightMaster> orderedListFM = FlightMasterDB.FlightMasterList
                .FindAll(fm => reachableTaxis.Contains(fm.Name))
                .OrderBy(fm => fm.Position.DistanceTo(destinationVector)).ToList();

            foreach (FlightMaster flightMaster in orderedListFM)
            {
                if (flightMaster.Position.DistanceTo(destinationVector) < num)
                {
                    float realDist = WFMToolBox.CalculatePathTotalDistance(flightMaster.Position, destinationVector);
                    WFMLogger.Log($"[TO2] {flightMaster.Name} is {Math.Round(realDist)} yards away from destination");
                    if (realDist < num)
                    {
                        num = realDist;
                        resultFM = flightMaster;
                    }
                }
            }
            return resultFM;
        }

        private static void MovementEventsOnMovementPulse(List<Vector3> points, CancelEventArgs cancelable)
        {
            if (points.Count <= 0)
                return;

            if (shouldTakeFlight
                && points.Last() == destinationVector
                && !inPause)
            {
                if (shouldTakeFlight && (from.IsDisabledByPlugin() || to.IsDisabledByPlugin()))
                    shouldTakeFlight = false;
                WFMLogger.Log("Cancelled move to " + destinationVector);
                cancelable.Cancel = true;
            }

            if (!ObjectManager.Me.IsAlive || ObjectManager.Me.IsOnTaxi || shouldTakeFlight || !isLaunched || inPause)
                return;

            // If we have detected a potential FP travel
            float totalWalkingDistance = WFMToolBox.CalculatePathTotalDistance(ObjectManager.Me.Position, points.Last());

            // If the path is shorter than setting, we skip
            if (totalWalkingDistance < (double)WFMSettings.CurrentSettings.TaxiTriggerDistance)
                return;

            WFMLogger.Log($"{Math.Round(totalWalkingDistance)} yards path is longer than trigger setting {WFMSettings.CurrentSettings.TaxiTriggerDistance}. Searching for flights.");

            if (Logging.Status.Contains("Follow Path")
                && !Logging.Status.Contains("Resurrect")
                && totalWalkingDistance < (double)WFMSettings.CurrentSettings.SkipIfFollowPathDistance)
            {
                WFMLogger.Log($"Currently following path. {totalWalkingDistance} yards is smaller than trigger setting {WFMSettings.CurrentSettings.SkipIfFollowPathDistance} yards. Ignoring flights.");
                return;
            }

            destinationVector = points.Last();

            from = GetClosestFlightMasterFrom(totalWalkingDistance);

            if (from == null)
            {
                WFMLogger.Log("No FROM found");
                return;
            }

            double distanceToNearestFM = WFMToolBox.CalculatePathTotalDistance(ObjectManager.Me.Position, from.Position);
            double departureDistance = distanceToNearestFM + WFMSettings.CurrentSettings.MinimumDistanceSaving;

            to = GetClosestFlightMasterTo(from.Position.DistanceTo(destinationVector));

            if (from.Equals(to))
                to = null;

            // Calculate total real distance FROM/TO
            double processedDistance;
            if (to == null)
                processedDistance = totalWalkingDistance;
            else
                processedDistance = departureDistance + WFMToolBox.CalculatePathTotalDistance(to.Position, destinationVector);

            // If total real distance does not save any distance or is longer, try to find alternative
            if (processedDistance >= totalWalkingDistance || to == null)
            {
                if (to == null)
                    WFMLogger.Log($"No direct flight path, trying to find an alternative, please wait");
                else
                    WFMLogger.Log($"Flight from {from.Name} to {to.Name} would save {Math.Round(totalWalkingDistance - processedDistance + WFMSettings.CurrentSettings.MinimumDistanceSaving)} yards. You set a minimum of {WFMSettings.CurrentSettings.MinimumDistanceSaving} yards. Trying to find an alternative.");

                List<FlightMaster> orderedListFM = FlightMasterDB.FlightMasterList
                    .FindAll(fm => fm.IsDiscovered && fm.NPCId != from.NPCId && WFMToolBox.FMIsOnMyContinent(fm))
                    .OrderBy(fm => fm.Position.DistanceTo(destinationVector)).ToList();

                foreach (FlightMaster flightMaster in orderedListFM)
                {
                    if (flightMaster.Position.DistanceTo(destinationVector) + departureDistance < processedDistance)
                    {
                        double alternativeDistance = departureDistance + WFMToolBox.CalculatePathTotalDistance(flightMaster.Position, destinationVector);
                        WFMLogger.Log($"Alternative TO : {flightMaster.Name} ({Math.Round(alternativeDistance)} yards total)");
                        if (alternativeDistance < processedDistance)
                        {
                            processedDistance = alternativeDistance;
                            to = flightMaster;
                        }
                    }
                }
            }

            if (to != null
                && from != null
                && !from.Equals(to)
                && processedDistance <= totalWalkingDistance)
            {
                double realProcessedDistance = Math.Round(processedDistance - WFMSettings.CurrentSettings.MinimumDistanceSaving);
                WFMLogger.Log($"Flight found for {Math.Round(totalWalkingDistance)} yards path. Processed distance is {realProcessedDistance} yards. Taking Taxi from {from.Name} to {to.Name}. (You will save {Math.Round(totalWalkingDistance) - realProcessedDistance} yards)");
                MovementManager.StopMoveNewThread();
                MovementManager.StopMoveToNewThread();
                cancelable.Cancel = true;
                shouldTakeFlight = true;
            }
            else
            {
                WFMLogger.Log($"No relevant flight found for {Math.Round(totalWalkingDistance)} yards path");
            }
        }
    }
}
