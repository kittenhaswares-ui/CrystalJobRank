using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Models;

public sealed class MatchRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CompletedAtUtc { get; set; }
    public CombatJob LocalJob { get; set; }
    public MatchOutcome Outcome { get; set; }
    public MatchQueue Queue { get; set; }
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

    public MatchSubmission ToSubmission() => new(
        Id.ToString("N"),
        DateTime.SpecifyKind(CompletedAtUtc, DateTimeKind.Utc),
        LocalJob,
        Outcome,
        Queue,
        TerritoryId,
        DurationSeconds,
        LocalStats);
}

public sealed class PlayerScoreboardRow
{
    public string Name { get; set; } = string.Empty;
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
    public int Version { get; set; } = 3;
    public int RatingRulesVersion { get; set; } = RatingEngine.RulesVersion;
    public List<MatchRecord> Matches { get; set; } = [];
    public Dictionary<CombatJob, int> CurrentRatingEpochs { get; set; } = [];
    public Dictionary<CombatJob, JobLifetimeStats> LifetimeByJob { get; set; } = [];
    public Dictionary<CombatRole, RoleStreakProgress> RoleStreaks { get; set; } = [];
}
