using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using CrystalJobRank.Plugin.Services;
using Dalamud.Plugin.Services;
using System.Net;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("approved rating examples match the Beta prior", ApprovedRatingExamples),
    ("integer rating vectors are cross-language deterministic", CrossLanguageRatingVectors),
    ("wins increase and losses decrease the exact rating", RatingMovement),
    ("rating is independent of result order", OrderIndependentReplay),
    ("ratings remain inside their mathematical bounds", FormulaBounds),
    ("replay matches direct totals", ReplayMatchesDirectTotals),
    ("jobs remain independent", JobsRemainIndependent),
    ("scoreboard performance never changes rating", PerformanceDoesNotAffectRating),
    ("provisional status ends after match ten", ProvisionalBoundary),
    ("casual and ranked queues affect rating", MatchmadeQueuesAreRated),
    ("rating epochs ignore prior outcomes", EpochReset),
    ("job reset epochs are isolated and repeat-safe", ResetEpochManagement),
    ("character rating epochs isolate the same job across characters", CharacterEpochIsolation),
    ("match store reset and duplicate checks stay character-scoped", MatchStoreCharacterScoping),
    ("v4 update resets ratings, peaks, and current streaks exactly once", OneTimeV4RatingReset),
    ("v1 history migrates in stages and remains idempotent", V1MigrationRoundTrip),
    ("v2 visible rating peak resets for the v4 season", StoredPeakResetsForV4Season),
    ("fresh v4 data is never reset", FreshV4DoesNotReset),
    ("v4 migration normalizes null collections without a reset", NullCollectionsAreNormalized),
    ("future plugin schema is rejected before mutation", FutureSchemaRejected),
    ("every combat job maps to exactly one role", CombatRoleMapping),
    ("lifetime job records preserve peaks and local bests", LifetimeRecords),
    ("role streaks are queue-aware and role-isolated", RoleStreakAchievements),
    ("scoreboard validation rejects implausible local stats", ScoreboardStatsValidation),
    ("official job abbreviations parse safely", OfficialJobAbbreviations),
    ("invalid match outcomes are rejected", InvalidOutcomeRejected),
    ("rating caps and movement directions are safe", RatingCaps),
    ("invalid rating jobs are rejected", InvalidRatingJobRejected),
    ("character identity contract normalizes and rejects unsafe values", CharacterIdentityValidation),
    ("automatic match keys are deterministic and use only local result fields", DeterministicAutomaticMatchKey),
    ("submission validation rejects invalid enums", SubmissionValidationRejectsInvalidEnums),
    ("leaderboard outbox stays ordered, bounded, and endpoint-bound", LeaderboardOutboxOrderingAndBinding),
    ("leaderboard retry backoff and HTTP classification are bounded", LeaderboardRetryBackoff),
    ("leaderboard outbox retry state survives JSON persistence", LeaderboardOutboxPersistence),
    ("serial persistence queue stays ordered and drains safely", SerialPersistenceQueue),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} checks passed.");
return failed == 0 ? 0 : 1;

static void ApprovedRatingExamples()
{
    Equal(1500, RatingEngine.CalculateRating(0, 0));
    Equal(1524, RatingEngine.CalculateRating(1, 0));
    Equal(1540, RatingEngine.CalculateRating(6, 4));
    Equal(1660, RatingEngine.CalculateRating(9, 1));
    Equal(1700, RatingEngine.CalculateRating(10, 0));
    Equal(1500, RatingEngine.CalculateRating(5, 5));
    Equal(1300, RatingEngine.CalculateRating(0, 10));

    var initial = RatingEngine.Empty(CombatJob.DRG);
    var win = RatingEngine.Apply(initial, MatchOutcome.Win);
    var loss = RatingEngine.Apply(initial, MatchOutcome.Loss);
    Equal(24, win.Delta);
    Equal(-24, loss.Delta);
    Equal(RatingEngine.InitialRating, win.Before);
}

static void CrossLanguageRatingVectors()
{
    // These vectors can be shared verbatim with the Worker. In particular,
    // +/-687.5 proves midpoint ties are rounded away from the 1500 baseline.
    (int Wins, int Losses, int Expected)[] vectors =
    [
        (0, 0, 1500),
        (1, 0, 1524),
        (0, 1, 1476),
        (2, 22, 1187),
        (22, 2, 1813),
        (0, 88, 812),
        (88, 0, 2188),
        (123, 77, 1692),
        (500, 500, 1500),
    ];

    foreach (var vector in vectors)
    {
        Equal(vector.Expected, RatingEngine.CalculateRating(vector.Wins, vector.Losses));
    }
}

static void RatingMovement()
{
    for (var wins = 0; wins <= 100; wins++)
    {
        for (var losses = 0; losses <= 100; losses++)
        {
            var exact = RatingEngine.CalculateExactRating(wins, losses);
            True(RatingEngine.CalculateExactRating(wins + 1, losses) > exact);
            True(RatingEngine.CalculateExactRating(wins, losses + 1) < exact);

            // Integer display is monotonic. At very large samples a sub-point
            // exact movement can legitimately round to the same integer.
            var displayed = RatingEngine.CalculateRating(wins, losses);
            True(RatingEngine.CalculateRating(wins + 1, losses) >= displayed);
            True(RatingEngine.CalculateRating(wins, losses + 1) <= displayed);
        }
    }

    Equal(
        RatingEngine.CalculateRating(1_000_000, 0),
        RatingEngine.CalculateRating(1_000_001, 0));
}

static void OrderIndependentReplay()
{
    MatchOutcome[] first =
    [
        MatchOutcome.Win,
        MatchOutcome.Win,
        MatchOutcome.Loss,
        MatchOutcome.Win,
        MatchOutcome.Loss,
    ];
    MatchOutcome[] second =
    [
        MatchOutcome.Loss,
        MatchOutcome.Win,
        MatchOutcome.Loss,
        MatchOutcome.Win,
        MatchOutcome.Win,
    ];

    Equal(RatingEngine.Replay(CombatJob.SCH, first), RatingEngine.Replay(CombatJob.SCH, second));
}

static void FormulaBounds()
{
    (int Wins, int Losses)[] samples =
    [
        (0, 0),
        (1, 0),
        (0, 1),
        (10, 0),
        (0, 10),
        (10_000, 1),
        (1, 10_000),
        (1_000_000, 0),
        (0, 1_000_000),
    ];

    foreach (var sample in samples)
    {
        var exact = RatingEngine.CalculateExactRating(sample.Wins, sample.Losses);
        var displayed = RatingEngine.CalculateRating(sample.Wins, sample.Losses);
        True(exact > RatingEngine.MinimumRating);
        True(exact < RatingEngine.MaximumRating);
        True(displayed >= RatingEngine.MinimumRating);
        True(displayed <= RatingEngine.MaximumRating);
    }
}

static void ReplayMatchesDirectTotals()
{
    for (var wins = 0; wins <= 30; wins++)
    {
        for (var losses = 0; losses <= 30; losses++)
        {
            var outcomes = Enumerable.Repeat(MatchOutcome.Win, wins)
                .Concat(Enumerable.Repeat(MatchOutcome.Loss, losses));
            Equal(
                RatingEngine.FromResults(CombatJob.PCT, wins, losses),
                RatingEngine.Replay(CombatJob.PCT, outcomes));
        }
    }
}

static void JobsRemainIndependent()
{
    var pld = RatingEngine.Replay(CombatJob.PLD, [MatchOutcome.Win, MatchOutcome.Win]);
    var war = RatingEngine.Replay(CombatJob.WAR, [MatchOutcome.Loss]);
    True(pld.Rating > RatingEngine.InitialRating);
    True(war.Rating < RatingEngine.InitialRating);
    Equal(2, pld.Matches);
    Equal(1, war.Matches);
}

static void PerformanceDoesNotAffectRating()
{
    var state = RatingEngine.Empty(CombatJob.BLM);
    var first = RatingEngine.Apply(state, MatchOutcome.Win);
    var second = RatingEngine.Apply(state, MatchOutcome.Win);
    Equal(first, second);
}

static void ProvisionalBoundary()
{
    var ninth = RatingEngine.FromResults(CombatJob.SGE, 5, 4);
    var tenth = RatingEngine.Apply(ninth, MatchOutcome.Win);
    var established = RatingEngine.FromResults(CombatJob.SGE, 5, 5);
    True(ninth.IsProvisional);
    True(!tenth.IsProvisionalAfter);
    True(!established.IsProvisional);

    var provisionalRow = new LeaderboardRow(0, "Valid Name", "Phoenix", CombatJob.SGE, 1524, 1, 1, 0, 1);
    var establishedRow = new LeaderboardRow(1, "Valid Name", "Phoenix", CombatJob.SGE, 1600, 10, 8, 2, 0.8);
    True(provisionalRow.IsProvisional);
    True(!establishedRow.IsProvisional);
}

static void MatchmadeQueuesAreRated()
{
    True(RatingEngine.IsRatedQueue(MatchQueue.Ranked));
    True(RatingEngine.IsRatedQueue(MatchQueue.Casual));
    True(!RatingEngine.IsRatedQueue(MatchQueue.Custom));
    True(!RatingEngine.IsRatedQueue(MatchQueue.Unknown));

    var initial = RatingEngine.Empty(CombatJob.DNC);
    Equal(24, RatingEngine.Apply(initial, MatchOutcome.Win, MatchQueue.Casual).Delta);
    Equal(24, RatingEngine.Apply(initial, MatchOutcome.Win, MatchQueue.Ranked).Delta);
    Equal(0, RatingEngine.Apply(initial, MatchOutcome.Win, MatchQueue.Custom).Delta);
    Equal(0, RatingEngine.Apply(initial, MatchOutcome.Win, MatchQueue.Unknown).Delta);
}

static void EpochReset()
{
    RatingEvent[] events =
    [
        new(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, 0),
        new(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, 0),
        new(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, 1),
        new(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, 1),
        new(CombatJob.SGE, MatchOutcome.Loss, MatchQueue.Ranked, 1),
    ];
    var resetDrk = RatingEngine.ReplayEpoch(CombatJob.DRK, 1, events);
    Equal(2, resetDrk.Matches);
    Equal(2, resetDrk.Wins);
    Equal(1548, resetDrk.Rating);
}

static void ResetEpochManagement()
{
    RatingEvent[] events =
    [
        new(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, 0),
        new(CombatJob.SGE, MatchOutcome.Win, MatchQueue.Ranked, 0),
        new(CombatJob.PLD, MatchOutcome.Win, MatchQueue.Casual, 0),
    ];
    var epochs = new Dictionary<CombatJob, int>();

    True(RatingEpochs.TryReset(epochs, events, CombatJob.DRK));
    Equal(1, RatingEpochs.Current(epochs, CombatJob.DRK));
    Equal(0, RatingEpochs.Current(epochs, CombatJob.SGE));
    True(!RatingEpochs.TryReset(epochs, events, CombatJob.DRK));

    var resetAll = RatingEpochs.ResetAll(epochs, events);
    Equal(2, resetAll.Count);
    Equal(CombatJob.PLD, resetAll[0]);
    Equal(CombatJob.SGE, resetAll[1]);
    Equal(1, RatingEpochs.Current(epochs, CombatJob.SGE));
    Equal(1, RatingEpochs.Current(epochs, CombatJob.PLD));
}

static void CharacterEpochIsolation()
{
    var start = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);
    var data = new PluginData
    {
        Version = 4,
        CurrentRatingEpochs = new Dictionary<CombatJob, int> { [CombatJob.DRK] = 4 },
        Matches =
        [
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start, epoch: 4), "Alpha One", 74, "Phoenix"),
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(1), epoch: 4), "Beta Two", 75, "Siren"),
        ],
    };
    PluginDataMigrations.RecalculateMatchRatings(data.Matches);

    True(PluginDataMigrations.Apply(data));
    Equal(0, data.CurrentRatingEpochs.Count);
    Equal(4, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    Equal(4, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Beta Two", 75, CombatJob.DRK));
    True(!PluginDataMigrations.Apply(data));

    Equal(5, CharacterRatingEpochs.Advance(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    Equal(5, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    Equal(4, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Beta Two", 75, CombatJob.DRK));
}

static void OneTimeV4RatingReset()
{
    var start = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
    var data = new PluginData
    {
        Version = 2,
        RatingRulesVersion = RatingEngine.RulesVersion,
        CurrentRatingEpochs = new Dictionary<CombatJob, int>
        {
            [CombatJob.DRK] = 1,
            [CombatJob.PLD] = 2,
        },
        LifetimeByJob = new Dictionary<CombatJob, JobLifetimeStats>
        {
            [CombatJob.DRK] = new() { HighestRating = 2100, HighestKills = 7 },
        },
        RoleStreaks = new Dictionary<CombatRole, RoleStreakProgress>
        {
            [CombatRole.Tank] = new()
            {
                CurrentWinStreak = 3,
                BestWinStreak = 5,
                CurrentDeathlessStreak = 2,
                BestDeathlessStreak = 4,
            },
        },
        Matches =
        [
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start, epoch: 0)),
            // Deliberately ahead of CurrentRatingEpochs to prove the update
            // always starts strictly after every historical rated epoch.
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(1), epoch: 2)),
            WithIdentity(NewMatch(CombatJob.SGE, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(2), epoch: 0)),
            WithIdentity(NewMatch(CombatJob.PLD, MatchOutcome.Win, MatchQueue.Custom, start.AddMinutes(3), epoch: 2)),
        ],
    };
    var ids = data.Matches.Select(x => x.Id).ToArray();
    var matchEpochs = data.Matches.Select(x => x.RatingEpoch).ToArray();

    True(PluginDataMigrations.Apply(data));
    Equal(PluginDataMigrations.CurrentSchemaVersion, data.Version);
    Equal(RatingEngine.RulesVersion, data.RatingRulesVersion);
    Equal(4, data.Matches.Count);
    True(ids.SequenceEqual(data.Matches.Select(x => x.Id)));
    True(matchEpochs.SequenceEqual(data.Matches.Select(x => x.RatingEpoch)));
    Equal(0, data.CurrentRatingEpochs.Count);
    Equal(3, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    Equal(1, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.SGE));
    Equal(0, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.PLD));
    Equal(RatingEngine.InitialRating, data.LifetimeByJob[CombatJob.DRK].HighestRating);
    Equal(7, data.LifetimeByJob[CombatJob.DRK].HighestKills);
    Equal(0, data.RoleStreaks[CombatRole.Tank].CurrentWinStreak);
    Equal(5, data.RoleStreaks[CombatRole.Tank].BestWinStreak);
    Equal(0, data.RoleStreaks[CombatRole.Tank].CurrentDeathlessStreak);
    Equal(4, data.RoleStreaks[CombatRole.Tank].BestDeathlessStreak);

    var currentDrk = RatingEngine.ReplayEpoch(
        CombatJob.DRK,
        CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK),
        data.Matches.Select(ToEvent));
    Equal(RatingEngine.InitialRating, currentDrk.Rating);
    Equal(0, currentDrk.Matches);

    True(!PluginDataMigrations.Apply(data));
    Equal(3, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
}

static void V1MigrationRoundTrip()
{
    var start = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
    var data = new PluginData
    {
        Version = 1,
        RatingRulesVersion = 0,
        Matches =
        [
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start, epoch: 7)),
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(1), epoch: 7)),
            WithIdentity(NewMatch(CombatJob.SGE, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(2), epoch: 9)),
        ],
    };

    True(PluginDataMigrations.Apply(data));
    Equal(PluginDataMigrations.CurrentSchemaVersion, data.Version);
    Equal(RatingEngine.RulesVersion, data.RatingRulesVersion);
    Equal(3, data.Matches.Count);
    True(data.Matches.All(x => x.RatingEpoch == 0));
    Equal(24, data.Matches[0].RatingDelta);
    Equal(1524, data.Matches[1].RatingBefore);
    Equal(1500, data.Matches[1].RatingAfter);
    Equal(1524, data.Matches[2].RatingAfter);
    Equal(1, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    Equal(1, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.SGE));
    Equal(RatingEngine.InitialRating, data.LifetimeByJob[CombatJob.DRK].HighestRating);

    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var reloaded = JsonSerializer.Deserialize<PluginData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("PluginData JSON round-trip returned null.");
    Equal(1, CharacterRatingEpochs.Current(reloaded.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    Equal(3, reloaded.Matches.Count);
    True(!PluginDataMigrations.Apply(reloaded));
    Equal(1, CharacterRatingEpochs.Current(reloaded.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
}

static void StoredPeakResetsForV4Season()
{
    var start = new DateTime(2026, 7, 14, 12, 30, 0, DateTimeKind.Utc);
    var casualLoss = NewMatch(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Casual, start);
    casualLoss.RatingBefore = 1500;
    casualLoss.RatingAfter = 1500;
    var rankedWin = NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(1));
    rankedWin.RatingBefore = 1500;
    rankedWin.RatingAfter = 1532;
    var data = new PluginData
    {
        Version = 2,
        RatingRulesVersion = 2,
        Matches = [casualLoss, rankedWin],
    };

    True(PluginDataMigrations.Apply(data));
    Equal(1476, casualLoss.RatingAfter);
    Equal(1476, rankedWin.RatingBefore);
    Equal(1500, rankedWin.RatingAfter);
    Equal(RatingEngine.InitialRating, data.LifetimeByJob[CombatJob.DRK].HighestRating);
    True(!PluginDataMigrations.Apply(data));
}

static void FreshV4DoesNotReset()
{
    var data = new PluginData
    {
        Version = 4,
        CurrentRatingEpochs = new Dictionary<CombatJob, int> { [CombatJob.DRK] = 4 },
        Matches =
        [
            WithIdentity(NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, DateTime.UtcNow, epoch: 4)),
        ],
    };
    PluginDataMigrations.RecalculateMatchRatings(data.Matches);

    True(PluginDataMigrations.Apply(data));
    Equal(4, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
    True(!PluginDataMigrations.Apply(data));
    Equal(4, CharacterRatingEpochs.Current(data.CurrentCharacterRatingEpochs, "Alpha One", 74, CombatJob.DRK));
}

static void NullCollectionsAreNormalized()
{
    var data = new PluginData
    {
        Version = 5,
        Matches = null!,
        CurrentRatingEpochs = null!,
        CurrentCharacterRatingEpochs = null!,
        LifetimeByJob = null!,
        RoleStreaks = null!,
    };

    True(PluginDataMigrations.Apply(data));
    Equal(0, data.Matches.Count);
    Equal(0, data.CurrentRatingEpochs.Count);
    Equal(0, data.CurrentCharacterRatingEpochs.Count);
    Equal(0, data.LifetimeByJob.Count);
    Equal(0, data.RoleStreaks.Count);
    True(!PluginDataMigrations.Apply(data));
}

static void FutureSchemaRejected()
{
    var data = new PluginData
    {
        Version = 6,
        Matches = null!,
        CurrentRatingEpochs = null!,
        CurrentCharacterRatingEpochs = null!,
        LifetimeByJob = null!,
        RoleStreaks = null!,
    };
    var threw = false;
    try
    {
        PluginDataMigrations.Apply(data);
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    True(threw);
    True(data.Matches is null);
    True(data.CurrentRatingEpochs is null);
    True(data.CurrentCharacterRatingEpochs is null);
    True(data.LifetimeByJob is null);
    True(data.RoleStreaks is null);
}

static void CombatRoleMapping()
{
    var groups = CombatJobs.All.GroupBy(CombatJobs.RoleOf).ToDictionary(x => x.Key, x => x.Count());
    Equal(4, groups[CombatRole.Tank]);
    Equal(4, groups[CombatRole.Healer]);
    Equal(13, groups[CombatRole.Dps]);
    Equal(CombatRole.Unknown, CombatJobs.RoleOf(CombatJob.Unknown));
    Equal(CombatRole.Unknown, CombatJobs.RoleOf((CombatJob)999));
    Equal(CombatJobs.All.Count, groups.Values.Sum());
}

static void LifetimeRecords()
{
    var start = new DateTime(2026, 7, 14, 13, 0, 0, DateTimeKind.Utc);
    var first = NewMatch(
        CombatJob.DRK,
        MatchOutcome.Win,
        MatchQueue.Ranked,
        start,
        new ScoreboardStats(4, 1, 3, 500_000, 420_000, 8_000, 60));
    first.RatingBefore = 1900;
    first.RatingAfter = 1932;
    var second = NewMatch(
        CombatJob.DRK,
        MatchOutcome.Loss,
        MatchQueue.Casual,
        start.AddMinutes(1),
        new ScoreboardStats(7, 2, 4, 610_000, 700_000, 12_000, 70));
    second.RatingBefore = 2050;
    second.RatingAfter = 2017;
    var custom = NewMatch(
        CombatJob.DRK,
        MatchOutcome.Win,
        MatchQueue.Custom,
        start.AddMinutes(2),
        new ScoreboardStats(9, 0, 5, 800_000, 650_000, 30_000, 80));
    var invalid = NewMatch(
        CombatJob.DRK,
        MatchOutcome.Win,
        MatchQueue.Ranked,
        start.AddMinutes(3),
        new ScoreboardStats(1000, 0, 0, 99_000_000, 0, 0, 0));
    invalid.RatingBefore = 1800;
    invalid.RatingAfter = 1832;

    var rebuilt = LifetimeProgressCalculator.Rebuild([first, second, custom, invalid]);
    var stats = rebuilt.LifetimeByJob[CombatJob.DRK];
    Equal(2050, stats.HighestRating);
    Equal(9, stats.HighestKills);
    Equal(800_000, stats.HighestDamageDealt);
    Equal(700_000, stats.HighestDamageTaken);
    Equal(30_000, stats.HighestHealing);

    var merged = LifetimeProgressCalculator.MergeLifetime(
        new Dictionary<CombatJob, JobLifetimeStats>
        {
            [CombatJob.DRK] = new() { HighestRating = 2200, HighestKills = 8 },
        },
        rebuilt.LifetimeByJob);
    Equal(2200, merged[CombatJob.DRK].HighestRating);
    Equal(9, merged[CombatJob.DRK].HighestKills);
}

static void RoleStreakAchievements()
{
    var start = new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc);
    var matches = new[]
    {
        NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start, Stats(deaths: 0)),
        NewMatch(CombatJob.BLM, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(1), Stats(deaths: 1)),
        NewMatch(CombatJob.PLD, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(2), Stats(deaths: 0)),
        NewMatch(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Custom, start.AddMinutes(3), Stats(deaths: 5)),
        NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(4), Stats(deaths: 0)),
        NewMatch(CombatJob.SGE, MatchOutcome.Loss, MatchQueue.Casual, start.AddMinutes(5), Stats(deaths: 0)),
        NewMatch(CombatJob.WAR, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(6), Stats(deaths: 0)),
        NewMatch(CombatJob.GNB, MatchOutcome.Win, MatchQueue.Unknown, start.AddMinutes(7), Stats(deaths: 0)),
        NewMatch(CombatJob.PLD, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(8), Stats(deaths: 2)),
    };

    var progress = LifetimeProgressCalculator.Rebuild(matches).RoleStreaks;
    var tank = progress[CombatRole.Tank];
    Equal(1, tank.CurrentWinStreak);
    Equal(3, tank.BestWinStreak);
    Equal(0, tank.CurrentDeathlessStreak);
    Equal(4, tank.BestDeathlessStreak);
    Equal(1, progress[CombatRole.Dps].CurrentWinStreak);
    Equal(0, progress[CombatRole.Dps].CurrentDeathlessStreak);
    Equal(0, progress[CombatRole.Healer].CurrentWinStreak);
    Equal(1, progress[CombatRole.Healer].BestDeathlessStreak);
    True(AchievementThresholds.WinStreak.SequenceEqual([3, 5, 10, 20]));
    True(AchievementThresholds.DeathlessStreak.SequenceEqual([1, 3, 5, 10, 20]));
}

static void ScoreboardStatsValidation()
{
    Validation.ValidateScoreboardStats(new ScoreboardStats(100, 100, 100, 20_000_000, 20_000_000, 20_000_000, 1800));

    var threw = false;
    try
    {
        Validation.ValidateScoreboardStats(new ScoreboardStats(-1, 0, 0, 0, 0, 0, 0));
    }
    catch (ArgumentException)
    {
        threw = true;
    }
    True(threw);
    True(!Validation.AreScoreboardStatsPlausible(new ScoreboardStats(0, 0, 0, 20_000_001, 0, 0, 0)));
}

static MatchRecord NewMatch(
    CombatJob job,
    MatchOutcome outcome,
    MatchQueue queue,
    DateTime completedAtUtc,
    ScoreboardStats? stats = null,
    int epoch = 0) => new()
    {
        Id = Guid.NewGuid(),
        CompletedAtUtc = completedAtUtc,
        LocalJob = job,
        Outcome = outcome,
        Queue = queue,
        DurationSeconds = 60,
        RatingEpoch = epoch,
        LocalStats = stats ?? Stats(),
    };

static MatchRecord WithIdentity(
    MatchRecord match,
    string characterName = "Alpha One",
    uint worldId = 74,
    string worldName = "Phoenix")
{
    match.CharacterName = characterName;
    match.WorldId = worldId;
    match.WorldName = worldName;
    return match;
}

static void MatchStoreCharacterScoping()
{
    var directory = Path.Combine(Path.GetTempPath(), "CrystalJobRankSelfTest", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new MatchStore(directory, new TestPluginLog());
        var start = DateTime.UtcNow.AddMinutes(-5);
        var alpha = WithIdentity(
            NewMatch(CombatJob.DNC, MatchOutcome.Win, MatchQueue.Casual, start),
            "Alpha One",
            74,
            "Coeurl");
        var beta = WithIdentity(
            NewMatch(CombatJob.DNC, MatchOutcome.Win, MatchQueue.Casual, start.AddMinutes(1)),
            "Beta Two",
            75,
            "Malboro");

        True(store.Add(alpha) is not null);
        True(store.Add(beta) is not null);
        True(store.ResetRating(CombatJob.DNC));

        var current = store.Ratings().Single(state => state.Job == CombatJob.DNC);
        Equal(RatingEngine.InitialRating, current.Rating);
        Equal(0, current.Matches);

        var json = File.ReadAllText(Path.Combine(directory, "matches.json"));
        var persisted = JsonSerializer.Deserialize<PluginData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("MatchStore persistence returned null.");
        Equal(0, CharacterRatingEpochs.Current(
            persisted.CurrentCharacterRatingEpochs,
            "Alpha One",
            74,
            CombatJob.DNC));
        Equal(1, CharacterRatingEpochs.Current(
            persisted.CurrentCharacterRatingEpochs,
            "Beta Two",
            75,
            CombatJob.DNC));

        var duplicate = WithIdentity(
            NewMatch(CombatJob.DNC, MatchOutcome.Win, MatchQueue.Casual, start.AddMinutes(1)),
            "Beta Two",
            75,
            "Malboro");
        True(store.Add(duplicate) is null);
        Equal(2, store.Snapshot().Count);
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static ScoreboardStats Stats(int deaths = 0) => new(0, deaths, 0, 0, 0, 0, 0);

static RatingEvent ToEvent(MatchRecord match) => new(
    match.LocalJob,
    match.Outcome,
    match.Queue,
    match.RatingEpoch);

static void OfficialJobAbbreviations()
{
    True(CombatJobs.TryParseAbbreviation("sge", out var sage));
    Equal(CombatJob.SGE, sage);
    True(CombatJobs.TryParseAbbreviation("DRK", out var darkKnight));
    Equal(CombatJob.DRK, darkKnight);
    True(!CombatJobs.TryParseAbbreviation("sage", out _));
    True(!CombatJobs.TryParseAbbreviation("1", out _));
    True(!CombatJobs.TryParseAbbreviation("unknown", out _));
}

static void InvalidOutcomeRejected()
{
    var threw = false;
    try
    {
        RatingEngine.Apply(RatingEngine.Empty(CombatJob.PLD), (MatchOutcome)99);
    }
    catch (ArgumentOutOfRangeException)
    {
        threw = true;
    }
    True(threw);
}

static void RatingCaps()
{
    Equal(RatingEngine.MaximumRating, RatingEngine.CalculateRating(1_000_000, 0));
    Equal(RatingEngine.MinimumRating, RatingEngine.CalculateRating(0, 1_000_000));
    True(RatingEngine.CalculateExactRating(1_000_000, 0) < RatingEngine.MaximumRating);
    True(RatingEngine.CalculateExactRating(0, 1_000_000) > RatingEngine.MinimumRating);
}

static void InvalidRatingJobRejected()
{
    var threw = false;
    try
    {
        RatingEngine.Apply(new RatingState((CombatJob)999, 1500, 0, 0, 0), MatchOutcome.Win);
    }
    catch (ArgumentException)
    {
        threw = true;
    }
    True(threw);
}

static void CharacterIdentityValidation()
{
    var identity = Validation.NormalizeCharacterIdentity("  A\u0301yla Sato  ", 74, "  Phoenix  ");
    Equal("Áyla Sato", identity.CharacterName);
    Equal(74u, identity.WorldId);
    Equal("Phoenix", identity.WorldName);

    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var requestJson = JsonSerializer.Serialize(identity, options);
    True(requestJson.Contains("\"characterName\"", StringComparison.Ordinal));
    True(requestJson.Contains("\"worldId\":74", StringComparison.Ordinal));
    True(requestJson.Contains("\"worldName\"", StringComparison.Ordinal));
    True(!requestJson.Contains("displayName", StringComparison.OrdinalIgnoreCase));
    Equal(identity, JsonSerializer.Deserialize<CharacterIdentity>(requestJson, options)!);

    var rowJson = JsonSerializer.Serialize(
        new LeaderboardRow(1, "Áyla Sato", "Phoenix", CombatJob.SGE, 1600, 4, 3, 1, 0.75),
        options);
    True(rowJson.Contains("\"characterName\"", StringComparison.Ordinal));
    True(rowJson.Contains("\"worldName\"", StringComparison.Ordinal));
    True(!rowJson.Contains("displayName", StringComparison.OrdinalIgnoreCase));

    Equal("Valid Name", Validation.NormalizeCharacterName("  Valid   Name  "));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("SingleAlias", 74, "Phoenix"));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("One Two Three", 74, "Phoenix"));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("Valid 😺", 74, "Phoenix"));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("Bad\nName", 74, "Phoenix"));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("Valid Name", 0, "Phoenix"));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("Valid Name", 65_536, "Phoenix"));
    ThrowsArgument(() => Validation.NormalizeCharacterIdentity("Valid Name", 74, "Bad_World"));
}

static void DeterministicAutomaticMatchKey()
{
    var completed = new DateTime(2026, 7, 15, 12, 34, 56, DateTimeKind.Utc);
    var stats = new ScoreboardStats(4, 1, 6, 654_321, 456_789, 123_456, 87);
    var first = WithIdentity(
        NewMatch(CombatJob.DNC, MatchOutcome.Win, MatchQueue.Casual, completed.AddMilliseconds(100), stats),
        "Alpha One",
        74,
        "Phoenix");
    first.TerritoryId = 1293;
    first.DurationSeconds = 321;
    first.Scoreboard =
    [
        new PlayerScoreboardRow { Name = "Other Player", WorldId = 75, World = "Siren", Job = CombatJob.DRK },
    ];

    var second = WithIdentity(
        NewMatch(CombatJob.DNC, MatchOutcome.Win, MatchQueue.Casual, completed.AddMilliseconds(900), stats),
        "Alpha One",
        74,
        "Phoenix");
    second.TerritoryId = first.TerritoryId;
    second.DurationSeconds = first.DurationSeconds;
    second.Scoreboard =
    [
        new PlayerScoreboardRow { Name = "Different Other", WorldId = 76, World = "Gilgamesh", Job = CombatJob.SGE },
    ];

    var firstSubmission = first.ToSubmission();
    var secondSubmission = second.ToSubmission();
    Equal(firstSubmission.MatchKey, secondSubmission.MatchKey);
    Equal(firstSubmission.CompletedAtUtc, secondSubmission.CompletedAtUtc);
    Equal(64, firstSubmission.MatchKey.Length);
    True(firstSubmission.MatchKey.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));

    second.LocalStats = second.LocalStats with { DamageDealt = second.LocalStats.DamageDealt + 1 };
    True(!string.Equals(firstSubmission.MatchKey, second.ToSubmission().MatchKey, StringComparison.Ordinal));
}

static void SubmissionValidationRejectsInvalidEnums()
{
    var threw = false;
    try
    {
        Validation.ValidateSubmission(new MatchSubmission(
            "invalid-enum",
            DateTime.UtcNow,
            CombatJob.DRG,
            (MatchOutcome)99,
            MatchQueue.Ranked,
            1,
            60,
            new ScoreboardStats(0, 0, 0, 0, 0, 0, 0)));
    }
    catch (ArgumentException)
    {
        threw = true;
    }
    True(threw);
}

static void LeaderboardOutboxOrderingAndBinding()
{
    var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
    var state = new LeaderboardOutboxState();
    True(state.Bind("https://leaderboard.example/"));

    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    Equal(LeaderboardEnqueueResult.Added, state.Enqueue(first, now));
    Equal(LeaderboardEnqueueResult.Added, state.Enqueue(second, now.AddSeconds(1)));
    Equal(LeaderboardEnqueueResult.Duplicate, state.Enqueue(first, now.AddSeconds(2)));
    Equal(first, state.Pending[0].MatchId);
    True(!state.Bind("https://leaderboard.example"));
    Equal(2, state.Pending.Count);
    True(!state.RemoveHead(second));
    True(state.RemoveHead(first));
    Equal(second, state.Pending[0].MatchId);

    while (state.Pending.Count < LeaderboardOutboxState.MaximumPending)
    {
        Equal(LeaderboardEnqueueResult.Added, state.Enqueue(Guid.NewGuid(), now));
    }
    Equal(LeaderboardEnqueueResult.Full, state.Enqueue(Guid.NewGuid(), now));
    Equal(second, state.Pending[0].MatchId);

    True(state.Bind("https://other-leaderboard.example"));
    Equal(LeaderboardOutboxState.MaximumPending, state.Pending.Count);
    True(state.Bind(string.Empty));
    Equal(string.Empty, state.ServerBaseUrl);
}

static void LeaderboardRetryBackoff()
{
    Equal(TimeSpan.FromSeconds(5), LeaderboardRetryPolicy.DelayAfterFailure(1));
    Equal(TimeSpan.FromSeconds(10), LeaderboardRetryPolicy.DelayAfterFailure(2));
    Equal(TimeSpan.FromSeconds(160), LeaderboardRetryPolicy.DelayAfterFailure(6));
    Equal(LeaderboardRetryPolicy.MaximumDelay, LeaderboardRetryPolicy.DelayAfterFailure(7));
    Equal(LeaderboardRetryPolicy.MaximumDelay, LeaderboardRetryPolicy.DelayAfterFailure(30));

    True(LeaderboardRetryPolicy.IsRetryable(null));
    True(LeaderboardRetryPolicy.IsRetryable(HttpStatusCode.RequestTimeout));
    True(LeaderboardRetryPolicy.IsRetryable(HttpStatusCode.TooManyRequests));
    True(LeaderboardRetryPolicy.IsRetryable(HttpStatusCode.InternalServerError));
    True(!LeaderboardRetryPolicy.IsRetryable(HttpStatusCode.BadRequest));
    True(!LeaderboardRetryPolicy.IsRetryable(HttpStatusCode.Unauthorized));
    True(!LeaderboardRetryPolicy.IsRetryable(HttpStatusCode.Conflict));
}

static void LeaderboardOutboxPersistence()
{
    var now = new DateTime(2026, 7, 15, 13, 0, 0, DateTimeKind.Utc);
    var state = new LeaderboardOutboxState();
    var match = Guid.NewGuid();
    state.Bind("https://leaderboard.example");
    state.Enqueue(match, now);
    state.Pending[0].AttemptCount = 4;
    state.Pending[0].NextAttemptUtc = now.AddSeconds(40);

    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var restored = JsonSerializer.Deserialize<LeaderboardOutboxState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Leaderboard outbox JSON round-trip returned null.");
    True(!restored.Normalize(now));
    Equal("https://leaderboard.example", restored.ServerBaseUrl);
    Equal(1, restored.Pending.Count);
    Equal(match, restored.Pending[0].MatchId);
    Equal(4, restored.Pending[0].AttemptCount);
    Equal(now.AddSeconds(40), restored.Pending[0].NextAttemptUtc);
}

static void SerialPersistenceQueue()
{
    var processed = new List<int>();
    var failures = new List<int>();
    var queue = new SerialWorkQueue<int>(
        value =>
        {
            if (value == 2) throw new InvalidOperationException("expected test failure");
            processed.Add(value);
        },
        (value, _) => failures.Add(value));

    True(queue.TryEnqueue(1));
    True(queue.TryEnqueue(2));
    True(queue.TryEnqueue(3));
    queue.Drain();

    True(processed.SequenceEqual([1, 3]));
    True(failures.SequenceEqual([2]));

    // A barrier does not close the queue. Disposal later drains work admitted
    // after the barrier and rejects only subsequent items.
    True(queue.TryEnqueue(4));
    queue.Dispose();
    queue.Dispose();
    True(processed.SequenceEqual([1, 3, 4]));
    True(!queue.TryEnqueue(5));
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void ThrowsArgument(Action action)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException("Expected ArgumentException.");
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Condition was false.");
}
