namespace CrystalJobRank.Core;

public static class CombatJobs
{
    public static IReadOnlyList<CombatJob> All { get; } = Enum
        .GetValues<CombatJob>()
        .Where(x => x != CombatJob.Unknown)
        .ToArray();

    public static string AbbreviationList { get; } = string.Join(", ", All);

    public static bool TryParseAbbreviation(string? value, out CombatJob job)
    {
        var token = value?.Trim();
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), token, StringComparison.OrdinalIgnoreCase))
            {
                job = candidate;
                return true;
            }
        }

        job = CombatJob.Unknown;
        return false;
    }

    public static CombatRole RoleOf(CombatJob job) => job switch
    {
        CombatJob.PLD or CombatJob.WAR or CombatJob.DRK or CombatJob.GNB => CombatRole.Tank,
        CombatJob.WHM or CombatJob.SCH or CombatJob.AST or CombatJob.SGE => CombatRole.Healer,
        CombatJob.MNK or CombatJob.DRG or CombatJob.NIN or CombatJob.SAM or CombatJob.RPR or CombatJob.VPR or
        CombatJob.BRD or CombatJob.MCH or CombatJob.DNC or
        CombatJob.BLM or CombatJob.SMN or CombatJob.RDM or CombatJob.PCT => CombatRole.Dps,
        _ => CombatRole.Unknown,
    };
}
