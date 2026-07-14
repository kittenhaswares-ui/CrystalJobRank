namespace CrystalJobRank.Core;

/// <summary>
/// A transparent Elo-like estimate against a fixed 1500 baseline.
/// It intentionally uses only match outcome; scoreboard values never affect rating.
/// </summary>
public static class RatingEngine
{
    public const int InitialRating = 1500;
    public const int BaselineRating = 1500;
    public const int ProvisionalMatches = 10;
    public const int ProvisionalK = 72;
    public const int EstablishedK = 48;
    public const int MinimumRating = 0;
    public const int MaximumRating = 3000;

    public static RatingState Empty(CombatJob job) => new(job, InitialRating, 0, 0, 0);

    public static RatingChange Apply(RatingState state, MatchOutcome outcome)
    {
        if (state.Job == CombatJob.Unknown)
        {
            throw new ArgumentException("A rating cannot be calculated for an unknown job.", nameof(state));
        }

        var expected = 1d / (1d + Math.Pow(10d, (BaselineRating - state.Rating) / 400d));
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
}

