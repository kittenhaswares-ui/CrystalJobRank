using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Services;

internal static class JobMapper
{
    public static CombatJob FromClassJobId(byte id) => id switch
    {
        19 => CombatJob.PLD,
        21 => CombatJob.WAR,
        32 => CombatJob.DRK,
        37 => CombatJob.GNB,
        24 => CombatJob.WHM,
        28 => CombatJob.SCH,
        33 => CombatJob.AST,
        40 => CombatJob.SGE,
        20 => CombatJob.MNK,
        22 => CombatJob.DRG,
        30 => CombatJob.NIN,
        34 => CombatJob.SAM,
        39 => CombatJob.RPR,
        41 => CombatJob.VPR,
        23 => CombatJob.BRD,
        31 => CombatJob.MCH,
        38 => CombatJob.DNC,
        25 => CombatJob.BLM,
        27 => CombatJob.SMN,
        35 => CombatJob.RDM,
        42 => CombatJob.PCT,
        _ => CombatJob.Unknown,
    };
}

