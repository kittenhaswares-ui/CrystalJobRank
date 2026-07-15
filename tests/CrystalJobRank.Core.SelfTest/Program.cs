using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using CrystalJobRank.Plugin.Services;
using System.Net;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("initial win and loss are symmetric", InitialSymmetry),
    ("ratings are deterministic", DeterministicReplay),
    ("jobs remain independent", JobsRemainIndependent),
    ("scoreboard performance never changes rating", PerformanceDoesNotAffectRating),
    ("rating rules are calibrated to the visible tiers", TierCalibration),
    ("provisional K changes after match ten", ProvisionalBoundary),
    ("casual and ranked queues affect rating", MatchmadeQueuesAreRated),
    ("rating epochs ignore prior outcomes", EpochReset),
    ("job reset epochs are isolated and repeat-safe", ResetEpochManagement),
    ("v2 update resets every local rating exactly once", OneTimeV3RatingReset),
    ("v1 history migrates in stages and remains idempotent", V1MigrationRoundTrip),
    ("v2 visible rating peak survives rule recalculation", StoredPeakSurvivesRuleMigration),
    ("fresh v3 data is never reset", FreshV3DoesNotReset),
    ("v3 migration normalizes null collections without a reset", NullCollectionsAreNormalized),
    ("future plugin schema is rejected before mutation", FutureSchemaRejected),
    ("every combat job maps to exactly one role", CombatRoleMapping),
    ("lifetime job records preserve peaks and local bests", LifetimeRecords),
    ("role streaks are queue-aware and role-isolated", RoleStreakAchievements),
    ("scoreboard validation rejects implausible local stats", ScoreboardStatsValidation),
    ("official job abbreviations parse safely", OfficialJobAbbreviations),
    ("invalid match outcomes are rejected", InvalidOutcomeRejected),
    ("rating caps and movement directions are safe", RatingCaps),
    ("invalid rating jobs are rejected", InvalidRatingJobRejected),
    ("validation rejects custom identity input", ValidationRejectsInvalidName),
    ("submission validation rejects invalid enums", SubmissionValidationRejectsInvalidEnums),
    ("leaderboard outbox stays ordered, bounded, and identity-bound", LeaderboardOutboxOrderingAndBinding),
    ("leaderboard retry backoff and HTTP classification are bounded", LeaderboardRetryBackoff),
    ("leaderboard outbox retry state survives JSON persistence", LeaderboardOutboxPersistence),
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

static void InitialSymmetry()
{
    var initial = RatingEngine.Empty(CombatJob.DRG);
    var win = RatingEngine.Apply(initial, MatchOutcome.Win);
    var loss = RatingEngine.Apply(initial, MatchOutcome.Loss);
    Equal(win.Delta, -loss.Delta);
    Equal(32, win.Delta);
    Equal(RatingEngine.InitialRating, win.Before);
}

static void DeterministicReplay()
{
    var outcomes = new[] { MatchOutcome.Win, MatchOutcome.Win, MatchOutcome.Loss, MatchOutcome.Win, MatchOutcome.Loss };
    Equal(RatingEngine.Replay(CombatJob.SCH, outcomes), RatingEngine.Replay(CombatJob.SCH, outcomes));
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

static void TierCalibration()
{
    Near(0.500, RatingEngine.EstimatedWinProbability(1500), 0.001);
    Near(0.529, RatingEngine.EstimatedWinProbability(1600), 0.001);
    Near(0.557, RatingEngine.EstimatedWinProbability(1700), 0.001);
    Near(0.586, RatingEngine.EstimatedWinProbability(1800), 0.001);
    Near(0.613, RatingEngine.EstimatedWinProbability(1900), 0.001);
    Near(0.640, RatingEngine.EstimatedWinProbability(2000), 0.001);
}

static void ProvisionalBoundary()
{
    var tenth = RatingEngine.Apply(new RatingState(CombatJob.SGE, 1500, 9, 5, 4), MatchOutcome.Win);
    var eleventh = RatingEngine.Apply(new RatingState(CombatJob.SGE, 1500, 10, 5, 5), MatchOutcome.Win);
    Equal(32, tenth.Delta);
    Equal(16, eleventh.Delta);
}

static void MatchmadeQueuesAreRated()
{
    True(RatingEngine.IsRatedQueue(MatchQueue.Ranked));
    True(RatingEngine.IsRatedQueue(MatchQueue.Casual));
    True(!RatingEngine.IsRatedQueue(MatchQueue.Custom));
    True(!RatingEngine.IsRatedQueue(MatchQueue.Unknown));
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
    Equal(1563, resetDrk.Rating);
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

static void OneTimeV3RatingReset()
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
            [CombatJob.DRK] = new() { HighestRating = 2100 },
        },
        Matches =
        [
            NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start, epoch: 0),
            // Deliberately ahead of CurrentRatingEpochs to prove the update
            // always starts strictly after every historical rated epoch.
            NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(1), epoch: 2),
            NewMatch(CombatJob.SGE, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(2), epoch: 0),
            NewMatch(CombatJob.PLD, MatchOutcome.Win, MatchQueue.Custom, start.AddMinutes(3), epoch: 2),
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
    Equal(3, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK));
    Equal(3, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.PLD));
    Equal(1, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.SGE));
    Equal(1, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.PCT));
    Equal(CombatJobs.All.Count, data.CurrentRatingEpochs.Count);
    Equal(2100, data.LifetimeByJob[CombatJob.DRK].HighestRating);

    var currentDrk = RatingEngine.ReplayEpoch(
        CombatJob.DRK,
        RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK),
        data.Matches.Select(ToEvent));
    Equal(RatingEngine.InitialRating, currentDrk.Rating);
    Equal(0, currentDrk.Matches);

    True(!PluginDataMigrations.Apply(data));
    Equal(3, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK));
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
            NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start, epoch: 7),
            NewMatch(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(1), epoch: 7),
            NewMatch(CombatJob.SGE, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(2), epoch: 9),
        ],
    };

    True(PluginDataMigrations.Apply(data));
    Equal(PluginDataMigrations.CurrentSchemaVersion, data.Version);
    Equal(RatingEngine.RulesVersion, data.RatingRulesVersion);
    Equal(3, data.Matches.Count);
    True(data.Matches.All(x => x.RatingEpoch == 0));
    Equal(32, data.Matches[0].RatingDelta);
    Equal(1532, data.Matches[1].RatingBefore);
    Equal(1499, data.Matches[1].RatingAfter);
    Equal(1532, data.Matches[2].RatingAfter);
    Equal(1, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK));
    Equal(1, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.SGE));
    Equal(1532, data.LifetimeByJob[CombatJob.DRK].HighestRating);

    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var reloaded = JsonSerializer.Deserialize<PluginData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("PluginData JSON round-trip returned null.");
    Equal(1, RatingEpochs.Current(reloaded.CurrentRatingEpochs, CombatJob.DRK));
    Equal(3, reloaded.Matches.Count);
    True(!PluginDataMigrations.Apply(reloaded));
    Equal(1, RatingEpochs.Current(reloaded.CurrentRatingEpochs, CombatJob.DRK));
}

static void StoredPeakSurvivesRuleMigration()
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
    Equal(1468, casualLoss.RatingAfter);
    Equal(1468, rankedWin.RatingBefore);
    Equal(1501, rankedWin.RatingAfter);
    Equal(1532, data.LifetimeByJob[CombatJob.DRK].HighestRating);
    True(!PluginDataMigrations.Apply(data));
}

static void FreshV3DoesNotReset()
{
    var data = new PluginData
    {
        Version = 3,
        CurrentRatingEpochs = new Dictionary<CombatJob, int> { [CombatJob.DRK] = 4 },
        Matches =
        [
            NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, DateTime.UtcNow, epoch: 4),
        ],
    };
    PluginDataMigrations.RecalculateMatchRatings(data.Matches);

    True(PluginDataMigrations.Apply(data));
    Equal(4, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK));
    True(!PluginDataMigrations.Apply(data));
    Equal(4, RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK));
}

static void NullCollectionsAreNormalized()
{
    var data = new PluginData
    {
        Version = 3,
        Matches = null!,
        CurrentRatingEpochs = null!,
        LifetimeByJob = null!,
        RoleStreaks = null!,
    };

    True(PluginDataMigrations.Apply(data));
    Equal(0, data.Matches.Count);
    Equal(0, data.CurrentRatingEpochs.Count);
    Equal(0, data.LifetimeByJob.Count);
    Equal(0, data.RoleStreaks.Count);
    True(!PluginDataMigrations.Apply(data));
}

static void FutureSchemaRejected()
{
    var data = new PluginData
    {
        Version = 4,
        Matches = null!,
        CurrentRatingEpochs = null!,
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
    var maxWin = RatingEngine.Apply(new RatingState(CombatJob.PCT, RatingEngine.MaximumRating, 20, 20, 0), MatchOutcome.Win);
    var maxLoss = RatingEngine.Apply(new RatingState(CombatJob.PCT, RatingEngine.MaximumRating, 20, 20, 0), MatchOutcome.Loss);
    var minLoss = RatingEngine.Apply(new RatingState(CombatJob.VPR, RatingEngine.MinimumRating, 20, 0, 20), MatchOutcome.Loss);
    var minWin = RatingEngine.Apply(new RatingState(CombatJob.VPR, RatingEngine.MinimumRating, 20, 0, 20), MatchOutcome.Win);
    Equal(0, maxWin.Delta);
    True(maxLoss.Delta < 0);
    Equal(0, minLoss.Delta);
    True(minWin.Delta > 0);
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

static void ValidationRejectsInvalidName()
{
    var threw = false;
    try
    {
        Validation.NormalizeDisplayName("<script>");
    }
    catch (ArgumentException)
    {
        threw = true;
    }
    True(threw);
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
    var player = Guid.NewGuid();
    var state = new LeaderboardOutboxState();
    True(state.Bind(player, "https://leaderboard.example/"));

    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    Equal(LeaderboardEnqueueResult.Added, state.Enqueue(first, now));
    Equal(LeaderboardEnqueueResult.Added, state.Enqueue(second, now.AddSeconds(1)));
    Equal(LeaderboardEnqueueResult.Duplicate, state.Enqueue(first, now.AddSeconds(2)));
    Equal(first, state.Pending[0].MatchId);
    True(!state.Bind(player, "https://leaderboard.example"));
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

    True(state.Bind(Guid.NewGuid(), "https://leaderboard.example"));
    Equal(0, state.Pending.Count);
    True(state.Bind(null, "https://leaderboard.example"));
    True(!state.PlayerId.HasValue);
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
    var player = Guid.NewGuid();
    var match = Guid.NewGuid();
    state.Bind(player, "https://leaderboard.example");
    state.Enqueue(match, now);
    state.Pending[0].AttemptCount = 4;
    state.Pending[0].NextAttemptUtc = now.AddSeconds(40);

    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var restored = JsonSerializer.Deserialize<LeaderboardOutboxState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Leaderboard outbox JSON round-trip returned null.");
    True(!restored.Normalize(now));
    Equal(player, restored.PlayerId!.Value);
    Equal(1, restored.Pending.Count);
    Equal(match, restored.Pending[0].MatchId);
    Equal(4, restored.Pending[0].AttemptCount);
    Equal(now.AddSeconds(40), restored.Pending[0].NextAttemptUtc);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Condition was false.");
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Expected {expected} ± {tolerance}, got {actual}.");
    }
}
