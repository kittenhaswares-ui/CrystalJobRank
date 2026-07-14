using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
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
    ("v1 history migrates and reset survives JSON reload", PluginDataMigrationAndResetRoundTrip),
    ("official job abbreviations parse safely", OfficialJobAbbreviations),
    ("invalid match outcomes are rejected", InvalidOutcomeRejected),
    ("rating caps and movement directions are safe", RatingCaps),
    ("invalid rating jobs are rejected", InvalidRatingJobRejected),
    ("validation rejects custom identity input", ValidationRejectsInvalidName),
    ("submission validation rejects invalid enums", SubmissionValidationRejectsInvalidEnums),
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

static void PluginDataMigrationAndResetRoundTrip()
{
    var start = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
    var data = new PluginData
    {
        Version = 1,
        RatingRulesVersion = 0,
        Matches =
        [
            NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Casual, start),
            NewMatch(CombatJob.DRK, MatchOutcome.Loss, MatchQueue.Ranked, start.AddMinutes(1)),
            NewMatch(CombatJob.SGE, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(2)),
        ],
    };

    True(PluginDataMigrations.Apply(data));
    Equal(PluginDataMigrations.CurrentSchemaVersion, data.Version);
    Equal(RatingEngine.RulesVersion, data.RatingRulesVersion);
    Equal(3, data.Matches.Count);
    Equal(32, data.Matches[0].RatingDelta);
    Equal(1532, data.Matches[1].RatingBefore);
    Equal(1499, data.Matches[1].RatingAfter);
    Equal(1532, data.Matches[2].RatingAfter);

    var events = data.Matches.Select(ToEvent).ToArray();
    True(RatingEpochs.TryReset(data.CurrentRatingEpochs, events, CombatJob.DRK));
    var firstAfterReset = NewMatch(CombatJob.DRK, MatchOutcome.Win, MatchQueue.Ranked, start.AddMinutes(3));
    firstAfterReset.RatingEpoch = RatingEpochs.Current(data.CurrentRatingEpochs, CombatJob.DRK);
    data.Matches.Add(firstAfterReset);
    PluginDataMigrations.RecalculateMatchRatings(data.Matches);
    Equal(4, data.Matches.Count);
    Equal(1500, firstAfterReset.RatingBefore);
    Equal(1532, firstAfterReset.RatingAfter);

    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var reloaded = JsonSerializer.Deserialize<PluginData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("PluginData JSON round-trip returned null.");
    Equal(1, RatingEpochs.Current(reloaded.CurrentRatingEpochs, CombatJob.DRK));
    Equal(4, reloaded.Matches.Count);
    True(!PluginDataMigrations.Apply(reloaded));

    var reloadedDrk = RatingEngine.ReplayEpoch(
        CombatJob.DRK,
        RatingEpochs.Current(reloaded.CurrentRatingEpochs, CombatJob.DRK),
        reloaded.Matches.OrderBy(x => x.CompletedAtUtc).Select(ToEvent));
    Equal(1, reloadedDrk.Matches);
    Equal(1532, reloadedDrk.Rating);
}

static MatchRecord NewMatch(
    CombatJob job,
    MatchOutcome outcome,
    MatchQueue queue,
    DateTime completedAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        CompletedAtUtc = completedAtUtc,
        LocalJob = job,
        Outcome = outcome,
        Queue = queue,
        DurationSeconds = 60,
    };

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
