using System;
using System.Collections.Generic;

namespace Wholesome_Auto_Quester.GUI
{
    /// <summary>
    /// A tiny typed settings model for the native overlay (mirrors the AIO3 fightclass overlay's Setting model so the
    /// overlay can generate dark, tabbed controls in the same style). Each setting WRAPS a property on
    /// <see cref="WholesomeAQSettings"/> via get/set lambdas and persists through an <c>onSaved</c> callback, so the
    /// overlay never reimplements the settings store — it edits the same <c>CurrentSetting</c> the rest of the bot reads.
    /// </summary>
    internal abstract class OverlaySetting
    {
        public string Label;
        public string Category;
        public string Description;
        protected readonly Action _onSaved; // persist after a change (e.g. CurrentSetting.Save)

        protected OverlaySetting(string label, string category, string description, Action onSaved)
        {
            Label = label;
            Category = category;
            Description = description;
            _onSaved = onSaved;
        }

        public abstract void Reset();
    }

    internal sealed class ToggleOverlaySetting : OverlaySetting
    {
        private readonly Func<bool> _get;
        private readonly Action<bool> _set;
        private readonly bool _default;

        public ToggleOverlaySetting(string label, string category, string description,
                                    Func<bool> get, Action<bool> set, Action onSaved)
            : base(label, category, description, onSaved)
        {
            _get = get; _set = set; _default = get();
        }

        public bool Value
        {
            get => _get();
            set { _set(value); _onSaved?.Invoke(); }
        }

        public override void Reset() => Value = _default;
    }

    internal sealed class IntOverlaySetting : OverlaySetting
    {
        private readonly Func<int> _get;
        private readonly Action<int> _set;
        private readonly int _default;
        public int Min, Max, Step;

        public IntOverlaySetting(string label, string category, string description,
                                 Func<int> get, Action<int> set, int min, int max, int step, Action onSaved)
            : base(label, category, description, onSaved)
        {
            _get = get; _set = set; Min = min; Max = max; Step = Math.Max(1, step); _default = get();
        }

        public int Value
        {
            get => _get();
            set { _set(Math.Max(Min, Math.Min(Max, value))); _onSaved?.Invoke(); }
        }

        public override void Reset() => Value = _default;
    }

    internal sealed class ChoiceOverlaySetting : OverlaySetting
    {
        private readonly Func<string> _get;
        private readonly Action<string> _set;
        private readonly string _default;
        public string[] Options;

        public ChoiceOverlaySetting(string label, string category, string description,
                                    Func<string> get, Action<string> set, string[] options, Action onSaved)
            : base(label, category, description, onSaved)
        {
            _get = get; _set = set; Options = options; _default = get();
        }

        public string Value
        {
            get => _get();
            set { _set(value); _onSaved?.Invoke(); }
        }

        public override void Reset() => Value = _default;
    }

    /// <summary>Builds the overlay's setting list from <see cref="WholesomeAQSettings.CurrentSetting"/>. Grouped into
    /// Category tabs (Leveling / Behavior / General). Dev-only flags (AllowStopWatch) and non-UI state (lists, window
    /// positions, dates) are intentionally omitted.</summary>
    internal static class QuesterOverlaySettings
    {
        public static IReadOnlyList<OverlaySetting> Build()
        {
            var s = WholesomeAQSettings.CurrentSetting;
            Action save = () => { try { s.Save(); } catch { } };
            var list = new List<OverlaySetting>();
            if (s == null) return list;

            // --- Leveling ---
            list.Add(new IntOverlaySetting("Quest level: up to +N above me", "Leveling",
                "Do quests up to this many levels ABOVE your level. Raise carefully — over-level mobs kill undergeared bots.",
                () => s.LevelDeltaPlus, v => s.LevelDeltaPlus = v, 0, 10, 1, save));
            list.Add(new IntOverlaySetting("Quest level: down to -N below me", "Leveling",
                "Do quests down to this many levels BELOW your level (lower = fewer low-XP quests).",
                () => s.LevelDeltaMinus, v => s.LevelDeltaMinus = v, 0, 15, 1, save));
            list.Add(new IntOverlaySetting("Stop at level", "Leveling",
                "Stop the bot when this character level is reached.",
                () => s.StopAtLevel, v => s.StopAtLevel = v, 1, 80, 1, save));
            list.Add(new ToggleOverlaySetting("Grind only (no quests)", "Leveling",
                "Only grind mobs — pick up / do no quests. Applies live (switches the planner immediately).",
                () => s.GrindOnly, v => s.GrindOnly = v, save));

            // --- Behavior ---
            list.Add(new ToggleOverlaySetting("Class quests", "Behavior",
                "Force class quests (totems, summons, ...) to top priority, even cross-continent. OFF = treat them as ordinary quests (no forcing); they still complete if picked up.",
                () => s.ClassQuestsEnabled, v => s.ClassQuestsEnabled = v, save));
            list.Add(new ToggleOverlaySetting("Continent travels", "Behavior",
                "Allow intercontinental travel (recommended ON).",
                () => s.ContinentTravel, v => s.ContinentTravel = v, save));
            list.Add(new ToggleOverlaySetting("Abandon unfit quests", "Behavior",
                "Drop deprecated / undoable quests (recommended ON).",
                () => s.AbandonUnfitQuests, v => s.AbandonUnfitQuests = v, save));
            list.Add(new ToggleOverlaySetting("Blacklist danger zones", "Behavior",
                "Avoid zones with a high density of hostile mobs.",
                () => s.BlacklistDangerousZones, v => s.BlacklistDangerousZones = v, save));
            list.Add(new ToggleOverlaySetting("Turbo loot", "Behavior",
                "Faster custom looting (may occasionally skip a loot).",
                () => s.TurboLoot, v => s.TurboLoot = v, save));
            list.Add(new ToggleOverlaySetting("Smooth move", "Behavior",
                "Smoother movement between points.",
                () => s.SmoothMove, v => s.SmoothMove = v, save));
            list.Add(new ToggleOverlaySetting("Record unreachables", "Behavior",
                "Remember objects that couldn't be reached, across sessions.",
                () => s.RecordUnreachables, v => s.RecordUnreachables = v, save));

            // --- General ---
            list.Add(new ToggleOverlaySetting("Quest tracker GUI", "General",
                "Show the advanced quest-tracker window.",
                () => s.ActivateQuestsGUI, v => s.ActivateQuestsGUI = v, save));
            list.Add(new ToggleOverlaySetting("Debug logging", "General",
                "Extra detail in the log (quest status reasons, etc.).",
                () => s.LogDebug, v => s.LogDebug = v, save));
            list.Add(new ToggleOverlaySetting("Dev mode", "General",
                "Developer diagnostics: the on-screen scanner/task radar + a one-time FSM state dump + JSON regen apply "
                + "when the bot is (re)STARTED (Stop then Start after toggling); verbose dev logging responds live. "
                + "(The auto-updater is disabled in this build regardless.) Only if you know what you're doing.",
                () => s.DevMode, v => s.DevMode = v, save));

            return list;
        }
    }
}
