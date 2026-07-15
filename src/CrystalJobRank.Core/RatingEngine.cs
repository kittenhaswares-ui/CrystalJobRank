namespace CrystalJobRank.Core;

/// <summary>
/// A seasonal, job-specific rating derived only from wins and losses.
///
/// The model is equivalent to a Beta(20, 20) prior mapped onto a 500-2500
/// rating range:
///
///   exact rating = 1500 + 1000 * (wins - losses) / (wins + losses + 40)
///
/// It deliberately has no opponent-MMR input because Crystalline Conflict
/// matchmaking does not expose one. Scoreboard performance never affects it.
/// </summary>
public static class RatingEngine
{
    public const int RulesVersion = 4;
    public const int InitialRating = 1500;
    public const int PriorWins = 20;
    public const int PriorLosses = 20;
    public const int PriorMatches = PriorWins + PriorLosses;
    public const int RatingOffsetScale = 1000;
    public const int ProvisionalMatches = 10;
    public const int MinimumRating = InitialRating - RatingOffsetScale;
    public const int MaximumRating = InitialRating + RatingOffsetScale;

    public static RatingState Empty(CombatJob job) => FromResults(job, 0, 0);

    /// <summary>
    /// Creates the canonical state for a job and a season's result totals.
    /// </summary>
    public static RatingState FromResults(CombatJob job, int wins, int losses)
    {
        ValidateJob(job);
        ValidateResults(wins, losses);

        var matches = checked(wins + losses);
        return new RatingState(job, CalculateRating(wins, losses), matches, wins, losses);
    }

    /// <summary>
    /// Calculates the integer shown by every client and leaderboard.
    /// Fractional offsets are rounded to nearest with midpoint ties away from
    /// zero using integer arithmetic, so C#, JavaScript and SQL can agree.
    /// </summary>
    public static int CalculateRating(int wins, int losses)
    {
        ValidateResults(wins, losses);

        var difference = (long)wins - losses;
        var denominator = (long)wins + losses + PriorMatches;
        var numerator = RatingOffsetScale * difference;
        var roundedOffset = DivideRoundedAwayFromZero(numerator, denominator);

        return Math.Clamp(
            checked(InitialRating + (int)roundedOffset),
            MinimumRating,
            MaximumRating);
    }

    /// <summary>
    /// Returns the unrounded mathematical rating. A win always raises this
    /// value and a loss always lowers it. The displayed integer can eventually
    /// remain unchanged for one result once the sample is very large.
    /// </summary>
    public static double CalculateExactRating(int wins, int losses)
    {
        ValidateResults(wins, losses);

        var difference = (long)wins - losses;
        var denominator = (long)wins + losses + PriorMatches;
        return InitialRating + RatingOffsetScale * (difference / (double)denominator);
    }

    public static RatingChange Apply(RatingState state, MatchOutcome outcome)
    {
        ValidateState(state);
        ValidateOutcome(outcome);

        var winsAfter = checked(state.Wins + (outcome == MatchOutcome.Win ? 1 : 0));
        var lossesAfter = checked(state.Losses + (outcome == MatchOutcome.Loss ? 1 : 0));
        var before = CalculateRating(state.Wins, state.Losses);
        var after = CalculateRating(winsAfter, lossesAfter);

        return new RatingChange(
            before,
            after,
            after - before,
            checked(state.Matches + 1),
            winsAfter,
            lossesAfter);
    }

    /// <summary>
    /// Applies only matchmade Casual and Ranked results. Custom and Unknown
    /// queues are returned as a no-op and never enter the seasonal totals.
    /// </summary>
    public static RatingChange Apply(RatingState state, MatchOutcome outcome, MatchQueue queue)
    {
        ValidateState(state);
        ValidateOutcome(outcome);
        ValidateQueue(queue);

        if (IsRatedQueue(queue))
        {
            return Apply(state, outcome);
        }

        var rating = CalculateRating(state.Wins, state.Losses);
        return new RatingChange(
            rating,
            rating,
            0,
            state.Matches,
            state.Wins,
            state.Losses);
    }

    public static RatingState Replay(CombatJob job, IEnumerable<MatchOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ValidateJob(job);

        var wins = 0;
        var losses = 0;
        foreach (var outcome in outcomes)
        {
            ValidateOutcome(outcome);
            if (outcome == MatchOutcome.Win)
            {
                wins = checked(wins + 1);
            }
            else
            {
                losses = checked(losses + 1);
            }
        }

        return FromResults(job, wins, losses);
    }

    public static RatingState ReplayEpoch(
        CombatJob job,
        int epoch,
        IEnumerable<RatingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Replay(
            job,
            events
                .Where(x => x.Job == job && x.Epoch == epoch && IsRatedQueue(x.Queue))
                .Select(x => x.Outcome));
    }

    public static bool IsRatedQueue(MatchQueue queue) =>
        queue is MatchQueue.Casual or MatchQueue.Ranked;

    private static long DivideRoundedAwayFromZero(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        var magnitude = Math.Abs(numerator);
        var roundedMagnitude = (magnitude + (denominator / 2)) / denominator;
        return numerator < 0 ? -roundedMagnitude : roundedMagnitude;
    }

    private static void ValidateState(RatingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateJob(state.Job);
        ValidateResults(state.Wins, state.Losses);

        if ((long)state.Wins + state.Losses != state.Matches)
        {
            throw new ArgumentException(
                "Rating matches must equal wins plus losses.",
                nameof(state));
        }
    }

    private static void ValidateJob(CombatJob job)
    {
        if (!CombatJobs.All.Contains(job))
        {
            throw new ArgumentException("A rating cannot be calculated for an invalid job.", nameof(job));
        }
    }

    private static void ValidateResults(int wins, int losses)
    {
        if (wins < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wins), wins, "Wins cannot be negative.");
        }

        if (losses < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(losses), losses, "Losses cannot be negative.");
        }

        if ((long)wins + losses > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(wins), "The total match count exceeds the supported range.");
        }
    }

    private static void ValidateOutcome(MatchOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A valid match outcome is required.");
        }
    }

    private static void ValidateQueue(MatchQueue queue)
    {
        if (!Enum.IsDefined(queue))
        {
            throw new ArgumentOutOfRangeException(nameof(queue), queue, "A valid match queue is required.");
        }
    }
}
