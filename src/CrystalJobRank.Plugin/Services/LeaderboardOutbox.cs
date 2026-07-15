using System.Text.Json;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin.Services;

internal sealed class LeaderboardOutbox : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object gate = new();
    private readonly object attemptLifecycleGate = new();
    private readonly string filePath;
    private readonly MatchStore matchStore;
    private readonly LeaderboardClient leaderboardClient;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Task worker;

    private LeaderboardOutboxState data;
    private UploadState uploadState = UploadState.Empty;
    private CancellationTokenSource? activeAttempt;
    private TaskCompletionSource<bool>? pauseOperationsDrained;
    private int activePauseOperations;
    private bool paused = true;
    private bool disposed;

    public event Action<string, bool>? StatusChanged;

    public int PendingCount
    {
        get
        {
            lock (gate) return data.Pending.Count;
        }
    }

    public LeaderboardOutbox(
        string configDirectory,
        MatchStore matchStore,
        LeaderboardClient leaderboardClient,
        IPluginLog log)
    {
        filePath = Path.Combine(configDirectory, "leaderboard-outbox.json");
        this.matchStore = matchStore;
        this.leaderboardClient = leaderboardClient;
        this.log = log;
        data = Load();
        worker = Task.Run(RunAsync);
    }

    public void UpdateState(string serverBaseUrl, string installationKey)
    {
        CancellationTokenSource? attemptToCancel;
        bool shouldWake;
        lock (gate)
        {
            if (disposed) return;

            var next = new UploadState(
                LeaderboardOutboxState.NormalizeServerUrl(serverBaseUrl),
                installationKey ?? string.Empty);
            var identityOrCredentialChanged = !uploadState.SameIdentityAndCredential(next);
            uploadState = next;
            paused = false;

            if (data.Bind(next.ServerBaseUrl)) TrySaveLocked();

            attemptToCancel = identityOrCredentialChanged || !next.CanUpload ? activeAttempt : null;
            shouldWake = next.CanUpload && data.Pending.Count > 0;
        }

        SafeCancel(attemptToCancel);
        if (shouldWake) Signal();
    }

    public void Enqueue(Guid matchId)
    {
        LeaderboardMatchSubmission? submission;
        try
        {
            submission = matchStore.FindSubmission(matchId);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Match {MatchId} has no valid automatic leaderboard identity.", matchId);
            NotifyStatus("Match saved locally, but its character identity could not be queued.", true);
            return;
        }

        if (submission is null || !RatingEngine.IsRatedQueue(submission.Queue)) return;

        LeaderboardEnqueueResult result;
        lock (gate)
        {
            if (disposed) return;
            result = data.Enqueue(matchId, DateTime.UtcNow);
            if (result == LeaderboardEnqueueResult.Added) TrySaveLocked();
        }

        if (result == LeaderboardEnqueueResult.Added)
        {
            NotifyStatus("Match saved locally; leaderboard upload queued.");
            Signal();
        }
        else if (result == LeaderboardEnqueueResult.Full)
        {
            log.Error(
                "The leaderboard upload queue is full ({Capacity}); match {MatchId} was not queued.",
                LeaderboardOutboxState.MaximumPending,
                matchId);
            NotifyStatus("Saved locally, but the leaderboard upload queue is full.", true);
        }
    }

    public async Task PauseAsync()
    {
        CancellationTokenSource? attemptToCancel;
        lock (gate)
        {
            if (disposed) return;
            paused = true;
            attemptToCancel = activeAttempt;
            BeginPauseOperationLocked();
        }

        var sendGateHeld = false;
        try
        {
            SafeCancel(attemptToCancel);
            Signal();

            await sendGate.WaitAsync().ConfigureAwait(false);
            sendGateHeld = true;
        }
        finally
        {
            if (sendGateHeld) sendGate.Release();
            CompletePauseOperation();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? attemptToCancel;
        Task pauseOperationsCompleted;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            paused = true;
            attemptToCancel = activeAttempt;
            pauseOperationsCompleted = pauseOperationsDrained?.Task ?? Task.CompletedTask;
        }

        SafeCancel(attemptToCancel);
        SafeCancel(lifetimeCancellation);
        Signal();

        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected while unloading the plugin.
        }

        // PauseAsync may already be waiting on sendGate when disposal starts. The worker is
        // stopped first so it can release the gate, then every admitted pause operation is
        // allowed to leave before the semaphores themselves are disposed.
        pauseOperationsCompleted.GetAwaiter().GetResult();

        wakeSignal.Dispose();
        sendGate.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task RunAsync()
    {
        var cancellationToken = lifetimeCancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var wait = DelayUntilHeadIsReady();
                if (!wait.HasValue)
                {
                    await wakeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (wait.Value > TimeSpan.Zero)
                {
                    await wakeSignal.WaitAsync(wait.Value, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await SendHeadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.Error(exception, "The leaderboard upload worker encountered an unexpected error.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan? DelayUntilHeadIsReady()
    {
        lock (gate)
        {
            if (disposed || paused || !uploadState.CanUpload || data.Pending.Count == 0) return null;
            var delay = data.Pending[0].NextAttemptUtc - DateTime.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private async Task SendHeadAsync(CancellationToken lifetimeToken)
    {
        await sendGate.WaitAsync(lifetimeToken).ConfigureAwait(false);
        CancellationTokenSource? attempt = null;
        PendingAttempt pending = default;
        UploadState currentUpload = UploadState.Empty;
        try
        {
            lock (gate)
            {
                if (disposed || paused || !uploadState.CanUpload || data.Pending.Count == 0) return;
                var head = data.Pending[0];
                if (head.NextAttemptUtc > DateTime.UtcNow) return;

                pending = new PendingAttempt(head.MatchId);
                currentUpload = uploadState;
                attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                activeAttempt = attempt;
            }

            var submission = matchStore.FindSubmission(pending.MatchId);
            if (submission is null || !RatingEngine.IsRatedQueue(submission.Queue))
            {
                RemoveHead(pending.MatchId);
                log.Warning(
                    "Removed leaderboard queue entry {MatchId} because its local rated match no longer exists.",
                    pending.MatchId);
                return;
            }

            try
            {
                await leaderboardClient.SubmitAsync(
                    currentUpload.ServerBaseUrl,
                    currentUpload.InstallationKey,
                    submission,
                    attempt.Token).ConfigureAwait(false);
                RemoveHead(pending.MatchId);
                NotifyStatus("Match saved locally and shared with the community leaderboard.");
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                // The endpoint/installation key changed, a pause drained in-flight work,
                // or the plugin is unloading. The persisted head remains queued.
            }
            catch (Exception exception)
            {
                HandleFailure(pending, currentUpload, exception);
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeAttempt, attempt)) activeAttempt = null;
            }

            SafeDispose(attempt);
            sendGate.Release();
        }
    }

    private void BeginPauseOperationLocked()
    {
        if (activePauseOperations++ == 0)
        {
            pauseOperationsDrained = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void CompletePauseOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (gate)
        {
            activePauseOperations--;
            if (activePauseOperations == 0)
            {
                drained = pauseOperationsDrained;
                pauseOperationsDrained = null;
            }
        }

        drained?.TrySetResult(true);
    }

    private void SafeCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;

        // Cancellation is deliberately performed outside the main state lock because token
        // callbacks run synchronously. A separate lifecycle lock serializes it with disposal,
        // closing the race where SendHeadAsync completed between capturing activeAttempt and
        // calling Cancel().
        lock (attemptLifecycleGate)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The attempt completed just before cancellation acquired the lifecycle lock.
            }
        }
    }

    private void SafeDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;

        lock (attemptLifecycleGate)
        {
            cancellation.Dispose();
        }
    }

    private void HandleFailure(PendingAttempt pending, UploadState attemptedUpload, Exception exception)
    {
        var statusCode = (exception as HttpRequestException)?.StatusCode;
        var retryable = LeaderboardRetryPolicy.IsRetryable(statusCode);
        var changed = false;
        var delay = TimeSpan.Zero;

        lock (gate)
        {
            if (!HeadStillBelongsTo(pending.MatchId, attemptedUpload)) return;
            if (!retryable)
            {
                changed = data.RemoveHead(pending.MatchId);
            }
            else
            {
                var head = data.Pending[0];
                head.AttemptCount = Math.Min(
                    LeaderboardRetryPolicy.MaximumAttemptCount,
                    head.AttemptCount + 1);
                delay = LeaderboardRetryPolicy.DelayAfterFailure(head.AttemptCount);
                head.NextAttemptUtc = DateTime.UtcNow + delay;
                changed = true;
            }

            if (changed) TrySaveLocked();
        }

        if (!changed) return;
        if (retryable)
        {
            log.Warning(
                exception,
                "Leaderboard upload for match {MatchId} failed and will retry in {Delay}.",
                pending.MatchId,
                delay);
            NotifyStatus("Saved locally; leaderboard upload queued for automatic retry.", true);
        }
        else
        {
            log.Warning(
                exception,
                "Leaderboard permanently rejected match {MatchId} with HTTP status {StatusCode}; removed it from the queue.",
                pending.MatchId,
                statusCode.HasValue ? (int)statusCode.Value : 0);
            NotifyStatus("A queued leaderboard upload was rejected and will not be retried.", true);
        }

        Signal();
    }

    private void RemoveHead(Guid matchId)
    {
        lock (gate)
        {
            if (data.RemoveHead(matchId)) TrySaveLocked();
        }

        Signal();
    }

    private bool HeadStillBelongsTo(Guid matchId, UploadState attemptedUpload) =>
        data.Pending.Count > 0 &&
        data.Pending[0].MatchId == matchId &&
        string.Equals(data.ServerBaseUrl, attemptedUpload.ServerBaseUrl, StringComparison.OrdinalIgnoreCase);

    private LeaderboardOutboxState Load()
    {
        LeaderboardOutboxState result;
        try
        {
            if (!File.Exists(filePath)) return new LeaderboardOutboxState();
            result = JsonSerializer.Deserialize<LeaderboardOutboxState>(File.ReadAllText(filePath), JsonOptions)
                ?? new LeaderboardOutboxState();
        }
        catch (Exception exception)
        {
            log.Error(exception, "The persisted leaderboard upload queue could not be read; starting with an empty queue.");
            return new LeaderboardOutboxState();
        }

        try
        {
            if (!result.Normalize(DateTime.UtcNow)) return result;
        }
        catch (Exception exception)
        {
            log.Error(exception, "The persisted leaderboard upload queue is invalid; starting with an empty queue.");
            return new LeaderboardOutboxState();
        }

        try
        {
            Save(result);
        }
        catch (Exception exception)
        {
            log.Error(exception, "The normalized leaderboard upload queue could not be persisted.");
        }

        return result;
    }

    private void TrySaveLocked()
    {
        try
        {
            Save(data);
        }
        catch (Exception exception)
        {
            log.Error(exception, "The leaderboard upload queue could not be persisted.");
            NotifyStatus("Leaderboard retry state could not be saved to disk.", true);
        }
    }

    private void Save(LeaderboardOutboxState value)
    {
        var tempPath = filePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tempPath, filePath, true);
    }

    private void Signal()
    {
        try
        {
            if (wakeSignal.CurrentCount == 0) wakeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // A late signal during plugin unload is harmless.
        }
        catch (SemaphoreFullException)
        {
            // Another caller already woke the single worker.
        }
    }

    private void NotifyStatus(string message, bool isError = false)
    {
        try
        {
            StatusChanged?.Invoke(message, isError);
        }
        catch (Exception exception)
        {
            log.Warning(exception, "A leaderboard status subscriber failed.");
        }
    }

    private readonly record struct PendingAttempt(Guid MatchId);

    private readonly record struct UploadState(
        string ServerBaseUrl,
        string InstallationKey)
    {
        public static UploadState Empty { get; } = new(string.Empty, string.Empty);
        public bool CanUpload =>
            !string.IsNullOrWhiteSpace(ServerBaseUrl) &&
            InstallationKey.Length == 43;

        public bool SameIdentityAndCredential(UploadState other) =>
            string.Equals(ServerBaseUrl, other.ServerBaseUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(InstallationKey, other.InstallationKey, StringComparison.Ordinal);
    }
}
