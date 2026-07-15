using CrystalJobRank.Core;

namespace CrystalJobRank.Plugin.Models;

internal static class PluginDataMigrations
{
    public const int CurrentSchemaVersion = 5;

    public static bool Apply(PluginData value)
    {
        if (value.Version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Match history schema {value.Version} is newer than the supported schema {CurrentSchemaVersion}. Refusing to overwrite it.");
        }

        var changed = NormalizeCollections(value);
        var requiresOneTimeSeasonReset = value.Version < 4;
        var requiresCharacterEpochMigration = value.Version < 5;
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

        if (requiresCharacterEpochMigration)
        {
            MigrateCharacterRatingEpochs(
                value.CurrentCharacterRatingEpochs,
                value.CurrentRatingEpochs,
                value.Matches,
                requiresOneTimeSeasonReset);
            value.CurrentRatingEpochs.Clear();
            value.Version = CurrentSchemaVersion;
            changed = true;
        }

        var rebuilt = LifetimeProgressCalculator.Rebuild(value.Matches);
        var preservedLifetime = LifetimeProgressCalculator.MergeLifetime(value.LifetimeByJob, storedRatingPeaks);
        var mergedLifetime = LifetimeProgressCalculator.MergeLifetime(preservedLifetime, rebuilt.LifetimeByJob);
        var mergedStreaks = LifetimeProgressCalculator.MergeStreaks(value.RoleStreaks, rebuilt.RoleStreaks);

        // Normalize the rebuilt candidates before comparing them with persisted data.
        // This keeps migration idempotent: old history can contribute records and best
        // badges, but cannot resurrect a previous-season peak or current streak.
        LifetimeProgressCalculator.ApplyCurrentSeasonValues(
            mergedLifetime,
            mergedStreaks,
            value.Matches,
            value.CurrentCharacterRatingEpochs);

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

        return changed;
    }

    public static void RecalculateMatchRatings(IEnumerable<MatchRecord> matches)
    {
        var states = new Dictionary<(string Character, uint WorldId, CombatJob Job, int Epoch), RatingState>();
        foreach (var match in matches.OrderBy(x => x.CompletedAtUtc).ThenBy(x => x.Id))
        {
            if (!CombatJobs.All.Contains(match.LocalJob))
            {
                match.RatingBefore = RatingEngine.InitialRating;
                match.RatingAfter = RatingEngine.InitialRating;
                match.RatingDelta = 0;
                continue;
            }

            var key = (
                NormalizeIdentityPart(match.CharacterName),
                match.WorldId,
                match.LocalJob,
                match.RatingEpoch);
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

    private static string NormalizeIdentityPart(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<legacy>" : value.Trim().ToUpperInvariant();

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

        if (value.CurrentCharacterRatingEpochs is null)
        {
            value.CurrentCharacterRatingEpochs = [];
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

    private static void MigrateCharacterRatingEpochs(
        IDictionary<string, int> characterEpochs,
        IReadOnlyDictionary<CombatJob, int> legacyEpochs,
        IEnumerable<MatchRecord> matches,
        bool resetForNewSeason)
    {
        var grouped = matches
            .Where(match => RatingEngine.IsRatedQueue(match.Queue))
            .Select(match => new
            {
                Match = match,
                HasKey = CharacterRatingEpochs.TryKey(
                    match.CharacterName,
                    match.WorldId,
                    match.LocalJob,
                    out var key),
                Key = key,
            })
            .Where(item => item.HasKey)
            .GroupBy(item => (item.Key, item.Match.LocalJob));

        foreach (var group in grouped)
        {
            characterEpochs.TryGetValue(group.Key.Key, out var existing);
            legacyEpochs.TryGetValue(group.Key.LocalJob, out var legacy);
            var current = Math.Max(0, Math.Max(existing, legacy));
            if (resetForNewSeason)
            {
                var historicalMaximum = group.Max(item => item.Match.RatingEpoch);
                current = checked(Math.Max(current, historicalMaximum) + 1);
            }

            if (current > 0 || characterEpochs.ContainsKey(group.Key.Key))
            {
                characterEpochs[group.Key.Key] = current;
            }
        }
    }
}

internal sealed record LifetimeProgressBuild(
    Dictionary<CombatJob, JobLifetimeStats> LifetimeByJob,
    Dictionary<CombatRole, RoleStreakProgress> RoleStreaks);

internal static class LifetimeProgressCalculator
{
    public static bool ApplyCurrentSeasonValues(
        IDictionary<CombatJob, JobLifetimeStats> lifetime,
        IDictionary<CombatRole, RoleStreakProgress> streaks,
        IEnumerable<MatchRecord> matches,
        IReadOnlyDictionary<string, int> currentEpochs)
    {
        var changed = false;
        var currentMatches = matches
            .Where(match =>
                CombatJobs.All.Contains(match.LocalJob) &&
                RatingEngine.IsRatedQueue(match.Queue) &&
                IsCurrentCharacterEpoch(match, currentEpochs))
            .OrderBy(match => match.CompletedAtUtc)
            .ThenBy(match => match.Id)
            .ToArray();

        foreach (var job in CombatJobs.All)
        {
            if (!lifetime.TryGetValue(job, out var stats)) continue;
            var currentPeak = RatingEngine.InitialRating;
            foreach (var match in currentMatches.Where(match => match.LocalJob == job))
            {
                currentPeak = HighestValidRating(currentPeak, match.RatingBefore, match.RatingAfter);
            }

            if (stats.HighestRating == currentPeak) continue;
            stats.HighestRating = currentPeak;
            changed = true;
        }

        var active = new Dictionary<CombatRole, (int Wins, int Deathless)>();
        foreach (var match in currentMatches)
        {
            var role = CombatJobs.RoleOf(match.LocalJob);
            if (role == CombatRole.Unknown) continue;
            active.TryGetValue(role, out var progress);
            progress.Wins = match.Outcome == MatchOutcome.Win ? checked(progress.Wins + 1) : 0;
            progress.Deathless = Validation.AreScoreboardStatsPlausible(match.LocalStats) && match.LocalStats.Deaths == 0
                ? checked(progress.Deathless + 1)
                : 0;
            active[role] = progress;
        }

        foreach (var role in new[] { CombatRole.Tank, CombatRole.Dps, CombatRole.Healer })
        {
            active.TryGetValue(role, out var current);
            if (!streaks.TryGetValue(role, out var progress))
            {
                if (current.Wins == 0 && current.Deathless == 0) continue;
                progress = new RoleStreakProgress();
                streaks[role] = progress;
                changed = true;
            }

            var bestWins = Math.Max(progress.BestWinStreak, current.Wins);
            var bestDeathless = Math.Max(progress.BestDeathlessStreak, current.Deathless);
            if (progress.CurrentWinStreak == current.Wins &&
                progress.CurrentDeathlessStreak == current.Deathless &&
                progress.BestWinStreak == bestWins &&
                progress.BestDeathlessStreak == bestDeathless)
            {
                continue;
            }

            progress.CurrentWinStreak = current.Wins;
            progress.CurrentDeathlessStreak = current.Deathless;
            progress.BestWinStreak = bestWins;
            progress.BestDeathlessStreak = bestDeathless;
            changed = true;
        }

        return changed;
    }

    private static bool IsCurrentCharacterEpoch(
        MatchRecord match,
        IReadOnlyDictionary<string, int> currentEpochs)
    {
        if (!CharacterRatingEpochs.TryKey(match.CharacterName, match.WorldId, match.LocalJob, out var key))
        {
            return false;
        }

        var current = currentEpochs.TryGetValue(key, out var epoch) ? epoch : 0;
        return match.RatingEpoch == current;
    }

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
