namespace Wholesome_Auto_Quester.Bot.FlightMasterManagement
{
using robotManager.Helpful;
using System.Collections.Generic;
using System.Threading;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;

public class FlightMasterDB
{
    public static List<FlightMaster> FlightMasterList { get; private set; }

    private static bool azerothNodesUpdated = false;
    private static bool kalimdorNodesUpdated = false;
    private static bool outlandNodesUpdated = false;
    private static bool northrendNodesUpdated = false;

    public static List<FlightMaster> AllFlightMasters
    {
        get
        {
            List<FlightMaster> result = new List<FlightMaster>();
            result.AddRange(hordeFlightMasters);
            result.AddRange(allianceFlightMasters);
            result.AddRange(neutralFlightMasters);
            return result;
        }
    }

    private static readonly List<FlightMaster> hordeFlightMasters = new List<FlightMaster>()
    {
        // EK
        new FlightMaster("Grom'gol, Stranglethorn", 1387, new Vector3(-12417.5f, 144.474f, 3.36881f, "None"), ContinentId.Azeroth),
        new FlightMaster("Flame Crest, Burning Steppes", 13177, new Vector3(-7504.06f, -2190.77f, 165.302f, "None"), ContinentId.Azeroth),
        new FlightMaster("Booty Bay, Stranglethorn", 2858, new Vector3(-14448.6f, 506.129f, 26.3565f, "None"), ContinentId.Azeroth),
        new FlightMaster("The Sepulcher, Silverpine Forest", 2226, new Vector3(473.939f, 1533.95f, 131.96f, "None"), ContinentId.Azeroth),
        new FlightMaster("Tarren Mill, Hillsbrad", 2389, new Vector3(2.67557f, -857.919f, 58.889f, "None"), ContinentId.Azeroth),
        new FlightMaster("Hammerfall, Arathi", 2851, new Vector3(-917.658, -3496.93994140625, 70.4505004882813, "None"), ContinentId.Azeroth),
        new FlightMaster("Kargath, Badlands", 2861, new Vector3(-6632.22021484375, -2178.419921875, 244.227, "None"), ContinentId.Azeroth),
        new FlightMaster("Thorium Point, Searing Gorge", 3305, new Vector3(-6559.26f, -1100.23f, 310.353f, "None"), ContinentId.Azeroth),
        new FlightMaster("Revantusk Village, The Hinterlands", 4314, new Vector3(-631.736f, -4720.6f, 5.48226f, "None"), ContinentId.Azeroth),
        new FlightMaster("Stonard, Swamp of Sorrows", 6026, new Vector3(-10459.2f, -3279.76f, 21.5445f, "None"), ContinentId.Azeroth),
        new FlightMaster("Undercity, Tirisfal", 4551, new Vector3(1567.12f, 266.345f, -43.0194f, "None"), ContinentId.Azeroth),
        new FlightMaster("Light's Hope Chapel, Eastern Plaguelands", 12636, new Vector3(2328.48f, -5290.72f, 81.79177f, "None"), ContinentId.Azeroth),
        // WotLK only
        new FlightMaster("The Bulwark, Tirisfal", 37915, new Vector3(1730.37f, -743.194f, 59.33407f, "None"), ContinentId.Azeroth),
        // Kalimdor
        new FlightMaster("Sun Rock Retreat, Stonetalon Mountains", 4312, new Vector3(968.077f, 1042.29f, 104.563f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Moonglade", 12740, new Vector3(7466.15f, -2122.08f, 492.427f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Bloodvenom Post, Felwood", 11900, new Vector3(5064.72f, -338.845f, 367.463f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Brackenwall Village, Dustwallow Marsh", 11899, new Vector3(-3149.14f, -2842.13f, 34.6649f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Everlook, Winterspring", 11139, new Vector3(6815.12f, -4610.12f, 710.759f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Valormok, Azshara", 8610, new Vector3(3664.02f, -4390.45f, 113.169f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Thunder Bluff, Mulgore", 2995, new Vector3(-1196.75f, 26.0777f, 177.033f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Orgrimmar, Durotar", 3310, new Vector3(1676.25f, -4313.45f, 61.7176f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Crossroads, The Barrens", 3615, new Vector3(-437.137f, -2596f, 95.8708f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Camp Taurajo, The Barrens", 10378, new Vector3(-2384.08f, -1880.94f, 95.9336f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Freewind Post, Thousand Needles", 4317, new Vector3(-5407.12f, -2419.61f, 89.7094f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Shadowprey Village, Desolace", 6726, new Vector3(-1770.37f, 3262.19f, 5.10852f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Gadgetzan, Tanaris", 7824, new Vector3(-7045.24f, -3779.4f, 10.3158f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Camp Mojache, Feralas", 8020, new Vector3(-4421.94f, 198.146f, 25.1863f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Zoram'gar Outpost, Ashenvale", 11901, new Vector3(3373.69f, 994.351f, 5.36158f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Splintertree Post, Ashenvale", 12616, new Vector3(2305.64f, -2520.15f, 103.893f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Cenarion Hold, Silithus", 15178, new Vector3(-6810.2f, 841.704f, 49.7481f, "None"), ContinentId.Kalimdor),
        // Outlands
        new FlightMaster("Thrallmar, Hellfire Peninsula", 16587, new Vector3(228.5f, 2633.57f, 87.67f, "None"), ContinentId.Expansion01),
        new FlightMaster("Falcon Watch, Hellfire Peninsula", 18942, new Vector3(-587.41f, 4101.01f, 91.37f, "None"), ContinentId.Expansion01),
        new FlightMaster("Zabra'jin, Zangarmarsh", 18791, new Vector3(219.45f, 7816f, 22.72f, "None"), ContinentId.Expansion01),
        new FlightMaster("Garadar, Nagrand", 18808, new Vector3(-1261.09f, 7133.39f, 57.34f, "None"), ContinentId.Expansion01),
        new FlightMaster("Shadowmoon Village, Shadowmoon Valley", 19317, new Vector3(-3018.62f, 2557.09f, 79.09f, "None"), ContinentId.Expansion01),
        new FlightMaster("Stonebreaker Hold, Terokkar Forest", 18807, new Vector3(-2567.33f, 4423.83f, 39.33f, "None"), ContinentId.Expansion01),
        new FlightMaster("Thunderlord Stronghold, Blade's Edge Mountains", 18953, new Vector3(2446.37f, 6020.93f, 154.34f, "None"), ContinentId.Expansion01),
        new FlightMaster("Sanctum of the Stars, Shadowmoon Valley", 21766, new Vector3(-4073.17f, 1123.61f, 42.47f, "None"), ContinentId.Expansion01),
        new FlightMaster("Spinebreaker Ridge, Hellfire Peninsula", 19558, new Vector3(-1316.84f, 2358.62f, 88.96f, "None"), ContinentId.Expansion01),
        new FlightMaster("Mok'Nathal Village, Blade's Edge Mountains", 22455, new Vector3(2028.79f, 4705.27f, 150.51f, "None"), ContinentId.Expansion01),
        new FlightMaster("Swamprat Post, Zangarmarsh", 20762, new Vector3(91.67f, 5214.92f, 23.1f, "None"), ContinentId.Expansion01),
        new FlightMaster("Hellfire Peninsula, The Dark Portal, Horde", 18930, new Vector3(-178.09f, 1026.72f, 54.19f, "None"), ContinentId.Expansion01),
        // Ghostlands
        new FlightMaster("Tranquillien, Ghostlands", 16189, new Vector3(7595.16f, -6782.24f, 86.76129f, "None"), ContinentId.Expansion01),
        new FlightMaster("Silvermoon City", 16192, new Vector3(9376.4f, -7164.92f, 8.937795f, "None"), ContinentId.Expansion01),
        //Northrend Horde
        new FlightMaster("Warsong Hold, Borean Tundra", 25288, new Vector3(2922.39f, 6244.39f, 208.753f, "None"), ContinentId.Northrend),
        new FlightMaster("Bor'gorok Outpost, Borean Tundra", 26848, new Vector3(4473.24f, 5708.79f, 81.26324f, "None"), ContinentId.Northrend),
        new FlightMaster("Taunka'le Village, Borean Tundra", 26847, new Vector3(3446.11f, 4088.41f, 16.77694f, "None"), ContinentId.Northrend),
        new FlightMaster("Vengeance Landing, Howling Fjord", 27344, new Vector3(1919.03f, -6176.72f, 24.48341f, "None"), ContinentId.Northrend),
        new FlightMaster("New Agamand, Howling Fjord", 24155, new Vector3(400.47f, -4542.25f, 245.0059f, "None"), ContinentId.Northrend),
        new FlightMaster("Apothecary Camp, Howling Fjord", 26844, new Vector3(2106.05f, -2968.81f, 148.5838f, "None"), ContinentId.Northrend),
        new FlightMaster("Camp Winterhoof, Howling Fjord", 24032, new Vector3(2649.27f, -4394.5f, 283.3044f, "None"), ContinentId.Northrend),
        new FlightMaster("Venomspite, Dragonblight", 26845, new Vector3(3248.77f, -662.297f, 166.7902f, "None"), ContinentId.Northrend),
        new FlightMaster("Agmar's Hammer, Dragonblight", 26566, new Vector3(3863.63f, 1523.11f, 90.10977f, "None"), ContinentId.Northrend),
        new FlightMaster("Kor'koron Vanguard, Dragonblight", 26850, new Vector3(4941.59f, 1167.95f, 239.3193f, "None"), ContinentId.Northrend),
        new FlightMaster("Conquest Hold, Grizzly Hills", 26852, new Vector3(3261.52f, -2265.45f, 114.1291f, "None"), ContinentId.Northrend),
        new FlightMaster("Camp Oneqwah, Grizzly Hills", 26853, new Vector3(3874.18f, -4520.87f, 217.2177f, "None"), ContinentId.Northrend),
        new FlightMaster("Sunreaver's Command, Crystalsong Forest", 30269, new Vector3(5587.26f, -694.69f, 206.6273f, "None"), ContinentId.Northrend),
        new FlightMaster("Warsong Camp, Wintergrasp", 30870, new Vector3(5023.4f, 3686.03f, 363.0365f, "None"), ContinentId.Northrend),
        new FlightMaster("Grom'arsh Crash-Site, The Storm Peaks", 29757, new Vector3(7855.98f, -732.388f, 1177.473f, "None"), ContinentId.Northrend),
        new FlightMaster("Camp Tunka'lo, The Storm Peaks", 29762, new Vector3(7798.38f, -2810.29f, 1217.843f, "None"), ContinentId.Northrend)
    };

    private static readonly List<FlightMaster> allianceFlightMasters = new List<FlightMaster>()
    {
        // EK
        new FlightMaster("Light's Hope Chapel, Eastern Plaguelands", 12617, new Vector3(2269.85f, -5345.39f, 86.94077f, "None"), ContinentId.Azeroth),
        new FlightMaster("Chillwind Camp, Western Plaguelands", 12596, new Vector3(928.273f, -1429.08f, 64.75098f, "None"), ContinentId.Azeroth),
        new FlightMaster("Aerie Peak, The Hinterlands", 8018, new Vector3(282.096f, -2001.28f, 194.127f, "None"), ContinentId.Azeroth),
        new FlightMaster("Southshore, Hillsbrad", 2432, new Vector3(-715.146f, -512.134f, 26.5445f, "None"), ContinentId.Azeroth),
        new FlightMaster("Refuge Pointe, Arathi", 2835, new Vector3(-1240.03f, -2513.96f, 21.92965f, "None"), ContinentId.Azeroth),
        new FlightMaster("Menethil Harbor, Wetlands", 1571, new Vector3(-3793.2f, -782.052f, 9.014537f, "None"), ContinentId.Azeroth),
        new FlightMaster("Ironforge, Dun Morogh", 1573, new Vector3(-4821.13f, -1152.4f, 502.2118f, "None"), ContinentId.Azeroth),
        new FlightMaster("Thelsamar, Loch Modan", 1572, new Vector3(-5424.85f, -2929.87f, 347.5621f, "None"), ContinentId.Azeroth),
        new FlightMaster("Thorium Point, Searing Gorge", 2941, new Vector3(-6559.06f, -1169.38f, 309.7972f, "None"), ContinentId.Azeroth),
        new FlightMaster("Morgan's Vigil, Burning Steppes", 2299, new Vector3(-8365.08f, -2736.93f, 185.6075f, "None"), ContinentId.Azeroth),
        new FlightMaster("Lakeshire, Redridge", 931, new Vector3(-9435.21f, -2234.88f, 69.10911f, "None"), ContinentId.Azeroth),
        new FlightMaster("Stormwind, Elwynn", 352, new Vector3(-8835.76f, 490.084f, 109.6157f, "None"), ContinentId.Azeroth),
        new FlightMaster("Darkshire, Duskwood", 2409, new Vector3(-10513.8f, -1258.79f, 41.43284f, "None"), ContinentId.Azeroth),
        new FlightMaster("Sentinel Hill, Westfall", 523, new Vector3(-10628.3f, 1037.27f, 34.11045f, "None"), ContinentId.Azeroth),
        new FlightMaster("Rebel Camp, Stranglethorn Vale", 24366, new Vector3(-11340.5f, -219.14f, 75.21273f, "None"), ContinentId.Azeroth),
        new FlightMaster("Booty Bay, Stranglethorn", 2859, new Vector3(-14477.9f, 464.101f, 36.38131f, "None"), ContinentId.Azeroth),
        new FlightMaster("Nethergarde Keep, Blasted Lands", 8609, new Vector3(-11110.2f, -3437.1f, 79.19771f, "None"), ContinentId.Azeroth),
        // Kalimdor
        new FlightMaster("Rut'theran Village, Teldrassil", 3838, new Vector3(8640.58f, 841.118f, 23.26336f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Moonglade", 10897, new Vector3(7454.85f, -2491.61f, 462.6161f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Everlook, Winterspring", 11138, new Vector3(6800.54f, -4742.35f, 701.499f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Talonbranch Glade, Felwood", 12578, new Vector3(6204.08f, -1951.43f, 571.3167f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Auberdine, Darkshore", 3841, new Vector3(6343.2f, 561.651f, 15.79894f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Forest Song, Ashenvale", 22935, new Vector3(3002.88f, -3206.81f, 190.0471f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Astranaar, Ashenvale", 4267, new Vector3(2828.38f, -284.25f, 106.6782f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Talrendis Point, Azshara", 12577, new Vector3(2718.18f, -3880.75f, 101.4485f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Stonetalon Peak, Stonetalon Mountains", 4407, new Vector3(2682.83f, 1466.45f, 233.6487f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Nijel's Point, Desolace", 6706, new Vector3(136.218f, 1326.33f, 193.4992f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Theramore, Dustwallow Marsh", 4321, new Vector3(-3828.88f, -4517.51f, 10.66114f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Thalanaar, Feralas", 4319, new Vector3(-4491.12f, -778.347f, -40.20263f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Feathermoon, Feralas", 8019, new Vector3(-4370.54f, 3340.11f, 12.26892f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Gadgetzan, Tanaris", 7823, new Vector3(-7224.87f, -3738.21f, 8.401169f, "None"), ContinentId.Kalimdor), // can't interact
        new FlightMaster("Cenarion Hold, Silithus", 15177, new Vector3(-6758.55f, 775.594f, 89.02157f, "None"), ContinentId.Kalimdor),
        // Outlands
        new FlightMaster("Hellfire Peninsula, The Dark Portal, Alliance", 18931, new Vector3(-323.81f, 1027.61f, 54.15654f, "None"), ContinentId.Expansion01),
        new FlightMaster("Honor Hold, Hellfire Peninsula", 16822, new Vector3(-665.804f, 2715.48f, 94.09752f, "None"), ContinentId.Expansion01),
        new FlightMaster("Temple of Telhamat, Hellfire Peninsula", 18785, new Vector3(199.061f, 4238.42f, 121.7271f, "None"), ContinentId.Expansion01),
        new FlightMaster("Shatter Point, Hellfire Peninsula", 20234, new Vector3(279.397f, 1489.76f, -15.52282f, "None"), ContinentId.Expansion01),
        new FlightMaster("Telredor, Zangarmarsh", 18788, new Vector3(210.492f, 6065.09f, 148.3183f, "None"), ContinentId.Expansion01),
        new FlightMaster("Orebor Harborage, Zangarmarsh", 22485, new Vector3(963.428f, 7399.58f, 29.24892f, "None"), ContinentId.Expansion01),
        new FlightMaster("Allerian Stronghold, Terokkar Forest", 18809, new Vector3(-2995.4f, 3873.27f, 9.543159f, "None"), ContinentId.Expansion01),
        new FlightMaster("Telaar, Nagrand", 18789, new Vector3(-2723.1f, 7302.84f, 88.63432f, "None"), ContinentId.Expansion01),
        new FlightMaster("Sylvanaar, Blade's Edge Mountains", 18937, new Vector3(2187.88f, 6794f, 183.3297f, "None"), ContinentId.Expansion01),
        new FlightMaster("Toshley's Station, Blade's Edge Mountains", 21107, new Vector3(1860.71f, 5528.27f, 276.6563f, "None"), ContinentId.Expansion01),
        new FlightMaster("Wildhammer Stronghold, Shadowmoon Valley", 18939, new Vector3(-3980.97f, 2156.29f, 104.9289f, "None"), ContinentId.Expansion01),
        // Dranei Continent
        new FlightMaster("The Exodar", 17555, new Vector3(-4057.15f, -11788.6f, 8.87662f, "None"), ContinentId.Expansion01),
        new FlightMaster("Blood Watch, Bloodmyst Isle", 17554, new Vector3(-1930.02f, -11956.8f, 57.39266f, "None"), ContinentId.Expansion01),
        //Northrend Alliance
        new FlightMaster("Valiance Keep, Borean Tundra", 26879, new Vector3(2272.98f, 5171.82f, 11.16359f, "None"), ContinentId.Northrend),
        new FlightMaster("Fizzcrank Airstrip, Borean Tundra", 26602, new Vector3(4126.8f, 5309.59f, 28.72333f, "None"), ContinentId.Northrend),
        new FlightMaster("Valgarde Port, Howling Fjord", 23736, new Vector3(567.415f, -5012.58f, 11.48421f, "None"), ContinentId.Northrend),
        new FlightMaster("Westguard Keep, Howling Fjord", 23859, new Vector3(1343.75f, -3287.78f, 174.4526f, "None"), ContinentId.Northrend),
        new FlightMaster("Fort Wildervar, Howling Fjord", 24061, new Vector3(2467.34f, -5028.79f, 283.6946f, "None"), ContinentId.Northrend),
        new FlightMaster("Wintergarde Keep, Dragonblight", 26878, new Vector3(3712.43f, -694.86f, 215.36f, "None"), ContinentId.Northrend),
        new FlightMaster("Stars' Rest, Dragonblight", 26881, new Vector3(3506.07f, 1990.42f, 65.18267f, "None"), ContinentId.Northrend),
        new FlightMaster("Fordragon Hold, Dragonblight", 26877, new Vector3(4606.09f, 1410.71f, 194.7481f, "None"), ContinentId.Northrend),
        new FlightMaster("Amberpine Lodge, Grizzly Hills", 26880, new Vector3(3447.84f, -2754.01f, 199.3687f, "None"), ContinentId.Northrend),
        new FlightMaster("Westfall Brigade, Grizzly Hills", 26876, new Vector3(4582.63f, -4254.86f, 182.208f, "None"), ContinentId.Northrend),
        new FlightMaster("Windrunner's Overlook, Crystalsong Forest", 30271, new Vector3(5032.91f, -521.242f, 225.9917f, "None"), ContinentId.Northrend),
        new FlightMaster("Valiance Landing Camp, Wintergrasp", 30869, new Vector3(5102.75f, 2187.83f, 365.6246f, "None"), ContinentId.Northrend),
        new FlightMaster("Frosthold, The Storm Peaks", 29750, new Vector3(6673.3f, -256.596f, 962.127f, "None"), ContinentId.Northrend)
    };

    private static readonly List<FlightMaster> neutralFlightMasters = new List<FlightMaster>()
    {
        // Kalimdor
        new FlightMaster("Emerald Sanctuary, Felwood", 22931, new Vector3(3981.74f, -1321.47f, 251.0415f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Marshal's Refuge, Un'Goro Crater", 10583, new Vector3(-6110.54f, -1140.35f, -186.866f, "None"), ContinentId.Kalimdor),
        new FlightMaster("Ratchet, The Barrens", 16227, new Vector3(-898.246f, -3769.65f, 11.7932f, "None"), ContinentId.Kalimdor),
        // EK
        new FlightMaster("Mudsprocket, Dustwallow Marsh", 23612, new Vector3(-4568.4f, -3223.19f, 34.90614f, "None"), ContinentId.Kalimdor),
        // WotLK only
        new FlightMaster("Thondoril River, Western Plaguelands", 37888, new Vector3(1943.14f, -2561.74f, 60.84085f, "None"), ContinentId.Azeroth),
        // DK only
        new FlightMaster("Acherus: The Ebon Hold", 29480, new Vector3(2348.63f, -5669.29f, 382.2413f, "None"), ContinentId.Azeroth),
        // Ghostlands
        new FlightMaster("Shattered Sun Staging Area", 26560, new Vector3(13012.4f, -6911.19f, 9.584099f, "None"), ContinentId.Expansion01),
        new FlightMaster("Zul'Aman, Ghostlands", 24851, new Vector3(6789.91f, -7749.43f, 126.619f, "None"), ContinentId.Expansion01),
        // Outlands
        new FlightMaster("Cosmowrench, Netherstorm", 20515, new Vector3(2973.2f, 1848.45f, 141.0015f, "None"), ContinentId.Expansion01),
        new FlightMaster("Shattrath, Terokkar Forest", 18940, new Vector3(-1837.23f, 5301.9f, -12.43f, "None"), ContinentId.Expansion01),
        new FlightMaster("Evergrove, Blade's Edge Mountains", 22216, new Vector3(2976.01f, 5501.13f, 143.67f, "None"), ContinentId.Expansion01),
        new FlightMaster("Area 52, Netherstorm", 18938, new Vector3(3082.31f, 3596.11f, 144.02f, "None"), ContinentId.Expansion01),
        new FlightMaster("The Stormspire, Netherstorm", 19583, new Vector3(4157.58f, 2959.69f, 352.08f, "None"), ContinentId.Expansion01),
        // Aldor reputation
        new FlightMaster("Altar of Sha'tar, Shadowmoon Valley", 19581, new Vector3(-3062.63f, 741.933f, -10.14242f, "None"), ContinentId.Expansion01),
        // Scryer reputation
        new FlightMaster("Sanctum of the Stars, Shadowmoon Valley", 21766, new Vector3(-4073.17f, 1123.61f, 42.47f, "None"), ContinentId.Expansion01),
        //Northrend Neutral
        new FlightMaster("Amber Ledge, Borean Tundra", 24795, new Vector3(3571.12f, 5957.59f, 135.7559f, "None"), ContinentId.Northrend),
        new FlightMaster("Transitus Shield, Coldarra", 27046, new Vector3(3573.9f, 6661.07f, 195.1814f, "None"), ContinentId.Northrend),
        new FlightMaster("Unu'pe, Borean Tundra", 28195, new Vector3(2917.21f, 4043.44f, 1.78471f, "None"), ContinentId.Northrend),
        new FlightMaster("Kamagua, Howling Fjord", 28197, new Vector3(787.755f, -2889.06f, 6.408492f, "None"), ContinentId.Northrend),
        new FlightMaster("Moa'ki, Dragonblight", 28196, new Vector3(2793.19f, 906.36f, 22.36289f, "None"), ContinentId.Northrend),
        new FlightMaster("Wyrmrest Temple, Dragonblight", 26851, new Vector3(3647.26f, 244.051f, 52.22766f, "None"), ContinentId.Northrend),
        new FlightMaster("Dalaran", 28674, new Vector3(5813.37f, 453.403f, 658.7562f, "None"), ContinentId.Northrend),
        new FlightMaster("Light's Breach, Zul'Drak", 28618, new Vector3(5192.26f, -2207.04f, 239.3989f, "None"), ContinentId.Northrend),
        new FlightMaster("Ebon Watch, Zul'Drak", 28615, new Vector3(5218.97f, -1299.12f, 242.2672f, "None"), ContinentId.Northrend),
        new FlightMaster("The Argent Stand, Zul'Drak", 28623, new Vector3(5523.68f, -2674.97f, 303.9543f, "None"), ContinentId.Northrend),
        new FlightMaster("Zim'Torga, Zul'Drak", 28624, new Vector3(5780.84f, -3598.16f, 387.1552f, "None"), ContinentId.Northrend),
        new FlightMaster("Gundrak, Zul'Drak", 30569, new Vector3(6893.54f, -4118.87f, 467.3545f, "None"), ContinentId.Northrend),
        new FlightMaster("River's Heart, Sholazar Basin", 28574, new Vector3(5505.6f, 4745.33f, -194.4336f, "None"), ContinentId.Northrend),
        new FlightMaster("K3, The Storm Peaks", 29721, new Vector3(6188.97f, -1056.53f, 409.8226f, "None"), ContinentId.Northrend),
        new FlightMaster("Bouldercrag's Refuge, The Storm Peaks", 29950, new Vector3(8475.49f, -337.946f, 905.9264f, "None"), ContinentId.Northrend),
        new FlightMaster("Ulduar, The Storm Peaks", 29951, new Vector3(8861.33f, -1322.39f, 1033.314f, "None"), ContinentId.Northrend),
        new FlightMaster("The Argent Vanguard, Icecrown", 30433, new Vector3(6162.62f, -62.0921f, 388.1804f, "None"), ContinentId.Northrend),
        new FlightMaster("Death's Rise, Icecrown", 31078, new Vector3(7429.6f, 4231.64f, 314.285f, "None"), ContinentId.Northrend),
        new FlightMaster("Argent Tournament Grounds, Icecrown", 33849, new Vector3(8481.62f, 891.614f, 547.293f, "None"), ContinentId.Northrend),
        //Northrend Neutral after Quest
        // Quest ID 12896 for Alliance - 12897 for Horde
        new FlightMaster("The Shadow Vault, Icecrown", 30314, new Vector3(8407.96f, 2700.43f, 655.1186f, "None"), ContinentId.Northrend),
        // Quest ID 12523 Neutral
        new FlightMaster("Nesingwary Base Camp, Sholazar Basin", 28037, new Vector3(5587.64f, 5830.73f, -67.9672f, "None"), ContinentId.Northrend),
        // Quest ID 13141 Neutral
        new FlightMaster("Crusaders' Pinnacle, Icecrown", 31069, new Vector3(6401.22f, 464.245f, 512.5808f, "None"), ContinentId.Northrend),
        // Quest ID 12956 Neutral
        new FlightMaster("Dun Nifflelem, The Storm Peaks", 32571, new Vector3(7309.45f, -2612.99f, 814.8444f, "None"), ContinentId.Northrend)
    };

    public static void Initialize()
    {
        if (FlightMasterManager.isHorde)
        {
            FlightMasterList = hordeFlightMasters;
            FlightMasterList.AddRange(neutralFlightMasters);
        }
        else
        {
            FlightMasterList = allianceFlightMasters;
            FlightMasterList.AddRange(neutralFlightMasters);
        }

        // Remove disabled FMs
        List<string> disabledFms = new List<string>(WFMSettings.CurrentSettings.DisabledFlightsList);
        List<FlightMaster> listFMCopy = new List<FlightMaster>(FlightMasterList);
        foreach (FlightMaster fm in listFMCopy)
        {
            if (disabledFms.Exists(disabled => fm.Name.ToLower().Contains(disabled.ToLower())))
            {
                WFMLogger.Log($"You have disabled {fm.Name}");
                FlightMasterList.Remove(fm);
            }
        }
    }

    public static FlightMaster GetFlightMasterById(int npcId)
    {
        foreach (FlightMaster fm in FlightMasterList)
        {
            if (fm.NPCId == npcId)
                return fm;
        }
        WFMLogger.LogError($"Couldn't find Flight Master with id {npcId} in the DB ");
        return null;
    }

    public static bool SetFlightMasterToKnown(string flightMasterName)
    {
        if (!WFMSettings.CurrentSettings.KnownFlightsList.Contains(flightMasterName))
        {
            WFMLogger.Log($"Adding {flightMasterName} to known flights list");
            WFMSettings.CurrentSettings.KnownFlightsList.Add(flightMasterName);
            WFMSettings.CurrentSettings.Save();
            return true;
        }
        return false;
    }

    public static bool SetFlightMasterToKnown(int flightMasterID)
    {
        FlightMaster flightMaster = GetFlightMasterById(flightMasterID);
        return SetFlightMasterToKnown(flightMaster.Name);
    }

    public static bool SetFlightMasterToUnknown(string flightMasterName)
    {
        if (WFMSettings.CurrentSettings.KnownFlightsList.Contains(flightMasterName))
        {
            WFMLogger.Log($"Removing {flightMasterName} from known flights list");
            WFMSettings.CurrentSettings.KnownFlightsList.Remove(flightMasterName);
            WFMSettings.CurrentSettings.Save();
            return true;
        }
        return false;
    }

    public static bool UpdateKnownFMs(FlightMaster FMWithMapOpen)
    {
        if ((ContinentId)Usefuls.ContinentId == ContinentId.Azeroth && !azerothNodesUpdated
            || (ContinentId)Usefuls.ContinentId == ContinentId.Kalimdor && !kalimdorNodesUpdated
            || (ContinentId)Usefuls.ContinentId == ContinentId.Expansion01 && !outlandNodesUpdated
            || (ContinentId)Usefuls.ContinentId == ContinentId.Northrend && !northrendNodesUpdated)
        {
            WFMLogger.Log($"Updating known FlightMasters on {Usefuls.ContinentNameMpq} for this session");

            bool allInvalid = true;
            bool latNodeWasInvalid = false;
            bool modificationWasMade = false;
            bool endOfListReached = false;

            // 3 attempts to discover flights
            for (int j = 1; j <= 3; j++)
            {
                if (endOfListReached)
                {
                    if ((ContinentId)Usefuls.ContinentId == ContinentId.Azeroth)
                        azerothNodesUpdated = true;
                    else if ((ContinentId)Usefuls.ContinentId == ContinentId.Kalimdor)
                        kalimdorNodesUpdated = true;
                    else if ((ContinentId)Usefuls.ContinentId == ContinentId.Expansion01)
                        outlandNodesUpdated = true;
                    else if ((ContinentId)Usefuls.ContinentId == ContinentId.Northrend)
                        northrendNodesUpdated = true;

                    break;
                }

                WFMMoveInteract.GoInteractwithFM(FMWithMapOpen);
                // Loop through nodes
                for (int i = 0; i < 120; i++)
                {
                    string nodeName = Lua.LuaDoString<string>($"return TaxiNodeName({i})");
                    if (nodeName != "INVALID")
                    {
                        allInvalid = false;
                        latNodeWasInvalid = false;
                        if (SetFlightMasterToKnown(nodeName) && modificationWasMade == false)
                            modificationWasMade = true;
                    }
                    else
                    {
                        // we hit 2 invalids in a row - means this is the end of the list
                        if (latNodeWasInvalid)
                        {
                            endOfListReached = true;
                            break;
                        }

                        latNodeWasInvalid = true;
                        if (SetFlightMasterToUnknown(nodeName) && modificationWasMade == false)
                            modificationWasMade = true;
                    }
                }

                if (allInvalid)
                {
                    Lua.LuaDoString("CloseGossip()");
                    WFMLogger.LogDebug($"All flight nodes are invalid, retrying ({j}/3)");
                    Thread.Sleep(500);
                }
            }

            if (allInvalid)
                WFMToolBox.PausePlugin("Couldn't find a valid flight node");

            return modificationWasMade;
        }

        return false;
    }
}

}

