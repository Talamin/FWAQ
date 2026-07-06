using robotManager.Helpful;
using System.Diagnostics;
using wManager.Wow.Enums;

namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
    /// <summary>One flight-master (taxi) node: the NPC to talk to, where it stands, and which continent it's on.
    /// Ported verbatim from the Wholesome-TBC-FlightMaster plugin.</summary>
    public class FlightMaster
    {
        public int NPCId { get; set; }
        public Vector3 Position { get; set; }
        public string Name { get; set; }
        public ContinentId Continent { get; set; }

        private readonly Stopwatch disableTimer = new Stopwatch();

        public FlightMaster(string name, int npcId, Vector3 position, ContinentId continent)
        {
            Name = name;
            NPCId = npcId;
            Position = position;
            Continent = continent;
            disableTimer.Reset();
        }

        public bool IsDiscovered => WFMSettings.CurrentSettings.KnownFlightsList.Contains(Name);

        public bool IsDisabledByPlugin()
        {
            bool isDisabled = disableTimer.IsRunning && disableTimer.ElapsedMilliseconds < WFMSettings.CurrentSettings.PauseLengthInSeconds * 1000;
            if (disableTimer.ElapsedMilliseconds >= WFMSettings.CurrentSettings.PauseLengthInSeconds * 1000)
            {
                disableTimer.Reset();
                isDisabled = false;
            }
            return isDisabled;
        }

        public void Disable(string reason)
        {
            WFMLogger.Log($"Disabling {Name} for {WFMSettings.CurrentSettings.PauseLengthInSeconds} seconds. ({reason})");
            disableTimer.Restart();
        }
    }
}
