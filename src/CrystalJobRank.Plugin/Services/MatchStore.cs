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
        AppliedOneTimeUpdateReset = data.Version < PluginDataMigrations.CurrentSchemaVersion;
        if (PluginDataMigrations.Apply(data)) SaveLocked();
    }

    public IReadOnlyList<MatchRecord> Snapshot()
    {
        lock (gate)
        {
            return data.Matches.OrderByDescending(x => x.CompletedAtUtc).ToArray();
        }
    }

    public MatchSubmission? FindSubmission(Guid matchId)
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
            var events = data.Matches
                .OrderBy(x => x.CompletedAtUtc)
                .ThenBy(x => x.Id)
                .Select(ToRatingEvent)
                .ToArray();
            return data.Matches
                .Where(x => CombatJobs.All.Contains(x.LocalJob))
                .Select(x => x.LocalJob)
                .Distinct()
                .Select(job => RatingEngine.ReplayEpoch(job, CurrentEpochLocked(job), events))
                .OrderByDescending(x => x.Rating)
                .ThenByDescending(x => x.Matches)
                .ToArray();
        }
    }

    public IReadOnlyDictionary<CombatJob, JobLifetimeStats> LifetimeSnapshot()
    {
        lock (gate)
        {
            return data.LifetimeByJob.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
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
            var previousEpochs = new Dictionary<CombatJob, int>(data.CurrentRatingEpochs);
            if (!RatingEpochs.TryReset(data.CurrentRatingEpochs, data.Matches.Select(ToRatingEvent), job)) return false;
            try
            {
                SaveLocked();
            }
            catch
            {
                data.CurrentRatingEpochs = previousEpochs;
                throw;
            }
            return true;
        }
    }

    public IReadOnlyList<CombatJob> ResetAllRatings()
    {
        lock (gate)
        {
            var previousEpochs = new Dictionary<CombatJob, int>(data.CurrentRatingEpochs);
            var jobs = RatingEpochs.ResetAll(data.CurrentRatingEpochs, data.Matches.Select(ToRatingEvent));
            if (jobs.Count == 0) return jobs;
            try
            {
                SaveLocked();
            }
            catch
            {
                data.CurrentRatingEpochs = previousEpochs;
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

            var isDuplicate = data.Matches.Any(existing =>
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

            record.RatingEpoch = CurrentEpochLocked(record.LocalJob);
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

    private int CurrentEpochLocked(CombatJob job) => RatingEpochs.Current(data.CurrentRatingEpochs, job);

    private static RatingEvent ToRatingEvent(MatchRecord match) => new(
        match.LocalJob,
        match.Outcome,
        match.Queue,
        match.RatingEpoch);

    private void SaveLocked()
    {
        var tempPath = filePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tempPath, filePath, true);
    }
}
