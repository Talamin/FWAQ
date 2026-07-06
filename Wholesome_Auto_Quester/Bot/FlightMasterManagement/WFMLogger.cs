using robotManager.Helpful;
using System.Drawing;

namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
    /// <summary>
    /// Logger for the integrated flight-master travel system (ported from the Wholesome-TBC-FlightMaster plugin so the
    /// WAQ product controls it directly instead of depending on a separate plugin). Renamed from the plugin's Logger
    /// to avoid clashing with <see cref="Wholesome_Auto_Quester.Helpers.Logger"/>.
    /// </summary>
    public static class WFMLogger
    {
        public static void Log(string s) =>
            Logging.Write($"[WAQ FlightMaster] {s}", Logging.LogType.Normal, Color.DarkCyan);

        public static void LogDebug(string s) =>
            Logging.WriteDebug($"[WAQ FlightMaster] {s}");

        public static void LogError(string s) =>
            Logging.WriteError($"[WAQ FlightMaster] {s}");

        public static void LogWarning(string s) =>
            Logging.Write($"[WAQ FlightMaster] {s}", Logging.LogType.Normal, Color.IndianRed);
    }
}
