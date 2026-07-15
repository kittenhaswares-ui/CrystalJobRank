using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Models;

public sealed class MatchRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CompletedAtUtc { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public CombatJob LocalJob { get; set; }
    public MatchOutcome Outcome { get; set; }
    public MatchQueue Queue { get; set; }
    public ushort ContentFinderConditionId { get; set; }
    public ushort TerritoryId { get; set; }
    public string Arena { get; set; } = "Unknown";
    public ushort DurationSeconds { get; set; }
    public int AstraProgressTenths { get; set; }
    public int UmbraProgressTenths { get; set; }
    public int RatingBefore { get; set; }
    public int RatingAfter { get; set; }
    public int RatingDelta { get; set; }
    public int RatingEpoch { get; set; }
    public ScoreboardStats LocalStats { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
    public List<PlayerScoreboardRow> Scoreboard { get; set; } = [];

    public LeaderboardMatchSubmission ToSubmission()
    {
        var identity = Validation.NormalizeCharacterIdentity(CharacterName, WorldId, WorldName);
        var completedAtUtc = CanonicalCompletedAtUtc();
        return new LeaderboardMatchSubmission(
            MatchKey(identity, completedAtUtc),
            completedAtUtc,
            identity.CharacterName,
            identity.WorldId,
            identity.WorldName,
            LocalJob,
            Outcome,
            Queue,
            TerritoryId,
            DurationSeconds,
            LocalStats);
    }

    private DateTime CanonicalCompletedAtUtc()
    {
        var utc = CompletedAtUtc.Kind switch
        {
            DateTimeKind.Utc => CompletedAtUtc,
            DateTimeKind.Local => CompletedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(CompletedAtUtc, DateTimeKind.Utc),
        };
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private string MatchKey(CharacterIdentity identity, DateTime completedAtUtc)
    {
        // Only stable fields belonging to the local player participate. Truncating the
        // completion time to whole seconds removes serializer/callback sub-second noise;
        // ToSubmission sends that same canonical timestamp so an idempotent retry also
        // has an identical payload hash on the service.
        var canonical = string.Join('|',
            identity.CharacterName.ToUpperInvariant(),
            identity.WorldId.ToString(CultureInfo.InvariantCulture),
            ((int)LocalJob).ToString(CultureInfo.InvariantCulture),
            ((int)Outcome).ToString(CultureInfo.InvariantCulture),
            ((int)Queue).ToString(CultureInfo.InvariantCulture),
            TerritoryId.ToString(CultureInfo.InvariantCulture),
            completedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            DurationSeconds.ToString(CultureInfo.InvariantCulture),
            LocalStats.Kills.ToString(CultureInfo.InvariantCulture),
            LocalStats.Deaths.ToString(CultureInfo.InvariantCulture),
            LocalStats.Assists.ToString(CultureInfo.InvariantCulture),
            LocalStats.DamageDealt.ToString(CultureInfo.InvariantCulture),
            LocalStats.DamageTaken.ToString(CultureInfo.InvariantCulture),
            LocalStats.HpRestored.ToString(CultureInfo.InvariantCulture),
            LocalStats.TimeOnCrystalSeconds.ToString(CultureInfo.InvariantCulture));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record LeaderboardMatchSubmission(
    string MatchKey,
    DateTime CompletedAtUtc,
    string CharacterName,
    uint WorldId,
    string WorldName,
    CombatJob Job,
    MatchOutcome Outcome,
    MatchQueue Queue,
    ushort TerritoryId,
    ushort DurationSeconds,
    ScoreboardStats Stats);

internal static class CharacterRatingEpochs
{
    public static string Key(string characterName, uint worldId, CombatJob job)
    {
        if (!CombatJobs.All.Contains(job))
        {
            throw new ArgumentException("A valid combat job is required.", nameof(job));
        }

        var normalizedName = Validation.NormalizeCharacterName(characterName).ToUpperInvariant();
        var normalizedWorldId = Validation.ValidateWorldId(worldId);
        return FormattableString.Invariant($"{normalizedWorldId}|{(int)job}|{normalizedName}");
    }

    public static bool TryKey(string? characterName, uint worldId, CombatJob job, out string key)
    {
        try
        {
            key = Key(characterName ?? string.Empty, worldId, job);
            return true;
        }
        catch (ArgumentException)
        {
            key = string.Empty;
            return false;
        }
    }

    public static int Current(
        IReadOnlyDictionary<string, int> epochs,
        string characterName,
        uint worldId,
        CombatJob job)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        return epochs.TryGetValue(Key(characterName, worldId, job), out var epoch)
            ? epoch
            : 0;
    }

    public static int Advance(
        IDictionary<string, int> epochs,
        string characterName,
        uint worldId,
        CombatJob job)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        var key = Key(characterName, worldId, job);
        var current = epochs.TryGetValue(key, out var epoch) ? epoch : 0;
        var next = checked(current + 1);
        epochs[key] = next;
        return next;
    }
}

public sealed class PlayerScoreboardRow
{
    public string Name { get; set; } = string.Empty;
    public uint WorldId { get; set; }
    public string World { get; set; } = string.Empty;
    public CombatJob Job { get; set; }
    public int Team { get; set; }
    public ScoreboardStats Stats { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed class JobLifetimeStats
{
    public int HighestRating { get; set; } = RatingEngine.InitialRating;
    public int HighestKills { get; set; }
    public int HighestDamageDealt { get; set; }
    public int HighestDamageTaken { get; set; }
    public int HighestHealing { get; set; }

    public JobLifetimeStats Copy() => new()
    {
        HighestRating = HighestRating,
        HighestKills = HighestKills,
        HighestDamageDealt = HighestDamageDealt,
        HighestDamageTaken = HighestDamageTaken,
        HighestHealing = HighestHealing,
    };
}

public sealed class RoleStreakProgress
{
    public int CurrentWinStreak { get; set; }
    public int BestWinStreak { get; set; }
    public int CurrentDeathlessStreak { get; set; }
    public int BestDeathlessStreak { get; set; }

    public RoleStreakProgress Copy() => new()
    {
        CurrentWinStreak = CurrentWinStreak,
        BestWinStreak = BestWinStreak,
        CurrentDeathlessStreak = CurrentDeathlessStreak,
        BestDeathlessStreak = BestDeathlessStreak,
    };
}

public static class AchievementThresholds
{
    public static IReadOnlyList<int> WinStreak { get; } = [3, 5, 10, 20];
    public static IReadOnlyList<int> DeathlessStreak { get; } = [1, 3, 5, 10, 20];
}

public sealed class PluginData
{
    public int Version { get; set; } = 5;
    public int RatingRulesVersion { get; set; } = RatingEngine.RulesVersion;
    public List<MatchRecord> Matches { get; set; } = [];
    // Retained for one migration so v1-v4 JSON can be read safely. Runtime rating
    // state uses CurrentCharacterRatingEpochs exclusively.
    public Dictionary<CombatJob, int> CurrentRatingEpochs { get; set; } = [];
    public Dictionary<string, int> CurrentCharacterRatingEpochs { get; set; } = [];
    public Dictionary<CombatJob, JobLifetimeStats> LifetimeByJob { get; set; } = [];
    public Dictionary<CombatRole, RoleStreakProgress> RoleStreaks { get; set; } = [];
}
