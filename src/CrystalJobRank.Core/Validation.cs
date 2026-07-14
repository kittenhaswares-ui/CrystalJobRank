using System.Text.RegularExpressions;

namespace CrystalJobRank.Core;

public static partial class Validation
{
    [GeneratedRegex("^[\\p{L}\\p{N} _.'-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayNamePattern();

    public static string NormalizeDisplayName(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 2 or > 24 || !DisplayNamePattern().IsMatch(normalized))
        {
            throw new ArgumentException("Display name must contain 2-24 letters, numbers, spaces, or ._'-." );
        }

        return normalized;
    }

    public static void ValidateSubmission(MatchSubmission submission)
    {
        if (!CombatJobs.All.Contains(submission.Job))
        {
            throw new ArgumentException("Job is required.");
        }

        if (!Enum.IsDefined(submission.Outcome))
        {
            throw new ArgumentException("A valid match outcome is required.");
        }

        if (!Enum.IsDefined(submission.Queue))
        {
            throw new ArgumentException("A valid match queue is required.");
        }

        if (string.IsNullOrWhiteSpace(submission.Fingerprint) || submission.Fingerprint.Length > 128)
        {
            throw new ArgumentException("Fingerprint must contain 1-128 characters.");
        }

        if (submission.CompletedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("CompletedAtUtc must be UTC.");
        }

        if (submission.CompletedAtUtc < DateTime.UtcNow.AddDays(-90) || submission.CompletedAtUtc > DateTime.UtcNow.AddMinutes(10))
        {
            throw new ArgumentException("Match timestamp is outside the accepted window.");
        }

        if (submission.DurationSeconds is < 10 or > 1800)
        {
            throw new ArgumentException("Match duration is outside the accepted range.");
        }

        var stats = submission.Stats;
        if (stats.Kills is < 0 or > 100 || stats.Deaths is < 0 or > 100 || stats.Assists is < 0 or > 100)
        {
            throw new ArgumentException("K/D/A values are outside the accepted range.");
        }

        if (stats.DamageDealt is < 0 or > 20_000_000 ||
            stats.DamageTaken is < 0 or > 20_000_000 ||
            stats.HpRestored is < 0 or > 20_000_000 ||
            stats.TimeOnCrystalSeconds is < 0 or > 1800)
        {
            throw new ArgumentException("Scoreboard values are outside the accepted range.");
        }
    }
}
