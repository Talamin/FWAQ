using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Collections.Generic;
using System.Linq;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Bot.TravelManagement;
using Wholesome_Auto_Quester.Helpers;
using Wholesome_Auto_Quester.Logic;
using wManager;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    class WAQStateMoveToHotspot : State, IWAQState
    {
        private ITaskManager _taskManager;
        private ITravelManager _travelManager;
        public override string DisplayName { get; set; } = "WAQ Move to hotspot";

        public WAQStateMoveToHotspot(ITaskManager taskManager, ITravelManager travelManager)
        {
            _taskManager = taskManager;
            _travelManager = travelManager;
        }

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || !ObjectManager.Me.IsValid)
                    return false;

                if (_taskManager.ActiveTask != null
                    && _taskManager.ActiveTask.IsValid
                    && !_travelManager.IsTravelRequired(_taskManager.ActiveTask))
                {
                    DisplayName = $"Moving to hotspot for {_taskManager.ActiveTask.TaskName}";
                    return true;
                }

                return false;
            }
        }

        public override void Run()
        {
            IWAQTask task = _taskManager.ActiveTask;

            if (task == null) return;

            if (wManagerSetting.IsBlackListedZone(task.Location))
            {
                task.PutTaskOnTimeout("Zone is blacklisted");
                MovementManager.StopMove();
                return;
            }

            if (task.Location.DistanceTo(ObjectManager.Me.Position) <= task.SearchRadius)
            {
                // Graduated recovery: short first window (the mob may be on respawn), escalating on repeats so we
                // don't churn back here every few minutes if it's genuinely gone.
                task.PutTaskOnTimeout("Couldn't find target", RecoveryPolicy.FirstTimeoutSeconds(TaskFailureKind.TargetNotFound), RecoveryPolicy.Escalates(TaskFailureKind.TargetNotFound));
            }

            ToolBox.CheckIfZReachable(task.Location);

            // Only (re)path when we're not already heading to (roughly) this hotspot. Comparing the path's last
            // node to task.Location by EXACT equality almost never matched (the navmesh end node is a few yards
            // off), so this used to FindPath to the same hotspot every single tick (~100 ms each). A small
            // tolerance fixes the per-tick pathfind.
            if (task.Location.DistanceTo(ObjectManager.Me.Position) > task.SearchRadius
                && (!MovementManager.InMovement
                || MovementManager.CurrentPath.Count > 0 && MovementManager.CurrentPath.Last().DistanceTo(task.Location) > 5f))
            {
                Logger.Log($"Moving to hotspot for {task.TaskName}");
                if (task.Location.DistanceTo(ObjectManager.Me.Position) > 50)
                {
                    MovementManager.StopMove();
                }
                List<Vector3> pathToTask = PathFinder.FindPath(task.Location);
                MovementManager.Go(pathToTask);
            }
        }
    }
}