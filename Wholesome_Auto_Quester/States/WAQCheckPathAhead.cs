using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Helpers;
using WholesomeToolbox;
using wManager;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    internal class WAQCheckPathAhead : State, IWAQState
    {
        public override string DisplayName { get; set; } = "WAQ Clearing path";
        private IWowObjectScanner _scanner;
        private List<TraceLineResult> _losCache = new List<TraceLineResult>();
        private readonly float _detectionRadius = 35f;
        private readonly float _detecttionPathDistance = 30f;
        public (WoWUnit unit, float pathDistance) UnitOnPath = (null, 0);
        public (Vector3 a, Vector3 b) DangerTraceline = (null, null);
        public List<Vector3> LinesToCheck = new List<Vector3>();
        public List<Vector3> PointsAlongPathSegments = new List<Vector3>();

        // The traceline + pathfind sweep is expensive and used to run on EVERY 10 ms engine tick while
        // travelling (avg ~12 ms, spikes to ~600 ms = the felt stutter). Throttle it so the heavy work runs a
        // few times a second; between runs NeedToRun only re-validates the cached danger (cheap).
        private const int AnalysisIntervalMs = 250;
        // Mounted travel sweeps at a lower rate: it used to not sweep AT ALL while mounted far from the
        // destination, which is the perf-friendly choice on open roads - but riding blind INTO a mob pack
        // (cave entrances, camps: the "Never Again" cave) means getting dazed/dismounted deep inside with a
        // train behind. Mounted speed ~14y/s + 50y look-ahead leaves plenty of margin at 750ms.
        private const int MountedAnalysisIntervalMs = 750;
        // A blocker only interrupts MOUNTED travel when it is part of a PACK (>= this many fightable hostiles
        // within PackRadius of it). Lone roadside mobs are ridden past like before - that is the point of being
        // mounted; packs are cleared like on foot instead of dragged into the cave.
        private const int MountedPackMinCount = 2;
        private const float MountedPackRadius = 15f;
        // Cap how many hostiles a single sweep pathfinds against, so one analysis stays bounded even in a
        // mob-dense area (each hostile can cost a traceline + pathfind). The list is distance-sorted, so this
        // keeps the nearest ones (the actual path-blockers).
        private const int MaxHostilesPerCheck = 8;
        private Timer _analysisTimer = new Timer();
        private (WoWUnit unit, float pathDistance) _cachedUnitOnPath = (null, 0);

        private readonly HashSet<int> _mobIdsToIgnoreDuringPathCheck = new HashSet<int>()
        {
            17578, // Hellfire Training Dummy
            13177, // Vahgruk
            3708, // Gruna
        };

        public WAQCheckPathAhead(IWowObjectScanner scanner)
        {
            _scanner = scanner;
        }

        public override bool NeedToRun
        {
            get
            {
                // Cheap guards run every tick on the engine thread.
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid
                    || ObjectManager.Me.InCombatFlagOnly
                    || Fight.InFight
                    || MovementManager.CurrentPath == null
                    || MovementManager.CurrentPath.Count <= 1
                    || (!MovementManager.InMoveTo && !MovementManager.InMovement)
                    || ObjectManager.Me.GetDurabilityPercent < 20)
                {
                    _cachedUnitOnPath = (null, 0);
                    DangerTraceline = (null, null);
                    UnitOnPath = (null, 0);
                    return false;
                }

                // Mounted far from the destination = cruise mode. We still sweep (at the slower mounted rate),
                // but only a PACK on the path interrupts the ride (see MountedPackMinCount); within 120y of the
                // destination we clear like on foot, as before.
                bool mountedCruise = ObjectManager.Me.IsMounted
                    && MovementManager.CurrentPath.Last().DistanceTo(ObjectManager.Me.Position) > 120;

                // Heavy work (tracelines + pathfinds) only a few times per second, not every tick. The gap is
                // re-armed AFTER the sweep, so a slow sweep in a mob-dense area can't run back-to-back and eat
                // the engine thread (it used to: the sweep outran the 200 ms timer that was armed at its start).
                if (_analysisTimer.IsReady)
                {
                    LinesToCheck = GetFrontLinesOnPath(MovementManager.CurrentPath);
                    List<WoWUnit> hostiles = ToolBox.GetListObjManagerHostiles()
                        .Where(unit => !wManagerSetting.IsBlackListedZone(unit.Position))
                        .Take(MaxHostilesPerCheck)
                        .ToList();
                    _cachedUnitOnPath = EnemyAlongTheLine(LinesToCheck, hostiles);
                    _analysisTimer = new Timer(mountedCruise ? MountedAnalysisIntervalMs : AnalysisIntervalMs);
                }

                // Re-validate the cached danger cheaply every tick (it can die / despawn between analyses).
                WoWUnit unit = _cachedUnitOnPath.unit;
                if (unit == null || !unit.IsValid || !unit.IsAlive)
                {
                    UnitOnPath = (null, 0);
                    return false;
                }

                // Mounted cruise: ride past lone roadside mobs (the whole point of mounting), but never ride
                // INTO a pack - being dazed/dismounted inside a cave/camp with a train behind is how the
                // "Never Again" runs died. Pack = blocker + at least one more fightable hostile close to it.
                if (mountedCruise && !IsPack(unit))
                {
                    UnitOnPath = (null, 0);
                    return false;
                }

                UnitOnPath = _cachedUnitOnPath;
                return true;
            }
        }

        public override void Run()
        {
            WoWUnit unitToClear = UnitOnPath.unit;
            if (unitToClear == null) return;
            DisplayName = $"WAQ Clearing path against {unitToClear.Name}";
            Logger.Log($"WAQ Clearing path against {unitToClear.Name}");
            MovementManager.StopMove();
            MovementManager.StopMoveNewThread();
            MountTask.DismountMount(); // clear a pack ON FOOT (no-op when not mounted)
            Fight.StartFight(unitToClear.Guid);
            UnitOnPath = (null, 0);
            _cachedUnitOnPath = (null, 0);
        }

        // At least MountedPackMinCount fightable hostiles (the blocker included) within MountedPackRadius of the
        // blocker = a pack we must not ride into.
        private bool IsPack(WoWUnit blocker)
        {
            Vector3 blockerPos = blocker.PositionWithoutType;
            return ToolBox.GetListObjManagerHostiles()
                .Count(u => u.PositionWithoutType.DistanceTo(blockerPos) <= MountedPackRadius) >= MountedPackMinCount;
        }

        private (WoWUnit unit, float pathDistance) EnemyAlongTheLine(List<Vector3> path, List<WoWUnit> hostileUnits)
        {
            PointsAlongPathSegments = ToolBox.GetPointsAlongPath(path, 3f, float.MaxValue);
            List<ulong> unreachableMobsGuid = new List<ulong>();
            float pathToUnitLength = 0;

            for (int i = 0; i < PointsAlongPathSegments.Count - 1; i++)
            {
                Vector3 segmentStart = PointsAlongPathSegments[i];
                Vector3 segmentEnd = PointsAlongPathSegments[i + 1];
                float segmentLength = segmentStart.DistanceTo(segmentEnd);

                // check if units have LoS/path from point
                foreach (WoWUnit unit in hostileUnits)
                {
                    if (((int)unit.Reaction) > 2
                        || _mobIdsToIgnoreDuringPathCheck.Contains(unit.Entry)
                        || unreachableMobsGuid.Contains(unit.Guid)
                        || unit.PositionWithoutType.DistanceTo(ObjectManager.Me.PositionWithoutType) > _detectionRadius // in radius?
                        || pathToUnitLength + segmentStart.DistanceTo(unit.PositionWithoutType) > _detecttionPathDistance // not too far?
                        || WTPathFinder.PointDistanceToLine(segmentStart, segmentEnd, unit.PositionWithoutType) > 20)
                    {
                        continue;
                    }

                    // Check if we already have a positive result for this unit in the cache
                    TraceLineResult positiveUnitLoS = _losCache.Where(result =>
                            result.Unit.Guid == unit.Guid
                            && result.IsVisibleAndReachable
                            && unit.PositionWithoutType.DistanceTo(result.End) < 3f // double check for patrols
                            && segmentLength + result.Distance < _detecttionPathDistance)
                        .FirstOrDefault();
                    if (positiveUnitLoS != null)
                    {
                        DangerTraceline = (segmentStart, positiveUnitLoS.Unit.PositionWithoutType);
                        return (positiveUnitLoS.Unit, segmentLength + positiveUnitLoS.Distance);
                    }

                    // Check the cache for any result for this traceline, cache it if not existant
                    TraceLineResult losResult = _losCache
                        .Where(tsResult => tsResult.Start.DistanceTo(segmentStart) < 3f && tsResult.End.DistanceTo(unit.PositionWithoutType) < 3f)
                        .FirstOrDefault();
                    if (losResult == null)
                    {
                        losResult = new TraceLineResult(segmentStart, unit.PositionWithoutType, unit);
                        _losCache.Add(losResult);

                        if (losResult.PathLength <= 0 && !unreachableMobsGuid.Contains(unit.Guid))
                        {
                            unreachableMobsGuid.Add(unit.Guid);
                        }

                        if (_losCache.Count > 100)
                        {
                            _losCache.RemoveRange(0, 20);
                        }
                    }

                    if (losResult.IsVisibleAndReachable)
                    {
                        pathToUnitLength += losResult.PathLength;
                        if (pathToUnitLength < _detecttionPathDistance)
                        {
                            DangerTraceline = (segmentStart, unit.PositionWithoutType);
                            return (unit, pathToUnitLength);
                        }
                    }
                }

                pathToUnitLength += segmentLength;
            }

            return (null, 0);
        }

        private List<Vector3> GetFrontLinesOnPath(List<Vector3> path, int maxDistance = 50)
        {
            List<Vector3> result = new List<Vector3>();
            Vector3 myNextNode = MovementManager.CurrentMoveTo;
            if (!path.Contains(myNextNode))
            {
                return result;
            }
            List<Vector3> adjustedPath = new List<Vector3>();
            Vector3 myPos = ObjectManager.Me.Position;
            int myNextNodeIndex = path.IndexOf(myNextNode);
            adjustedPath.Add(myPos);
            adjustedPath.AddRange(path.GetRange(myNextNodeIndex, path.Count - myNextNodeIndex));
            float lineToCheckDistance = 0;
            result.Add(myPos);

            for (int i = 0; i < adjustedPath.Count - 1; i++)
            {
                // Ignore if too far
                if (result.Count > 2 && lineToCheckDistance > maxDistance)
                {
                    break;
                }

                Vector3 nextNode = adjustedPath[i + 1];

                result.Add(nextNode);
                lineToCheckDistance += adjustedPath[i].DistanceTo(nextNode);
            }

            return result;
        }

        private class TraceLineResult
        {
            public Vector3 Start;
            public Vector3 End;
            public bool HasLoS;
            public List<Vector3> Path;
            public float PathLength;
            public float Distance;
            public WoWUnit Unit;

            public TraceLineResult(Vector3 start, Vector3 end, WoWUnit unit)
            {
                Unit = unit;
                Start = start;
                End = end;
                HasLoS = !TraceLine.TraceLineGo(start, end, CGWorldFrameHitFlags.HitTestSpellLoS | CGWorldFrameHitFlags.HitTestLOS);
                //HasLoS = Toolbox.CheckLos(start, end, CGWorldFrameHitFlags.HitTestSpellLoS | CGWorldFrameHitFlags.HitTestLOS);
                if (!HasLoS)
                    return;
                Path = PathFinder.FindPath(start, end, out bool resultSuccess, skipIfPartiel: true);
                PathLength = resultSuccess ? WTPathFinder.CalculatePathTotalDistance(Path) : 0;
                Distance = start.DistanceTo(end);
            }

            public bool IsVisibleAndReachable => PathLength > 0
                && HasLoS
                && PathLength < Distance * 2;
        }
    }
}
