using System.Collections.Generic;
using robotManager.Helpful;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;

namespace Wholesome_Auto_Quester.Helpers
{
    /// <summary>
    /// "Is this world position under water?" via a vertical TraceLine liquid probe. TraceLine is frame-locked
    /// (~1 client round-trip per frame), so the verdict is CACHED per GameObject GUID — a world object's position
    /// is static, so it's probed at most once ever. Used to skip underwater quest pickups where WRobot's navmesh
    /// is poor (e.g. the "Lost Armaments" Weapon Containers, half of which spawn on the Azuremyst sea floor).
    /// A liquid probe only works once the client has that geometry loaded (object within scan range), so callers
    /// must probe when the object is nearby, not at task-generation time from across the map. (Talamin)
    /// </summary>
    public static class WaterHelper
    {
        private static readonly Dictionary<ulong, bool> _cache = new Dictionary<ulong, bool>();

        /// <summary>Cached submerged-verdict for a world object (keyed by its GUID; position is static).</summary>
        public static bool IsObjectUnderWater(ulong guid, Vector3 position)
        {
            if (guid != 0 && _cache.TryGetValue(guid, out bool cached))
                return cached;
            bool submerged = IsPositionUnderWater(position);
            if (guid != 0)
                _cache[guid] = submerged;
            return submerged;
        }

        /// <summary>
        /// True if 'p' sits below the liquid surface. Traces a vertical ray through P (HitTestWater = liquid only);
        /// TraceLineGo returns true on a liquid hit and 'surface' is the water-surface point. If the surface is at
        /// or above P, P is submerged. No liquid in the column -> not under water.
        /// </summary>
        public static bool IsPositionUnderWater(Vector3 p, float span = 50f, float epsilon = 0.1f)
        {
            Vector3 from = new Vector3(p.X, p.Y, p.Z + span);
            Vector3 to = new Vector3(p.X, p.Y, p.Z - span);
            if (!TraceLine.TraceLineGo(from, to, CGWorldFrameHitFlags.HitTestWater, out Vector3 surface))
                return false;                     // no liquid in the column
            return surface.Z >= p.Z - epsilon;    // liquid surface at/above the point -> submerged
        }
    }
}
