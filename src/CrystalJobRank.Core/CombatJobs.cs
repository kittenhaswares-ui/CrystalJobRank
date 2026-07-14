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
}
