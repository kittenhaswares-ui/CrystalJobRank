using System.Numerics;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using CrystalJobRank.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin.UI;

internal sealed class MainWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly MatchStore matchStore;
    private readonly LeaderboardClient leaderboardClient;
    private readonly LeaderboardOutbox leaderboardOutbox;
    private readonly ITextureProvider textureProvider;
    private readonly Action saveConfiguration;
    private readonly object stateGate = new();

    private Guid? selectedMatchId;
    private CombatJob selectedLeaderboardJob = CombatJob.PLD;
    private IReadOnlyList<LeaderboardRow> leaderboard = [];
    private string status = "Ready. New CC result screens will be recorded automatically.";
    private bool statusIsError;
    private bool networkBusy;
    private bool confirmAccountDeletion;

    public MainWindow(
        PluginConfiguration configuration,
        MatchStore matchStore,
        LeaderboardClient leaderboardClient,
        LeaderboardOutbox leaderboardOutbox,
        ITextureProvider textureProvider,
        Action saveConfiguration)
        : base("Crystal Job Rank###CrystalJobRankMain")
    {
        this.configuration = configuration;
        this.matchStore = matchStore;
        this.leaderboardClient = leaderboardClient;
        this.leaderboardOutbox = leaderboardOutbox;
        this.textureProvider = textureProvider;
        this.saveConfiguration = saveConfiguration;

        Size = new Vector2(920, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void NotifyMatch(MatchRecord match)
    {
        SetStatus(RatingEngine.IsRatedQueue(match.Queue)
            ? $"Recorded {match.Queue} {match.Outcome} on {match.LocalJob}: {match.RatingAfter} ({match.RatingDelta:+#;-#;0})."
            : $"Recorded {match.Queue} {match.Outcome} on {match.LocalJob}; rating unchanged.");
    }

    public void SetStatus(string value, bool isError = false)
    {
        lock (stateGate)
        {
            status = value;
            statusIsError = isError;
        }
    }

    public override void Draw()
    {
        string currentStatus;
        bool currentError;
        lock (stateGate)
        {
            currentStatus = status;
            currentError = statusIsError;
        }

        ImGui.TextColored(
            currentError ? new Vector4(1f, 0.42f, 0.42f, 1f) : new Vector4(0.55f, 0.85f, 1f, 1f),
            currentStatus);
        ImGui.Separator();

        AchievementVisuals.DrawHeader(matchStore.RoleStreakSnapshot());
        ImGui.Spacing();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##cjr-tabs")) return;

        if (ImGui.BeginTabItem("Job ratings"))
        {
            DrawRatings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Matches"))
        {
            DrawMatches();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Community leaderboard"))
        {
            DrawLeaderboard();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawRatings()
    {
        var ratings = matchStore.Ratings();
        var lifetime = matchStore.LifetimeSnapshot();
        var currentMatches = ratings.Sum(x => x.Matches);
        var currentWins = ratings.Sum(x => x.Wins);
        var currentLosses = ratings.Sum(x => x.Losses);

        ImGui.TextColored(new Vector4(0.82f, 0.75f, 1f, 1f), "JOB-SPECIFIC CRYSTAL RATING");
        ImGui.TextWrapped("Each job climbs independently. Casual and Ranked wins and losses move the rating; Custom and Unknown-queue matches and scoreboard performance never change it.");
        ImGui.TextDisabled($"Current rating epochs  •  Matches  {currentMatches}     Wins  {currentWins}     Losses  {currentLosses}");
        ImGui.Spacing();

        if (ratings.Count == 0)
        {
            ImGui.TextDisabled("No job records yet. Finish a Crystalline Conflict match to create your first job card.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX;
        if (!ImGui.BeginTable("##rating-cards", 2, flags)) return;
        for (var index = 0; index < ratings.Count; index++)
        {
            if (index % 2 == 0) ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(index % 2);
            lifetime.TryGetValue(ratings[index].Job, out var records);
            RankVisuals.DrawRatingCard(
                ratings[index],
                records ?? new JobLifetimeStats(),
                textureProvider,
                ImGui.GetContentRegionAvail().X);
            ImGui.Spacing();
        }

        ImGui.EndTable();
    }

    private void DrawMatches()
    {
        var matches = matchStore.Snapshot();
        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No matches recorded yet.");
            return;
        }

        ImGui.TextUnformatted("Recent matches");
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##match-list", 7, flags, new Vector2(0, 250)))
        {
            ImGui.TableSetupColumn("When");
            ImGui.TableSetupColumn("Result");
            ImGui.TableSetupColumn("Job");
            ImGui.TableSetupColumn("Queue");
            ImGui.TableSetupColumn("Arena");
            ImGui.TableSetupColumn("Rating");
            ImGui.TableSetupColumn("K / D / A");
            ImGui.TableHeadersRow();

            foreach (var match in matches.Take(200))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var selected = selectedMatchId == match.Id;
                if (ImGui.Selectable($"{match.CompletedAtUtc.ToLocalTime():g}##{match.Id}", selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    selectedMatchId = match.Id;
                }
                Cell(match.Outcome == MatchOutcome.Win ? "Win" : "Loss");
                Cell(match.LocalJob.ToString());
                Cell(match.Queue.ToString());
                Cell(match.Arena);
                Cell($"{match.RatingAfter} ({match.RatingDelta:+#;-#;0})");
                Cell($"{match.LocalStats.Kills} / {match.LocalStats.Deaths} / {match.LocalStats.Assists}");
            }

            ImGui.EndTable();
        }

        var detail = matches.FirstOrDefault(x => x.Id == selectedMatchId) ?? matches[0];
        selectedMatchId ??= detail.Id;
        ImGui.Spacing();
        ImGui.TextUnformatted($"{detail.Outcome} — {detail.LocalJob} — {detail.Arena} — {TimeSpan.FromSeconds(detail.DurationSeconds):m\\:ss}");
        ImGui.TextUnformatted($"Crystal progress: Astra {detail.AstraProgressTenths / 10f:N1}% / Umbra {detail.UmbraProgressTenths / 10f:N1}%");
        ImGui.TextUnformatted($"Your stats: {detail.LocalStats.DamageDealt:N0} damage dealt, {detail.LocalStats.DamageTaken:N0} taken, {detail.LocalStats.HpRestored:N0} HP restored, {detail.LocalStats.TimeOnCrystalSeconds}s on crystal");
        ImGui.Spacing();
        DrawScoreboard(detail);
    }

    private static void DrawScoreboard(MatchRecord match)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##scoreboard", 10, flags, new Vector2(0, 210))) return;
        foreach (var header in new[] { "Team", "Player", "Job", "K", "D", "A", "Damage", "Taken", "Restored", "Crystal" })
        {
            ImGui.TableSetupColumn(header);
        }
        ImGui.TableHeadersRow();

        foreach (var row in match.Scoreboard.OrderBy(x => x.Team).ThenBy(x => x.Name))
        {
            ImGui.TableNextRow();
            Cell(row.Team == 0 ? "Astra" : "Umbra");
            Cell($"{row.Name} {row.World}".Trim());
            Cell(row.Job.ToString());
            Cell(row.Stats.Kills.ToString());
            Cell(row.Stats.Deaths.ToString());
            Cell(row.Stats.Assists.ToString());
            Cell(row.Stats.DamageDealt.ToString("N0"));
            Cell(row.Stats.DamageTaken.ToString("N0"));
            Cell(row.Stats.HpRestored.ToString("N0"));
            Cell($"{row.Stats.TimeOnCrystalSeconds}s");
        }
        ImGui.EndTable();
    }

    private void DrawLeaderboard()
    {
        ImGui.TextColored(new Vector4(0.82f, 0.75f, 1f, 1f), "OPTIONAL COMMUNITY LEADERBOARD");
        ImGui.TextWrapped("This experimental ladder is community-reported and cannot be cheat-proof. Creating an identity is optional, uses only your chosen public alias, and never uploads your FFXIV identity or another player's data.");
        ImGui.TextWrapped("For each new Casual or Ranked match shared after opt-in, the plugin sends its time, job, result, queue, arena ID, duration, random fingerprint, and your own scoreboard values. The hosted service discards raw arena, duration, and scoreboard values after validation, but retains a one-way hash of the complete submission for duplicate protection.");
        ImGui.TextDisabled("The API key is stored in your local plugin configuration. Turning sharing off pauses queued uploads; re-enabling resumes them. Deleting the identity clears its queue and existing leaderboard entries.");
        ImGui.Spacing();

        var displayName = configuration.DisplayName;
        if (configuration.IsRegistered) ImGui.BeginDisabled();
        if (ImGui.InputText("Public display name", ref displayName, 24))
        {
            configuration.DisplayName = displayName;
            saveConfiguration();
        }
        if (configuration.IsRegistered) ImGui.EndDisabled();
        ImGui.TextDisabled(configuration.IsRegistered
            ? "The public alias is locked after registration. Delete the online identity to choose another."
            : "Use a pseudonym if you do not want the public entry linked to your character.");

        var serverUrl = configuration.ServerBaseUrl;
        if (configuration.IsRegistered) ImGui.BeginDisabled();
        if (ImGui.InputText("Server URL (advanced)", ref serverUrl, 256))
        {
            configuration.ServerBaseUrl = serverUrl;
            saveConfiguration();
        }
        if (configuration.IsRegistered) ImGui.EndDisabled();
        if (configuration.IsRegistered)
        {
            ImGui.TextDisabled("The server address is locked while this leaderboard identity exists, so its API key cannot be sent to another host.");
        }
        else
        {
            ImGui.TextDisabled("A custom server has a different operator and may follow a different privacy policy.");
        }

        if (!configuration.IsRegistered)
        {
            if (ImGui.Button(networkBusy ? "Registering..." : "Create leaderboard identity") && !networkBusy)
            {
                _ = RegisterAsync();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("This creates only the alias and key; sharing starts separately below.");
        }
        else
        {
            ImGui.TextUnformatted($"Registered as {configuration.DisplayName} ({configuration.PlayerId})");
            var share = configuration.ShareLeaderboard;
            if (ImGui.Checkbox("Share future Casual and Ranked results", ref share))
            {
                configuration.ShareLeaderboard = share;
                saveConfiguration();
                UpdateOutboxState();
            }
            ImGui.TextDisabled($"Pending uploads: {leaderboardOutbox.PendingCount}. Temporary network and server failures retry automatically.");

            if (!confirmAccountDeletion)
            {
                if (ImGui.Button("Delete leaderboard account")) confirmAccountDeletion = true;
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.42f, 0.42f, 1f), "This immediately removes the identity and all submitted matches from the active service. Cloudflare recovery backups can retain a copy for up to 7 days.");
                if (ImGui.Button(networkBusy ? "Deleting..." : "Confirm online deletion") && !networkBusy)
                {
                    _ = DeleteAccountAsync();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) confirmAccountDeletion = false;
            }
        }

        ImGui.Separator();
        if (ImGui.BeginCombo("Job", selectedLeaderboardJob.ToString()))
        {
            foreach (var job in Enum.GetValues<CombatJob>().Where(x => x != CombatJob.Unknown))
            {
                var selected = job == selectedLeaderboardJob;
                if (ImGui.Selectable(job.ToString(), selected)) selectedLeaderboardJob = job;
            }
            ImGui.EndCombo();
        }

        if (ImGui.Button(networkBusy ? "Loading..." : "Refresh leaderboard") && !networkBusy)
        {
            _ = RefreshLeaderboardAsync();
        }

        if (leaderboard.Count == 0)
        {
            ImGui.TextDisabled("No leaderboard rows loaded for this job.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##leaderboard", 7, flags)) return;
        foreach (var header in new[] { "#", "Name", "Rating", "Matches", "Wins", "Losses", "Win rate" })
        {
            ImGui.TableSetupColumn(header);
        }
        ImGui.TableHeadersRow();
        foreach (var row in leaderboard)
        {
            ImGui.TableNextRow();
            Cell(row.Rank.ToString());
            Cell(row.DisplayName);
            Cell(row.Rating.ToString("N0"));
            Cell(row.Matches.ToString("N0"));
            Cell(row.Wins.ToString("N0"));
            Cell(row.Losses.ToString("N0"));
            Cell($"{row.WinRate:P1}");
        }
        ImGui.EndTable();
    }

    private async Task RegisterAsync()
    {
        networkBusy = true;
        SetStatus("Registering leaderboard identity...");
        try
        {
            var registration = await leaderboardClient.RegisterAsync(configuration.ServerBaseUrl, configuration.DisplayName);
            configuration.PlayerId = registration.PlayerId;
            configuration.ApiKey = registration.ApiKey;
            configuration.ShareLeaderboard = false;
            saveConfiguration();
            UpdateOutboxState();
            SetStatus("Leaderboard identity created. Enable automatic sharing when you are ready; no matches are uploaded yet.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            networkBusy = false;
        }
    }

    private async Task RefreshLeaderboardAsync()
    {
        networkBusy = true;
        SetStatus($"Loading {selectedLeaderboardJob} leaderboard...");
        try
        {
            leaderboard = await leaderboardClient.GetLeaderboardAsync(configuration.ServerBaseUrl, selectedLeaderboardJob);
            SetStatus($"Loaded {leaderboard.Count} {selectedLeaderboardJob} leaderboard entries.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            networkBusy = false;
        }
    }

    private async Task DeleteAccountAsync()
    {
        networkBusy = true;
        SetStatus("Deleting leaderboard identity and submitted matches...");
        try
        {
            await leaderboardOutbox.PauseAsync();
            await leaderboardClient.DeleteAccountAsync(configuration.ServerBaseUrl, configuration.ApiKey);
            configuration.PlayerId = null;
            configuration.ApiKey = string.Empty;
            configuration.ShareLeaderboard = false;
            confirmAccountDeletion = false;
            saveConfiguration();
            UpdateOutboxState();
            SetStatus("Leaderboard account and active submissions were deleted. Local history was kept; recovery backups expire within 7 days.");
        }
        catch (Exception exception)
        {
            UpdateOutboxState();
            SetStatus(exception.Message, true);
        }
        finally
        {
            networkBusy = false;
        }
    }

    private void UpdateOutboxState() => leaderboardOutbox.UpdateState(
        configuration.ServerBaseUrl,
        configuration.PlayerId,
        configuration.ApiKey,
        configuration.ShareLeaderboard);

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }
}
