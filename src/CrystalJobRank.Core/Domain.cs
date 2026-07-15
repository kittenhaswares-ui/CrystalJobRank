namespace CrystalJobRank.Core;

public enum MatchOutcome
{
    Loss = 0,
    Win = 1,
}

public enum MatchQueue
{
    Unknown = 0,
    Casual = 1,
    Ranked = 2,
    Custom = 3,
}

public enum CombatRole
{
    Unknown = 0,
    Tank = 1,
    Dps = 2,
    Healer = 3,
}

public enum CombatJob
{
    Unknown = 0,
    PLD,
    WAR,
    DRK,
    GNB,
    WHM,
    SCH,
    AST,
    SGE,
    MNK,
    DRG,
    NIN,
    SAM,
    RPR,
    VPR,
    BRD,
    MCH,
    DNC,
    BLM,
    SMN,
    RDM,
    PCT,
}

public sealed record ScoreboardStats(
    int Kills,
    int Deaths,
    int Assists,
    int DamageDealt,
    int DamageTaken,
    int HpRestored,
    int TimeOnCrystalSeconds);

public sealed record RatingState(
    CombatJob Job,
    int Rating,
    int Matches,
    int Wins,
    int Losses)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

public sealed record RatingChange(
    int Before,
    int After,
    int Delta,
    int MatchesAfter,
    int WinsAfter,
    int LossesAfter);

public sealed record RatingEvent(
    CombatJob Job,
    MatchOutcome Outcome,
    MatchQueue Queue,
    int Epoch);

public sealed record MatchSubmission(
    string Fingerprint,
    DateTime CompletedAtUtc,
    CombatJob Job,
    MatchOutcome Outcome,
    MatchQueue Queue,
    ushort TerritoryId,
    ushort DurationSeconds,
    ScoreboardStats Stats);

public sealed record RegisterRequest(
    string CharacterName,
    uint WorldId,
    string WorldName);

public sealed record RegistrationResponse(Guid PlayerId, string ApiKey);

public sealed record LeaderboardRow(
    int Rank,
    string CharacterName,
    string WorldName,
    CombatJob Job,
    int Rating,
    int Matches,
    int Wins,
    int Losses,
    double WinRate);
