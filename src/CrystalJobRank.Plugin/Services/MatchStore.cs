using System.Text.Json;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin.Services;

internal sealed class MatchStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object gate = new();
    private readonly string filePath;
    private readonly IPluginLog log;
    private PluginData data;

    public bool AppliedOneTimeUpdateReset { get; }

    public MatchStore(string configDirectory, IPluginLog log)
    {
        this.log = log;
        filePath = Path.Combine(configDirectory, "matches.json");
        data = Load();
        // Schema 5 only changes the local epoch key shape. The advertised season
        // reset belongs exclusively to the v4 rules update and must not repeat.
        AppliedOneTimeUpdateReset = data.Version < 4;
        if (PluginDataMigrations.Apply(data)) SaveLocked();
    }

    public IReadOnlyList<MatchRecord> Snapshot()
    {
        lock (gate)
        {
            return data.Matches.OrderByDescending(x => x.CompletedAtUtc).ToArray();
        }
    }

    public LeaderboardMatchSubmission? FindSubmission(Guid matchId)
    {
        lock (gate)
        {
            return data.Matches.FirstOrDefault(match => match.Id == matchId)?.ToSubmission();
        }
    }

    public IReadOnlyList<RatingState> Ratings()
    {
        lock (gate)
        {
            var latestIdentity = data.Matches
                .OrderByDescending(match => match.CompletedAtUtc)
                .FirstOrDefault(HasAutomaticIdentity);
            if (latestIdentity is null) return [];

            var characterMatches = data.Matches
                .Where(match => SameIdentity(match, latestIdentity))
                .ToArray();
            var events = characterMatches
                .OrderBy(x => x.CompletedAtUtc)
                .ThenBy(x => x.Id)
                .Select(ToRatingEvent)
                .ToArray();
            return characterMatches
                .Where(x => CombatJobs.All.Contains(x.LocalJob))
                .Select(x => x.LocalJob)
                .Distinct()
                .Select(job => RatingEngine.ReplayEpoch(job, CurrentEpochLocked(latestIdentity, job), events))
                .OrderByDescending(x => x.Rating)
                .ThenByDescending(x => x.Matches)
                .ToArray();
        }
    }

    public IReadOnlyDictionary<CombatJob, JobLifetimeStats> LifetimeSnapshot()
    {
        lock (gate)
        {
            var result = data.LifetimeByJob.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
            var latestIdentity = data.Matches
                .OrderByDescending(match => match.CompletedAtUtc)
                .FirstOrDefault(HasAutomaticIdentity);
            if (latestIdentity is null) return result;

            // The displayed rating peak follows the same character+world scope as
            // Ratings(). Combat records remain historical, while rating never leaks
            // from another character played on this installation.
            foreach (var stats in result.Values) stats.HighestRating = RatingEngine.InitialRating;
            foreach (var match in data.Matches.Where(match =>
                         SameIdentity(match, latestIdentity) &&
                         RatingEngine.IsRatedQueue(match.Queue) &&
                         match.RatingEpoch == CurrentEpochLocked(latestIdentity, match.LocalJob)))
            {
                if (!result.TryGetValue(match.LocalJob, out var stats))
                {
                    stats = new JobLifetimeStats();
                    result[match.LocalJob] = stats;
                }

                if (match.RatingBefore is >= RatingEngine.MinimumRating and <= RatingEngine.MaximumRating)
                {
                    stats.HighestRating = Math.Max(stats.HighestRating, match.RatingBefore);
                }
                if (match.RatingAfter is >= RatingEngine.MinimumRating and <= RatingEngine.MaximumRating)
                {
                    stats.HighestRating = Math.Max(stats.HighestRating, match.RatingAfter);
                }
            }

            return result;
        }
    }

    public IReadOnlyDictionary<CombatRole, RoleStreakProgress> RoleStreakSnapshot()
    {
        lock (gate)
        {
            var result = data.RoleStreaks.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
            foreach (var role in new[] { CombatRole.Tank, CombatRole.Dps, CombatRole.Healer })
            {
                result.TryAdd(role, new RoleStreakProgress());
            }

            return result;
        }
    }

    public bool ResetRating(CombatJob job)
    {
        lock (gate)
        {
            if (!CombatJobs.All.Contains(job))
            {
                throw new ArgumentException("A valid combat job is required.", nameof(job));
            }

            var identity = LatestIdentityLocked();
            if (identity is null) return false;
            var current = CurrentEpochLocked(identity, job);
            if (!data.Matches.Any(match =>
                    SameIdentity(match, identity) &&
                    match.LocalJob == job &&
                    RatingEngine.IsRatedQueue(match.Queue) &&
                    match.RatingEpoch == current))
            {
                return false;
            }

            var previousEpochs = new Dictionary<string, int>(data.CurrentCharacterRatingEpochs);
            CharacterRatingEpochs.Advance(
                data.CurrentCharacterRatingEpochs,
                identity.CharacterName,
                identity.WorldId,
                job);
            try
            {
                SaveLocked();
            }
            catch
            {
                data.CurrentCharacterRatingEpochs = previousEpochs;
                throw;
            }
            return true;
        }
    }

    public IReadOnlyList<CombatJob> ResetAllRatings()
    {
        lock (gate)
        {
            var identity = LatestIdentityLocked();
            if (identity is null) return [];
            var jobs = CombatJobs.All
                .Where(job =>
                {
                    var current = CurrentEpochLocked(identity, job);
                    return data.Matches.Any(match =>
                        SameIdentity(match, identity) &&
                        match.LocalJob == job &&
                        RatingEngine.IsRatedQueue(match.Queue) &&
                        match.RatingEpoch == current);
                })
                .OrderBy(job => job)
                .ToArray();
            if (jobs.Length == 0) return jobs;

            var previousEpochs = new Dictionary<string, int>(data.CurrentCharacterRatingEpochs);
            foreach (var job in jobs)
            {
                CharacterRatingEpochs.Advance(
                    data.CurrentCharacterRatingEpochs,
                    identity.CharacterName,
                    identity.WorldId,
                    job);
            }
            try
            {
                SaveLocked();
            }
            catch
            {
                data.CurrentCharacterRatingEpochs = previousEpochs;
                throw;
            }
            return jobs;
        }
    }

    public MatchRecord? Add(MatchRecord record)
    {
        lock (gate)
        {
            if (!CombatJobs.All.Contains(record.LocalJob))
            {
                throw new ArgumentException("A valid local combat job is required.", nameof(record));
            }
            if (!Enum.IsDefined(record.Outcome) || !Enum.IsDefined(record.Queue))
            {
                throw new ArgumentException("A valid match outcome and queue are required.", nameof(record));
            }
            if (record.DurationSeconds is < 10 or > 1800)
            {
                throw new ArgumentException("Match duration is outside the accepted range.", nameof(record));
            }
            Validation.ValidateScoreboardStats(record.LocalStats);
            var identity = Validation.NormalizeCharacterIdentity(
                record.CharacterName,
                record.WorldId,
                record.WorldName);
            record.CharacterName = identity.CharacterName;
            record.WorldId = identity.WorldId;
            record.WorldName = identity.WorldName;

            var isDuplicate = data.Matches.Any(existing =>
                SameIdentity(existing, record) &&
                existing.LocalJob == record.LocalJob &&
                existing.Outcome == record.Outcome &&
                existing.Queue == record.Queue &&
                existing.TerritoryId == record.TerritoryId &&
                existing.DurationSeconds == record.DurationSeconds &&
                existing.AstraProgressTenths == record.AstraProgressTenths &&
                existing.UmbraProgressTenths == record.UmbraProgressTenths &&
                existing.LocalStats == record.LocalStats &&
                Math.Abs((existing.CompletedAtUtc - record.CompletedAtUtc).TotalSeconds) < 20);

            if (isDuplicate)
            {
                log.Warning("Ignoring a duplicate Crystalline Conflict result payload.");
                return null;
            }

            record.RatingEpoch = CurrentEpochLocked(record, record.LocalJob);
            var previousLifetime = data.LifetimeByJob;
            var previousStreaks = data.RoleStreaks;
            var previousRatings = data.Matches
                .Select(match => (Match: match, match.RatingBefore, match.RatingAfter, match.RatingDelta))
                .ToArray();
            data.Matches.Add(record);
            try
            {
                PluginDataMigrations.RecalculateMatchRatings(data.Matches);
                var rebuilt = LifetimeProgressCalculator.Rebuild(data.Matches);
                data.LifetimeByJob = LifetimeProgressCalculator.MergeLifetime(previousLifetime, rebuilt.LifetimeByJob);
                data.RoleStreaks = LifetimeProgressCalculator.MergeStreaks(previousStreaks, rebuilt.RoleStreaks);
                LifetimeProgressCalculator.ApplyCurrentSeasonValues(
                    data.LifetimeByJob,
                    data.RoleStreaks,
                    data.Matches,
                    data.CurrentCharacterRatingEpochs);
                SaveLocked();
            }
            catch
            {
                data.Matches.Remove(record);
                foreach (var previous in previousRatings)
                {
                    previous.Match.RatingBefore = previous.RatingBefore;
                    previous.Match.RatingAfter = previous.RatingAfter;
                    previous.Match.RatingDelta = previous.RatingDelta;
                }
                data.LifetimeByJob = previousLifetime;
                data.RoleStreaks = previousStreaks;
                throw;
            }
            return record;
        }
    }

    private PluginData Load()
    {
        try
        {
            if (!File.Exists(filePath)) return new PluginData();
            return JsonSerializer.Deserialize<PluginData>(File.ReadAllText(filePath), JsonOptions) ?? new PluginData();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Match history at '{filePath}' could not be read. Refusing to overwrite it.",
                exception);
        }
    }

    private int CurrentEpochLocked(MatchRecord identity, CombatJob job) => CharacterRatingEpochs.Current(
        data.CurrentCharacterRatingEpochs,
        identity.CharacterName,
        identity.WorldId,
        job);

    private MatchRecord? LatestIdentityLocked() => data.Matches
        .OrderByDescending(match => match.CompletedAtUtc)
        .FirstOrDefault(HasAutomaticIdentity);

    private static RatingEvent ToRatingEvent(MatchRecord match) => new(
        match.LocalJob,
        match.Outcome,
        match.Queue,
        match.RatingEpoch);

    private static bool HasAutomaticIdentity(MatchRecord match) =>
        !string.IsNullOrWhiteSpace(match.CharacterName) &&
        match.WorldId is > 0 and <= ushort.MaxValue &&
        !string.IsNullOrWhiteSpace(match.WorldName);

    private static bool SameIdentity(MatchRecord left, MatchRecord right) =>
        left.WorldId == right.WorldId &&
        string.Equals(left.CharacterName, right.CharacterName, StringComparison.OrdinalIgnoreCase);

    private void SaveLocked()
    {
        var tempPath = filePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tempPath, filePath, true);
    }
}
