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

public sealed class PluginData
{
    public int Version { get; set; } = 1;
    public List<MatchRecord> Matches { get; set; } = [];
}

