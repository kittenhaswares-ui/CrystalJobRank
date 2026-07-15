using System.Text;
using System.Text.RegularExpressions;

namespace CrystalJobRank.Core;

public static partial class Validation
{
    [GeneratedRegex("^[\\p{L}\\p{M}'-]+ [\\p{L}\\p{M}'-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterNamePattern();

    [GeneratedRegex("^[\\p{L}\\p{M}\\p{N}' -]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WorldNamePattern();

    public static string NormalizeCharacterName(string value)
    {
        var source = value ?? string.Empty;
        if (source.Any(char.IsControl))
        {
            throw new ArgumentException("Character name must not contain control characters.", nameof(value));
        }

        var normalized = string.Join(
            ' ',
            source.Trim().Normalize(NormalizationForm.FormC)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < 3 or > 42 || !CharacterNamePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "Character name must contain exactly two name parts and 3-42 letters, apostrophes, or hyphens.",
                nameof(value));
        }

        return normalized;
    }

    public static string NormalizeWorldName(string value)
    {
        var source = value ?? string.Empty;
        if (source.Any(char.IsControl))
        {
            throw new ArgumentException("World name must not contain control characters.", nameof(value));
        }

        var normalized = source.Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length is < 1 or > 32 || !WorldNamePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "World name must contain 1-32 letters, numbers, spaces, apostrophes, or hyphens.",
                nameof(value));
        }

        return normalized;
    }

    public static uint ValidateWorldId(uint worldId)
    {
        if (worldId is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("World ID must be between 1 and 65535.", nameof(worldId));
        }

        return worldId;
    }

    public static CharacterIdentity NormalizeCharacterIdentity(
        string characterName,
        uint worldId,
        string worldName) => new(
            NormalizeCharacterName(characterName),
            ValidateWorldId(worldId),
            NormalizeWorldName(worldName));

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

        ValidateScoreboardStats(submission.Stats);
    }

    public static bool AreScoreboardStatsPlausible(ScoreboardStats? stats) =>
        stats is not null &&
        stats.Kills is >= 0 and <= 100 &&
        stats.Deaths is >= 0 and <= 100 &&
        stats.Assists is >= 0 and <= 100 &&
        stats.DamageDealt is >= 0 and <= 20_000_000 &&
        stats.DamageTaken is >= 0 and <= 20_000_000 &&
        stats.HpRestored is >= 0 and <= 20_000_000 &&
        stats.TimeOnCrystalSeconds is >= 0 and <= 1800;

    public static void ValidateScoreboardStats(ScoreboardStats? stats)
    {
        if (!AreScoreboardStatsPlausible(stats))
        {
            throw new ArgumentException("Scoreboard values are outside the accepted range.", nameof(stats));
        }
    }
}
