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
            return data.Matches
                .Where(x => x.LocalJob != CombatJob.Unknown && x.Queue != MatchQueue.Custom)
                .GroupBy(x => x.LocalJob)
                .Select(group => RatingEngine.Replay(
                    group.Key,
                    group.OrderBy(x => x.CompletedAtUtc).Select(x => x.Outcome)))
                .OrderByDescending(x => x.Rating)
                .ThenByDescending(x => x.Matches)
                .ToArray();
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

            var state = RatingEngine.Replay(
                record.LocalJob,
                data.Matches
                    .Where(x => x.LocalJob == record.LocalJob && x.Queue != MatchQueue.Custom)
                    .OrderBy(x => x.CompletedAtUtc)
                    .Select(x => x.Outcome));
            var change = record.Queue == MatchQueue.Custom
                ? new RatingChange(state.Rating, state.Rating, 0, state.Matches, state.Wins, state.Losses)
                : RatingEngine.Apply(state, record.Outcome);
            record.RatingBefore = change.Before;
            record.RatingAfter = change.After;
            record.RatingDelta = change.Delta;

            data.Matches.Add(record);
            SaveLocked();
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
            log.Error(exception, "Unable to read local match history. Starting with an empty in-memory store.");
            return new PluginData();
        }
    }

    private void SaveLocked()
    {
        var tempPath = filePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tempPath, filePath, true);
    }
}
