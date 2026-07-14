using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Models;

internal static class PluginDataMigrations
{
    public const int CurrentSchemaVersion = 2;

    public static bool Apply(PluginData value)
    {
        if (value.Version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Match history schema {value.Version} is newer than the supported schema {CurrentSchemaVersion}. Refusing to overwrite it.");
        }

        value.Matches ??= [];
        value.CurrentRatingEpochs ??= [];

        var requiresMigration =
            value.Version < CurrentSchemaVersion ||
            value.RatingRulesVersion != RatingEngine.RulesVersion;
        if (!requiresMigration) return false;

        if (value.Version < CurrentSchemaVersion)
        {
            foreach (var match in value.Matches) match.RatingEpoch = 0;
            value.CurrentRatingEpochs.Clear();
        }

        RecalculateMatchRatings(value.Matches);
        value.Version = CurrentSchemaVersion;
        value.RatingRulesVersion = RatingEngine.RulesVersion;
        return true;
    }

    public static void RecalculateMatchRatings(IEnumerable<MatchRecord> matches)
    {
        var states = new Dictionary<(CombatJob Job, int Epoch), RatingState>();
        foreach (var match in matches.OrderBy(x => x.CompletedAtUtc).ThenBy(x => x.Id))
        {
            if (!CombatJobs.All.Contains(match.LocalJob))
            {
                match.RatingBefore = RatingEngine.InitialRating;
                match.RatingAfter = RatingEngine.InitialRating;
                match.RatingDelta = 0;
                continue;
            }

            var key = (match.LocalJob, match.RatingEpoch);
            var state = states.TryGetValue(key, out var existing)
                ? existing
                : RatingEngine.Empty(match.LocalJob);
            if (!RatingEngine.IsRatedQueue(match.Queue))
            {
                match.RatingBefore = state.Rating;
                match.RatingAfter = state.Rating;
                match.RatingDelta = 0;
                continue;
            }

            var change = RatingEngine.Apply(state, match.Outcome);
            match.RatingBefore = change.Before;
            match.RatingAfter = change.After;
            match.RatingDelta = change.Delta;
            states[key] = new RatingState(
                match.LocalJob,
                change.After,
                change.MatchesAfter,
                change.WinsAfter,
                change.LossesAfter);
        }
    }
}
