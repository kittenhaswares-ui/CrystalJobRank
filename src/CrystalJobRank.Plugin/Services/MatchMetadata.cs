using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Services;

internal static class MatchMetadata
{
    private static readonly Dictionary<ushort, string> Arenas = new()
    {
        [1032] = "The Palaistra",
        [1058] = "The Palaistra",
        [1033] = "The Volcanic Heart",
        [1059] = "The Volcanic Heart",
        [1034] = "Cloud Nine",
        [1060] = "Cloud Nine",
        [1116] = "Clockwork Castletown",
        [1117] = "Clockwork Castletown",
        [1138] = "The Red Sands",
        [1139] = "The Red Sands",
        [1293] = "Bayside Battleground",
        [1294] = "Bayside Battleground",
        [1357] = "Archeia Harmonias",
        [1358] = "Archeia Harmonias",
    };

    private static readonly HashSet<ushort> CasualDuties = [835, 836, 837, 912, 967, 1046, 1102];
    private static readonly HashSet<ushort> CustomDuties = [862, 863, 864, 923, 978, 1057, 1113];
    private static readonly HashSet<ushort> RankedDuties =
    [
        838, 839, 840, 841, 842, 843, 847, 848, 849, 850, 851, 852,
        853, 854, 855, 856, 857, 858, 859, 860, 861,
        913, 914, 915, 916, 917, 918, 919, 920, 921, 922,
        968, 969, 970, 971, 972, 973, 974, 975, 976, 977,
        1047, 1048, 1049, 1050, 1051, 1052, 1053, 1054, 1055, 1056,
        1103, 1104, 1105, 1106, 1107, 1108, 1109, 1110, 1111, 1112,
    ];

    public static string ArenaName(ushort territoryId) =>
        Arenas.GetValueOrDefault(territoryId, $"Territory {territoryId}");

    public static MatchQueue Queue(ushort dutyId)
    {
        if (CasualDuties.Contains(dutyId)) return MatchQueue.Casual;
        if (RankedDuties.Contains(dutyId)) return MatchQueue.Ranked;
        if (CustomDuties.Contains(dutyId)) return MatchQueue.Custom;
        return MatchQueue.Unknown;
    }
}

