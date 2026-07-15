using System.Net;

namespace CrystalJobRank.Plugin.Services;

internal enum LeaderboardEnqueueResult
{
    Added,
    Duplicate,
    Full,
}

internal sealed class LeaderboardOutboxItem
{
    public Guid MatchId { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptUtc { get; set; }
}

internal sealed class LeaderboardOutboxState
{
    public const int CurrentVersion = 1;
    public const int MaximumPending = 512;

    public int Version { get; set; } = CurrentVersion;
    public Guid? PlayerId { get; set; }
    public string ServerBaseUrl { get; set; } = string.Empty;
    public List<LeaderboardOutboxItem> Pending { get; set; } = [];

    public bool Bind(Guid? playerId, string serverBaseUrl)
    {
        var normalizedUrl = playerId.HasValue ? NormalizeServerUrl(serverBaseUrl) : string.Empty;
        if (PlayerId == playerId &&
            string.Equals(ServerBaseUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        PlayerId = playerId;
        ServerBaseUrl = normalizedUrl;
        Pending.Clear();
        return true;
    }

    public LeaderboardEnqueueResult Enqueue(Guid matchId, DateTime nowUtc)
    {
        if (Pending.Any(item => item.MatchId == matchId)) return LeaderboardEnqueueResult.Duplicate;
        if (Pending.Count >= MaximumPending) return LeaderboardEnqueueResult.Full;

        Pending.Add(new LeaderboardOutboxItem
        {
            MatchId = matchId,
            NextAttemptUtc = EnsureUtc(nowUtc),
        });
        return LeaderboardEnqueueResult.Added;
    }

    public bool RemoveHead(Guid matchId)
    {
        if (Pending.Count == 0 || Pending[0].MatchId != matchId) return false;
        Pending.RemoveAt(0);
        return true;
    }

    public bool Normalize(DateTime nowUtc)
    {
        if (Version > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Leaderboard outbox version {Version} is newer than the supported version {CurrentVersion}.");
        }

        var changed = Version != CurrentVersion;
        Version = CurrentVersion;
        if (Pending is null)
        {
            Pending = [];
            changed = true;
        }

        var now = EnsureUtc(nowUtc);
        var latestAllowedAttempt = now + LeaderboardRetryPolicy.MaximumDelay;
        var normalized = new List<LeaderboardOutboxItem>(Math.Min(Pending.Count, MaximumPending));
        var seen = new HashSet<Guid>();
        foreach (var item in Pending)
        {
            if (item is null || item.MatchId == Guid.Empty || !seen.Add(item.MatchId))
            {
                changed = true;
                continue;
            }

            if (normalized.Count >= MaximumPending)
            {
                changed = true;
                break;
            }

            var attempts = Math.Clamp(item.AttemptCount, 0, LeaderboardRetryPolicy.MaximumAttemptCount);
            var nextAttempt = item.NextAttemptUtc == default ? now : EnsureUtc(item.NextAttemptUtc);
            if (nextAttempt > latestAllowedAttempt) nextAttempt = latestAllowedAttempt;
            if (attempts != item.AttemptCount ||
                nextAttempt != item.NextAttemptUtc ||
                item.NextAttemptUtc.Kind != DateTimeKind.Utc)
            {
                changed = true;
            }

            normalized.Add(new LeaderboardOutboxItem
            {
                MatchId = item.MatchId,
                AttemptCount = attempts,
                NextAttemptUtc = nextAttempt,
            });
        }

        if (!PlayerId.HasValue && normalized.Count > 0)
        {
            normalized.Clear();
            changed = true;
        }

        var normalizedUrl = PlayerId.HasValue ? NormalizeServerUrl(ServerBaseUrl) : string.Empty;
        if (!string.Equals(ServerBaseUrl, normalizedUrl, StringComparison.Ordinal)) changed = true;
        ServerBaseUrl = normalizedUrl;
        if (changed) Pending = normalized;
        return changed;
    }

    public static string NormalizeServerUrl(string value) => (value ?? string.Empty).Trim().TrimEnd('/');

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

internal static class LeaderboardRetryPolicy
{
    public const int MaximumAttemptCount = 30;
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    public static TimeSpan DelayAfterFailure(int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 6);
        var seconds = Math.Min(MaximumDelay.TotalSeconds, 5d * Math.Pow(2d, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    public static bool IsRetryable(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue) return true;
        var numeric = (int)statusCode.Value;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || numeric >= 500;
    }
}
