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

    public MatchStore(string configDirectory, IPluginLog log)
    {
        this.log = log;
        filePath = Path.Combine(configDirectory, "matches.json");
        data = Load();
        if (PluginDataMigrations.Apply(data)) SaveLocked();
    }

    public IReadOnlyList<MatchRecord> Snapshot()
    {
        lock (gate)
        {
            return data.Matches.OrderByDescending(x => x.CompletedAtUtc).ToArray();
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
                .Where(x => CombatJobs.All.Contains(x.LocalJob) && RatingEngine.IsRatedQueue(x.Queue))
                .Select(x => x.LocalJob)
                .Distinct()
                .Select(job => RatingEngine.ReplayEpoch(job, CurrentEpochLocked(job), events))
                .OrderByDescending(x => x.Rating)
                .ThenByDescending(x => x.Matches)
                .ToArray();
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
            var isDuplicate = data.Matches.Any(existing =>
                existing.LocalJob == record.LocalJob &&
                existing.Outcome == record.Outcome &&
                existing.DurationSeconds == record.DurationSeconds &&
                existing.LocalStats == record.LocalStats &&
                Math.Abs((existing.CompletedAtUtc - record.CompletedAtUtc).TotalSeconds) < 20);

            if (isDuplicate)
            {
                log.Warning("Ignoring a duplicate Crystalline Conflict result payload.");
                return null;
            }

            record.RatingEpoch = CurrentEpochLocked(record.LocalJob);
            var state = RatingForCurrentEpochLocked(record.LocalJob);
            var change = !RatingEngine.IsRatedQueue(record.Queue)
                ? new RatingChange(state.Rating, state.Rating, 0, state.Matches, state.Wins, state.Losses)
                : RatingEngine.Apply(state, record.Outcome);
            record.RatingBefore = change.Before;
            record.RatingAfter = change.After;
            record.RatingDelta = change.Delta;

            data.Matches.Add(record);
            try
            {
                SaveLocked();
            }
            catch
            {
                data.Matches.Remove(record);
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

    private RatingState RatingForCurrentEpochLocked(CombatJob job) => RatingEngine.ReplayEpoch(
        job,
        CurrentEpochLocked(job),
        data.Matches
            .OrderBy(x => x.CompletedAtUtc)
            .ThenBy(x => x.Id)
            .Select(ToRatingEvent));

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
