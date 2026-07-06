using robotManager.Helpful;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
    /// <summary>Persistent settings + the per-character discovered-node list for the integrated flight-master system.
    /// Ported from the Wholesome-TBC-FlightMaster plugin; kept self-contained (its own WFMSettings.*.xml) so the port
    /// doesn't have to merge into the WAQ settings GUI. KnownFlightsList persists which taxi nodes the character has
    /// discovered, across runs.</summary>
    [Serializable]
    public class WFMSettings : Settings
    {
        public WFMSettings()
        {
            TaxiTriggerDistance = 600;
            DetectTaxiDistance = 400;
            MinimumDistanceSaving = 500;
            SkipIfFollowPathDistance = 2000f;
            PauseLengthInSeconds = 600;
            TakeUndiscoveredTaxi = false;
            LastUpdateDate = 0;

            KnownFlightsList = new List<string>();
            DisabledFlightsList = new string[] { };

            ConfigWinForm(new System.Drawing.Point(400, 400), "WAQ FlightMaster Settings");
        }

        public double LastUpdateDate { get; set; }

        [DefaultValue(new string[] { })]
        [Category("Lists")]
        [DisplayName("Discovered nodes")]
        [Description("List of already known nodes. You cannot modify this list.")]
        public List<string> KnownFlightsList { get; set; }

        [Category("Lists")]
        [DisplayName("Blacklisted nodes")]
        [Description("You can add flight nodes you want to disable completely in this list. The name can be partial (ex : mojach for Camp Mojache)")]
        public string[] DisabledFlightsList { get; set; }

        [DefaultValue(false)]
        [Category("Settings")]
        [DisplayName("Take undiscovered taxi")]
        [Description("Will choose the nearest taxi for a flight even if you haven't discovered it. WARNING : can take you to dangerous zones")]
        public bool TakeUndiscoveredTaxi { get; set; }

        [DefaultValue(600)]
        [Category("Settings")]
        [DisplayName("Pause length in seconds")]
        [Description("In case of an unconnected flight or dead FlightMaster, set how long the plugin should be paused (in seconds)")]
        public int PauseLengthInSeconds { get; set; }

        [DefaultValue(600)]
        [Category("Settings")]
        [DisplayName("Trigger Distance")]
        [Description("Sets the minimum walking distance to your destination to check for flights")]
        public int TaxiTriggerDistance { get; set; }

        [DefaultValue(400)]
        [Category("Settings")]
        [DisplayName("Discover Distance")]
        [Description("Maximum distance to discover a taxi node")]
        public int DetectTaxiDistance { get; set; }

        [DefaultValue(500)]
        [Category("Settings")]
        [DisplayName("Minimum distance saving")]
        [Description("Sets how much shorter a path has to be for a flight to be taken")]
        public int MinimumDistanceSaving { get; set; }

        [DefaultValue(2000f)]
        [Category("Follow Path")]
        [DisplayName("Minimum Follow Path distance")]
        [Description("Minimum Follow Path distance to be considered for a flight.")]
        public float SkipIfFollowPathDistance { get; set; }

        public static WFMSettings CurrentSettings { get; set; }

        public bool Save()
        {
            try
            {
                return Save(AdviserFilePathAndName("WFMSettings",
                    ObjectManager.Me.Name + "." + Usefuls.RealmName));
            }
            catch (Exception ex)
            {
                WFMLogger.Log("WFMSettings > Save(): " + ex);
                return false;
            }
        }

        public static bool Load()
        {
            try
            {
                if (File.Exists(AdviserFilePathAndName("WFMSettings",
                    ObjectManager.Me.Name + "." + Usefuls.RealmName)))
                {
                    CurrentSettings = Load<WFMSettings>(
                        AdviserFilePathAndName("WFMSettings",
                        ObjectManager.Me.Name + "." + Usefuls.RealmName));
                    return true;
                }
                CurrentSettings = new WFMSettings();
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("WFMSettings > Load(): " + ex);
            }
            return false;
        }
    }
}
