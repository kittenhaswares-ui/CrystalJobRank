using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Models;

internal static class PluginDataMigrations
{
    public const int CurrentSchemaVersion = 3;

    public static bool Apply(PluginData value)
    {
        if (value.Version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Match history schema {value.Version} is newer than the supported schema {CurrentSchemaVersion}. Refusing to overwrite it.");
        }

        var changed = NormalizeCollections(value);
        var requiresOneTimeSeasonReset = value.Version < 3;
        // Capture the rating numbers the player actually saw before any rule
        // migration rewrites per-match rating fields.
        var storedRatingPeaks = LifetimeProgressCalculator.SnapshotStoredRatingPeaks(value.Matches);

        // Version 1 predated rating epochs. Keep this conversion separate from
        // later migrations so a v2 -> v3 update never rewrites old match epochs.
        if (value.Version < 2)
        {
            foreach (var match in value.Matches) match.RatingEpoch = 0;
            value.CurrentRatingEpochs.Clear();
            value.Version = 2;
            changed = true;
        }

        if (requiresOneTimeSeasonReset || value.RatingRulesVersion != RatingEngine.RulesVersion)
        {
            RecalculateMatchRatings(value.Matches);
            value.RatingRulesVersion = RatingEngine.RulesVersion;
            changed = true;
        }

        var rebuilt = LifetimeProgressCalculator.Rebuild(value.Matches);
        var preservedLifetime = LifetimeProgressCalculator.MergeLifetime(value.LifetimeByJob, storedRatingPeaks);
        var mergedLifetime = LifetimeProgressCalculator.MergeLifetime(preservedLifetime, rebuilt.LifetimeByJob);
        var mergedStreaks = LifetimeProgressCalculator.MergeStreaks(value.RoleStreaks, rebuilt.RoleStreaks);

        if (!LifetimeProgressCalculator.LifetimeEquals(value.LifetimeByJob, mergedLifetime))
        {
            value.LifetimeByJob = mergedLifetime;
            changed = true;
        }

        if (!LifetimeProgressCalculator.StreaksEqual(value.RoleStreaks, mergedStreaks))
        {
            value.RoleStreaks = mergedStreaks;
            changed = true;
        }

        if (requiresOneTimeSeasonReset)
        {
            AdvanceEveryLocalRatingEpoch(value.CurrentRatingEpochs, value.Matches);
            value.Version = CurrentSchemaVersion;
            changed = true;
        }

        return changed;
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

    private static bool NormalizeCollections(PluginData value)
    {
        var changed = false;
        if (value.Matches is null)
        {
            value.Matches = [];
            changed = true;
        }

        if (value.CurrentRatingEpochs is null)
        {
            value.CurrentRatingEpochs = [];
            changed = true;
        }

        if (value.LifetimeByJob is null)
        {
            value.LifetimeByJob = [];
            changed = true;
        }

        if (value.RoleStreaks is null)
        {
            value.RoleStreaks = [];
            changed = true;
        }

        return changed;
    }

    private static void AdvanceEveryLocalRatingEpoch(
        IDictionary<CombatJob, int> epochs,
        IEnumerable<MatchRecord> matches)
    {
        var previous = new Dictionary<CombatJob, int>(epochs);
        foreach (var job in CombatJobs.All)
        {
            var historicalMaximum = matches
                .Where(match => match.LocalJob == job && RatingEngine.IsRatedQueue(match.Queue))
                .Select(match => match.RatingEpoch)
                .DefaultIfEmpty(-1)
                .Max();
            epochs[job] = checked(Math.Max(RatingEpochs.Current(previous, job), historicalMaximum) + 1);
        }
    }
}

internal sealed record LifetimeProgressBuild(
    Dictionary<CombatJob, JobLifetimeStats> LifetimeByJob,
    Dictionary<CombatRole, RoleStreakProgress> RoleStreaks);

internal static class LifetimeProgressCalculator
{
    public static Dictionary<CombatJob, JobLifetimeStats> SnapshotStoredRatingPeaks(
        IEnumerable<MatchRecord> matches)
    {
        var result = new Dictionary<CombatJob, JobLifetimeStats>();
        foreach (var match in matches)
        {
            if (!CombatJobs.All.Contains(match.LocalJob) || !RatingEngine.IsRatedQueue(match.Queue)) continue;
            if (!result.TryGetValue(match.LocalJob, out var stats))
            {
                stats = new JobLifetimeStats();
                result[match.LocalJob] = stats;
            }

            stats.HighestRating = HighestValidRating(
                stats.HighestRating,
                match.RatingBefore,
                match.RatingAfter);
        }

        return result;
    }

    public static LifetimeProgressBuild Rebuild(IEnumerable<MatchRecord> matches)
    {
        var lifetime = new Dictionary<CombatJob, JobLifetimeStats>();
        var streaks = new Dictionary<CombatRole, RoleStreakProgress>();

        foreach (var match in matches.OrderBy(x => x.CompletedAtUtc).ThenBy(x => x.Id))
        {
            if (!CombatJobs.All.Contains(match.LocalJob)) continue;

            if (!lifetime.TryGetValue(match.LocalJob, out var jobStats))
            {
                jobStats = new JobLifetimeStats();
                lifetime[match.LocalJob] = jobStats;
            }

            if (RatingEngine.IsRatedQueue(match.Queue))
            {
                jobStats.HighestRating = HighestValidRating(
                    jobStats.HighestRating,
                    match.RatingBefore,
                    match.RatingAfter);
            }

            var validStats = Validation.AreScoreboardStatsPlausible(match.LocalStats);
            if (validStats)
            {
                jobStats.HighestKills = Math.Max(jobStats.HighestKills, match.LocalStats.Kills);
                jobStats.HighestDamageDealt = Math.Max(jobStats.HighestDamageDealt, match.LocalStats.DamageDealt);
                jobStats.HighestDamageTaken = Math.Max(jobStats.HighestDamageTaken, match.LocalStats.DamageTaken);
                jobStats.HighestHealing = Math.Max(jobStats.HighestHealing, match.LocalStats.HpRestored);
            }

            if (!RatingEngine.IsRatedQueue(match.Queue)) continue;
            var role = CombatJobs.RoleOf(match.LocalJob);
            if (role == CombatRole.Unknown) continue;

            if (!streaks.TryGetValue(role, out var roleProgress))
            {
                roleProgress = new RoleStreakProgress();
                streaks[role] = roleProgress;
            }

            roleProgress.CurrentWinStreak = match.Outcome == MatchOutcome.Win
                ? checked(roleProgress.CurrentWinStreak + 1)
                : 0;
            roleProgress.BestWinStreak = Math.Max(roleProgress.BestWinStreak, roleProgress.CurrentWinStreak);

            roleProgress.CurrentDeathlessStreak = validStats && match.LocalStats.Deaths == 0
                ? checked(roleProgress.CurrentDeathlessStreak + 1)
                : 0;
            roleProgress.BestDeathlessStreak = Math.Max(
                roleProgress.BestDeathlessStreak,
                roleProgress.CurrentDeathlessStreak);
        }

        return new LifetimeProgressBuild(lifetime, streaks);
    }

    public static Dictionary<CombatJob, JobLifetimeStats> MergeLifetime(
        IReadOnlyDictionary<CombatJob, JobLifetimeStats> existing,
        IReadOnlyDictionary<CombatJob, JobLifetimeStats> rebuilt)
    {
        var result = new Dictionary<CombatJob, JobLifetimeStats>();
        foreach (var job in CombatJobs.All)
        {
            existing.TryGetValue(job, out var oldStats);
            rebuilt.TryGetValue(job, out var newStats);
            if (oldStats is null && newStats is null) continue;

            result[job] = new JobLifetimeStats
            {
                HighestRating = Math.Clamp(
                    Math.Max(oldStats?.HighestRating ?? RatingEngine.InitialRating,
                        newStats?.HighestRating ?? RatingEngine.InitialRating),
                    RatingEngine.InitialRating,
                    RatingEngine.MaximumRating),
                HighestKills = Math.Clamp(
                    Math.Max(oldStats?.HighestKills ?? 0, newStats?.HighestKills ?? 0), 0, 100),
                HighestDamageDealt = Math.Clamp(
                    Math.Max(oldStats?.HighestDamageDealt ?? 0, newStats?.HighestDamageDealt ?? 0), 0, 20_000_000),
                HighestDamageTaken = Math.Clamp(
                    Math.Max(oldStats?.HighestDamageTaken ?? 0, newStats?.HighestDamageTaken ?? 0), 0, 20_000_000),
                HighestHealing = Math.Clamp(
                    Math.Max(oldStats?.HighestHealing ?? 0, newStats?.HighestHealing ?? 0), 0, 20_000_000),
            };
        }

        return result;
    }

    public static Dictionary<CombatRole, RoleStreakProgress> MergeStreaks(
        IReadOnlyDictionary<CombatRole, RoleStreakProgress> existing,
        IReadOnlyDictionary<CombatRole, RoleStreakProgress> rebuilt)
    {
        var result = new Dictionary<CombatRole, RoleStreakProgress>();
        foreach (var role in new[] { CombatRole.Tank, CombatRole.Dps, CombatRole.Healer })
        {
            existing.TryGetValue(role, out var oldProgress);
            rebuilt.TryGetValue(role, out var newProgress);
            if (oldProgress is null && newProgress is null) continue;

            var currentWins = Math.Max(0, newProgress?.CurrentWinStreak ?? oldProgress?.CurrentWinStreak ?? 0);
            var currentDeathless = Math.Max(
                0,
                newProgress?.CurrentDeathlessStreak ?? oldProgress?.CurrentDeathlessStreak ?? 0);
            result[role] = new RoleStreakProgress
            {
                CurrentWinStreak = currentWins,
                BestWinStreak = Math.Max(
                    currentWins,
                    Math.Max(oldProgress?.BestWinStreak ?? 0, newProgress?.BestWinStreak ?? 0)),
                CurrentDeathlessStreak = currentDeathless,
                BestDeathlessStreak = Math.Max(
                    currentDeathless,
                    Math.Max(oldProgress?.BestDeathlessStreak ?? 0, newProgress?.BestDeathlessStreak ?? 0)),
            };
        }

        return result;
    }

    public static bool LifetimeEquals(
        IReadOnlyDictionary<CombatJob, JobLifetimeStats> left,
        IReadOnlyDictionary<CombatJob, JobLifetimeStats> right) =>
        left.Count == right.Count && right.All(pair =>
            left.TryGetValue(pair.Key, out var value) &&
            value is not null &&
            value.HighestRating == pair.Value.HighestRating &&
            value.HighestKills == pair.Value.HighestKills &&
            value.HighestDamageDealt == pair.Value.HighestDamageDealt &&
            value.HighestDamageTaken == pair.Value.HighestDamageTaken &&
            value.HighestHealing == pair.Value.HighestHealing);

    public static bool StreaksEqual(
        IReadOnlyDictionary<CombatRole, RoleStreakProgress> left,
        IReadOnlyDictionary<CombatRole, RoleStreakProgress> right) =>
        left.Count == right.Count && right.All(pair =>
            left.TryGetValue(pair.Key, out var value) &&
            value is not null &&
            value.CurrentWinStreak == pair.Value.CurrentWinStreak &&
            value.BestWinStreak == pair.Value.BestWinStreak &&
            value.CurrentDeathlessStreak == pair.Value.CurrentDeathlessStreak &&
            value.BestDeathlessStreak == pair.Value.BestDeathlessStreak);

    private static int HighestValidRating(int current, params int[] candidates)
    {
        var result = Math.Max(RatingEngine.InitialRating, current);
        foreach (var candidate in candidates)
        {
            if (candidate is >= RatingEngine.MinimumRating and <= RatingEngine.MaximumRating)
            {
                result = Math.Max(result, candidate);
            }
        }

        return result;
    }
}
