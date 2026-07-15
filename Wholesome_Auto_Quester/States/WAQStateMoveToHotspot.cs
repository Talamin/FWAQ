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
        private IWAQTask _lastPathedTask;                                       // the task the current Go was pathed for
        private robotManager.Helpful.Timer _repathCheckTimer = new robotManager.Helpful.Timer();
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

            // (Re)path only when actually needed: movement ended, the ACTIVE TASK changed, or a throttled sanity
            // check shows the current path isn't heading to this task anymore. The old guard compared the LIVE
            // path end against task.Location in 3D with a 5yd tolerance - at ridge/cliff targets the navmesh end
            // node alternates between stacked surfaces (>5yd apart in Z), so it re-pathed (with a visible
            // StopMove) on every pulse until arrival. Task identity + 2D distance + a 10s throttle keep every
            // legitimate re-path trigger while killing that loop.
            bool needsPath = !MovementManager.InMovement || task != _lastPathedTask;
            if (!needsPath && _repathCheckTimer.IsReady
                && MovementManager.CurrentPath.Count > 0
                && MovementManager.CurrentPath.Last().DistanceTo2D(task.Location) > 10f)
            {
                needsPath = true;   // current movement goes somewhere else entirely (stale path) -> replan
            }

            if (needsPath && task.Location.DistanceTo(ObjectManager.Me.Position) > task.SearchRadius)
            {
                Logger.Log($"Moving to hotspot for {task.TaskName}");
                if (task.Location.DistanceTo(ObjectManager.Me.Position) > 50)
                {
                    MovementManager.StopMove();
                }
                List<Vector3> pathToTask = PathFinder.FindPath(task.Location);
                MovementManager.Go(pathToTask);
                _lastPathedTask = task;
                _repathCheckTimer = new robotManager.Helpful.Timer(10 * 1000);
            }
        }
    }
}