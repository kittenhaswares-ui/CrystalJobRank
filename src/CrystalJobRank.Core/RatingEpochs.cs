namespace CrystalJobRank.Core;

public static class RatingEpochs
{
    public static int Current(IReadOnlyDictionary<CombatJob, int> epochs, CombatJob job) =>
        epochs.TryGetValue(job, out var epoch) ? epoch : 0;

    public static bool TryReset(
        IDictionary<CombatJob, int> epochs,
        IEnumerable<RatingEvent> events,
        CombatJob job)
    {
        if (!CombatJobs.All.Contains(job))
        {
            throw new ArgumentException("A valid combat job is required.", nameof(job));
        }

        var current = Current(AsReadOnly(epochs), job);
        if (!events.Any(x => x.Job == job && x.Epoch == current && RatingEngine.IsRatedQueue(x.Queue)))
        {
            return false;
        }

        epochs[job] = checked(current + 1);
        return true;
    }

    public static IReadOnlyList<CombatJob> ResetAll(
        IDictionary<CombatJob, int> epochs,
        IEnumerable<RatingEvent> events)
    {
        var snapshot = events.ToArray();
        var readOnlyEpochs = AsReadOnly(epochs);
        var jobs = snapshot
            .Where(x =>
                CombatJobs.All.Contains(x.Job) &&
                RatingEngine.IsRatedQueue(x.Queue) &&
                x.Epoch == Current(readOnlyEpochs, x.Job))
            .Select(x => x.Job)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        foreach (var job in jobs)
        {
            epochs[job] = checked(Current(readOnlyEpochs, job) + 1);
        }

        return jobs;
    }

    private static IReadOnlyDictionary<CombatJob, int> AsReadOnly(IDictionary<CombatJob, int> epochs) =>
        epochs as IReadOnlyDictionary<CombatJob, int>
        ?? new Dictionary<CombatJob, int>(epochs);
}
