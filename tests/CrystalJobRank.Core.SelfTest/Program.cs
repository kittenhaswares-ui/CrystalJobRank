using CrystalJobRank.Core;

var tests = new (string Name, Action Run)[]
{
    ("initial win and loss are symmetric", InitialSymmetry),
    ("ratings are deterministic", DeterministicReplay),
    ("jobs remain independent", JobsRemainIndependent),
    ("scoreboard performance never changes rating", PerformanceDoesNotAffectRating),
    ("validation rejects custom identity input", ValidationRejectsInvalidName),
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
