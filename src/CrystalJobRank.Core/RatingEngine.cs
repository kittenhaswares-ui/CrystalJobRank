namespace CrystalJobRank.Core;

/// <summary>
/// A transparent Elo-like estimate against a fixed 1500 baseline.
/// It intentionally uses only match outcome; scoreboard values never affect rating.
/// </summary>
public static class RatingEngine
{
    public const int RulesVersion = 3;
    public const int InitialRating = 1500;
    public const int BaselineRating = 1500;
    public const int RatingScale = 2000;
    public const int ProvisionalMatches = 10;
    public const int ProvisionalK = 64;
    public const int EstablishedK = 32;
    public const int MinimumRating = 0;
    public const int MaximumRating = 3000;

    public static RatingState Empty(CombatJob job) => new(job, InitialRating, 0, 0, 0);

    public static RatingChange Apply(RatingState state, MatchOutcome outcome)
    {
        if (!CombatJobs.All.Contains(state.Job))
        {
            throw new ArgumentException("A rating cannot be calculated for an invalid job.", nameof(state));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A valid match outcome is required.");
        }

        var expected = EstimatedWinProbability(state.Rating);
        var actual = outcome == MatchOutcome.Win ? 1d : 0d;
        var k = state.Matches < ProvisionalMatches ? ProvisionalK : EstablishedK;
        var delta = (int)Math.Round(k * (actual - expected), MidpointRounding.AwayFromZero);

        // A valid result must always visibly move the number.
        if (delta == 0)
        {
            delta = outcome == MatchOutcome.Win ? 1 : -1;
        }

        var after = Math.Clamp(state.Rating + delta, MinimumRating, MaximumRating);
        delta = after - state.Rating;

        return new RatingChange(
            state.Rating,
            after,
            delta,
            state.Matches + 1,
            state.Wins + (outcome == MatchOutcome.Win ? 1 : 0),
            state.Losses + (outcome == MatchOutcome.Loss ? 1 : 0));
    }

    public static RatingState Replay(CombatJob job, IEnumerable<MatchOutcome> outcomes)
    {
        var state = Empty(job);
        foreach (var outcome in outcomes)
        {
            var change = Apply(state, outcome);
            state = new RatingState(job, change.After, change.MatchesAfter, change.WinsAfter, change.LossesAfter);
        }

        return state;
    }

    public static RatingState ReplayEpoch(
        CombatJob job,
        int epoch,
        IEnumerable<RatingEvent> events) => Replay(
            job,
            events
                .Where(x => x.Job == job && x.Epoch == epoch && IsRatedQueue(x.Queue))
                .Select(x => x.Outcome));

    public static double EstimatedWinProbability(int rating) =>
        1d / (1d + Math.Pow(10d, (BaselineRating - rating) / (double)RatingScale));

    public static bool IsRatedQueue(MatchQueue queue) =>
        queue is MatchQueue.Casual or MatchQueue.Ranked;
}
