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
    private static readonly CombatJob[] LeaderboardTanks =
        [CombatJob.PLD, CombatJob.WAR, CombatJob.DRK, CombatJob.GNB];
    private static readonly CombatJob[] LeaderboardHealers =
        [CombatJob.WHM, CombatJob.SCH, CombatJob.AST, CombatJob.SGE];
    private static readonly CombatJob[] LeaderboardMelee =
        [CombatJob.MNK, CombatJob.DRG, CombatJob.NIN, CombatJob.SAM, CombatJob.RPR, CombatJob.VPR];
    private static readonly CombatJob[] LeaderboardPhysicalRanged =
        [CombatJob.BRD, CombatJob.MCH, CombatJob.DNC];
    private static readonly CombatJob[] LeaderboardCasters =
        [CombatJob.BLM, CombatJob.SMN, CombatJob.RDM, CombatJob.PCT];

    private readonly PluginConfiguration configuration;
    private readonly MatchStore matchStore;
    private readonly LeaderboardClient leaderboardClient;
    private readonly LeaderboardOutbox leaderboardOutbox;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ITextureProvider textureProvider;
    private readonly Action saveConfiguration;
    private readonly object stateGate = new();

    private Guid? selectedMatchId;
    private CombatJob selectedLeaderboardJob;
    private IReadOnlyList<LeaderboardRow> leaderboard = [];
    private CombatJob? loadedLeaderboardJob;
    private CombatJob? leaderboardLoadingJob;
    private string loadedLeaderboardServerBaseUrl = string.Empty;
    private DateTime? leaderboardLoadedAtUtc;
    private string status = "Ready. New CC result screens will be recorded automatically.";
    private bool statusIsError;
    private bool networkBusy;
    private bool confirmAccountDeletion;

    public MainWindow(
        PluginConfiguration configuration,
        MatchStore matchStore,
        LeaderboardClient leaderboardClient,
        LeaderboardOutbox leaderboardOutbox,
        IClientState clientState,
        IPlayerState playerState,
        ITextureProvider textureProvider,
        Action saveConfiguration)
        : base("Crystal Job Rank###CrystalJobRankMain")
    {
        this.configuration = configuration;
        this.matchStore = matchStore;
        this.leaderboardClient = leaderboardClient;
        this.leaderboardOutbox = leaderboardOutbox;
        this.clientState = clientState;
        this.playerState = playerState;
        this.textureProvider = textureProvider;
        this.saveConfiguration = saveConfiguration;
        selectedLeaderboardJob = matchStore.Snapshot().FirstOrDefault()?.LocalJob ?? CombatJob.PLD;

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
        bool currentNetworkBusy;
        CombatJob? currentLoadedJob;
        CombatJob? currentLoadingJob;
        string currentLoadedServerBaseUrl;
        DateTime? currentLoadedAtUtc;
        IReadOnlyList<LeaderboardRow> currentLeaderboard;
        lock (stateGate)
        {
            currentNetworkBusy = networkBusy;
            currentLoadedJob = loadedLeaderboardJob;
            currentLoadingJob = leaderboardLoadingJob;
            currentLoadedServerBaseUrl = loadedLeaderboardServerBaseUrl;
            currentLoadedAtUtc = leaderboardLoadedAtUtc;
            currentLeaderboard = leaderboard;
        }

        var hasCurrentIdentity = LocalCharacterIdentityReader.TryRead(
            clientState,
            playerState,
            out var currentIdentity,
            out var identityUnavailableReason);
        var currentIdentityMatchesRegistration =
            hasCurrentIdentity &&
            configuration.MatchesRegisteredIdentity(currentIdentity.CharacterName, currentIdentity.WorldId);
        if (!currentNetworkBusy) UpdateOutboxState(currentIdentityMatchesRegistration);

        ImGui.TextColored(new Vector4(0.82f, 0.75f, 1f, 1f), "OPTIONAL COMMUNITY LEADERBOARD");
        ImGui.TextWrapped("Optional: joining publishes your automatically detected character name, Home World, and per-job leaderboard statistics.");
        ImGui.TextWrapped("Sharing sends only your own future Casual and Ranked results—never other players' data or Square Enix account IDs. You can stop sharing or delete the online identity at any time.");
        ImGui.Spacing();

        ImGui.TextDisabled("CURRENT CHARACTER");
        if (hasCurrentIdentity)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.85f, 1f, 1f), currentIdentity.DisplayLabel);
            ImGui.TextDisabled("Read automatically from Dalamud. This identity cannot be edited in the plugin.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.65f, 0.32f, 1f), "No complete logged-in character identity available.");
            ImGui.TextDisabled(identityUnavailableReason);
        }
        ImGui.Spacing();

        var serverUrl = configuration.ServerBaseUrl;
        if (configuration.IsRegistered || currentNetworkBusy) ImGui.BeginDisabled();
        if (ImGui.InputText("Server URL (advanced)", ref serverUrl, 256))
        {
            configuration.ServerBaseUrl = serverUrl;
            saveConfiguration();
            ClearLeaderboardSnapshot();
        }
        if (configuration.IsRegistered || currentNetworkBusy) ImGui.EndDisabled();
        if (configuration.IsRegistered)
        {
            ImGui.TextDisabled("The server address is locked while this leaderboard identity exists, so its API key cannot be sent to another host.");
        }
        else
        {
            ImGui.TextDisabled("A custom server has a different operator and may follow a different privacy policy.");
        }
        var validServerUrl = IsValidServerBaseUrl(configuration.ServerBaseUrl);
        if (!validServerUrl)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.42f, 0.42f, 1f),
                "Enter an HTTPS leaderboard URL (HTTP is allowed only for localhost development)." );
        }

        if (!configuration.IsRegistered)
        {
            var canRegister = hasCurrentIdentity && validServerUrl && !currentNetworkBusy;
            if (!canRegister) ImGui.BeginDisabled();
            var registerLabel = hasCurrentIdentity
                ? $"Register {currentIdentity.DisplayLabel}"
                : "Log in to register";
            if (ImGui.Button(currentNetworkBusy ? "Please wait..." : registerLabel) && canRegister)
            {
                _ = RegisterAsync(currentIdentity);
            }
            if (!canRegister) ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(!hasCurrentIdentity
                ? "A complete logged-in character and Home World are required."
                : !validServerUrl
                    ? "Enter a valid leaderboard server URL first."
                    : "This publishes the shown character identity; match sharing starts separately below.");
        }
        else
        {
            ImGui.TextUnformatted($"Registered leaderboard identity: {configuration.RegisteredIdentityLabel}");
            if (currentIdentityMatchesRegistration)
            {
                ImGui.TextColored(new Vector4(0.45f, 0.9f, 0.55f, 1f), "The logged-in character matches this identity.");
            }
            else
            {
                var currentLabel = hasCurrentIdentity ? currentIdentity.DisplayLabel : "no logged-in character";
                ImGui.TextColored(
                    new Vector4(1f, 0.65f, 0.32f, 1f),
                    $"Sharing is paused: currently {currentLabel}. Switch to {configuration.RegisteredIdentityLabel}.");
            }

            var share = configuration.ShareLeaderboard;
            var canChangeSharing = currentIdentityMatchesRegistration || share;
            if (!canChangeSharing) ImGui.BeginDisabled();
            if (ImGui.Checkbox("Share future Casual and Ranked results", ref share))
            {
                if (!share || currentIdentityMatchesRegistration)
                {
                    configuration.ShareLeaderboard = share;
                    saveConfiguration();
                    UpdateOutboxState(currentIdentityMatchesRegistration);
                }
            }
            if (!canChangeSharing) ImGui.EndDisabled();
            ImGui.TextDisabled(currentIdentityMatchesRegistration
                ? $"Pending uploads: {leaderboardOutbox.PendingCount}. Temporary network and server failures retry automatically."
                : $"Pending uploads: {leaderboardOutbox.PendingCount}. They remain local and paused until the registered character is active.");

            if (!confirmAccountDeletion)
            {
                if (currentNetworkBusy) ImGui.BeginDisabled();
                if (ImGui.Button("Delete leaderboard account") && !currentNetworkBusy) confirmAccountDeletion = true;
                if (currentNetworkBusy) ImGui.EndDisabled();
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.42f, 0.42f, 1f), "This immediately removes the identity and all submitted matches from the active service. Cloudflare recovery backups can retain a copy for up to 7 days.");
                if (currentNetworkBusy) ImGui.BeginDisabled();
                if (ImGui.Button(currentNetworkBusy ? "Please wait..." : "Confirm online deletion") && !currentNetworkBusy)
                {
                    _ = DeleteAccountAsync();
                }
                if (currentNetworkBusy) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) confirmAccountDeletion = false;
            }
        }

        ImGui.Separator();
        ImGui.TextColored(RankVisuals.JobColor(selectedLeaderboardJob), $"{selectedLeaderboardJob} COMMUNITY STANDINGS");
        ImGui.TextWrapped("Every job has a completely separate rating and ranking. Choose one job below; the public board combines participating players across all regions.");
        ImGui.TextDisabled("Character name and Home World are public. Region is not uploaded or used as a filter.");
        ImGui.Spacing();

        if (currentNetworkBusy) ImGui.BeginDisabled();
        var jobChanged = DrawLeaderboardJobGroup("Tank", LeaderboardTanks);
        jobChanged |= DrawLeaderboardJobGroup("Healer", LeaderboardHealers);
        jobChanged |= DrawLeaderboardJobGroup("Melee", LeaderboardMelee);
        jobChanged |= DrawLeaderboardJobGroup("Physical ranged", LeaderboardPhysicalRanged);
        jobChanged |= DrawLeaderboardJobGroup("Caster", LeaderboardCasters);
        if (currentNetworkBusy) ImGui.EndDisabled();
        if (jobChanged && validServerUrl && !currentNetworkBusy)
        {
            _ = RefreshLeaderboardAsync();
            currentNetworkBusy = true;
            currentLoadingJob = selectedLeaderboardJob;
        }
        ImGui.Spacing();

        var selectedServerBaseUrl = NormalizeServerBaseUrl(configuration.ServerBaseUrl);
        var hasLoadedSelection = currentLoadedJob == selectedLeaderboardJob &&
            string.Equals(
                currentLoadedServerBaseUrl,
                selectedServerBaseUrl,
                StringComparison.OrdinalIgnoreCase);
        var buttonLabel = currentLoadingJob.HasValue
            ? $"Loading {currentLoadingJob}..."
            : hasLoadedSelection
                ? $"Refresh {selectedLeaderboardJob} standings"
                : $"Load {selectedLeaderboardJob} standings";
        if (currentNetworkBusy || !validServerUrl) ImGui.BeginDisabled();
        if (ImGui.Button(buttonLabel) && !currentNetworkBusy && validServerUrl)
        {
            _ = RefreshLeaderboardAsync();
        }
        if (currentNetworkBusy || !validServerUrl) ImGui.EndDisabled();

        ImGui.SameLine();
        if (currentLoadingJob == selectedLeaderboardJob)
        {
            ImGui.TextDisabled(hasLoadedSelection
                ? "Refreshing; the previous snapshot remains visible."
                : "Loading the top 50 entries...");
        }
        else if (hasLoadedSelection && currentLoadedAtUtc.HasValue)
        {
            ImGui.TextDisabled($"Top {currentLeaderboard.Count} • updated {currentLoadedAtUtc.Value.ToLocalTime():g}");
        }
        else
        {
            ImGui.TextDisabled($"{selectedLeaderboardJob} has not been loaded yet.");
        }

        if (!hasLoadedSelection)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Load {selectedLeaderboardJob} to view only that job's current community standings.");
            return;
        }

        if (currentLeaderboard.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"No {selectedLeaderboardJob} entries have been shared in the current community season yet.");
            return;
        }

        ImGui.Spacing();
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                      ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        var tableHeight = MathF.Max(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable($"##leaderboard-{selectedLeaderboardJob}", 7, flags, new Vector2(0, tableHeight))) return;
        foreach (var header in new[] { "#", "Player", "Tier", "Rating", "Matches", "Record", "Win rate" })
        {
            ImGui.TableSetupColumn(header);
        }
        ImGui.TableHeadersRow();
        foreach (var row in currentLeaderboard)
        {
            ImGui.TableNextRow();
            Cell(row.Rank.ToString());
            var isOwnEntry = configuration.IsRegistered &&
                string.Equals(row.CharacterName, configuration.RegisteredCharacterName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.WorldName, configuration.RegisteredWorldName, StringComparison.OrdinalIgnoreCase);
            var publicIdentity = $"{row.CharacterName} · {row.WorldName}";
            Cell(isOwnEntry ? $"{publicIdentity}  (you)" : publicIdentity);
            var band = RankVisuals.GetBand(row.Rating);
            ColoredCell(band.Name, band.MetalColor);
            Cell(row.Rating.ToString("N0"));
            Cell(row.Matches.ToString("N0"));
            Cell($"{row.Wins:N0}W  {row.Losses:N0}L");
            Cell($"{row.WinRate:P1}");
        }
        ImGui.EndTable();
    }

    private bool DrawLeaderboardJobGroup(string label, IReadOnlyList<CombatJob> jobs)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        var changed = false;
        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            var selected = job == selectedLeaderboardJob;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Text, RankVisuals.JobColor(job));
            if (ImGui.RadioButton($"{job}##leaderboard-job-{job}", selected))
            {
                selectedLeaderboardJob = job;
                changed = true;
            }
            if (selected) ImGui.PopStyleColor();
            if (index < jobs.Count - 1) ImGui.SameLine();
        }
        return changed;
    }

    private async Task RegisterAsync(LocalCharacterIdentity identity)
    {
        if (!TryBeginNetworkOperation()) return;
        SetStatus($"Registering {identity.DisplayLabel}...");
        try
        {
            var registration = await leaderboardClient.RegisterAsync(
                configuration.ServerBaseUrl,
                identity.CharacterName,
                identity.WorldId,
                identity.WorldName);
            configuration.SetRegisteredIdentity(
                registration.PlayerId,
                registration.ApiKey,
                identity.CharacterName,
                identity.WorldId,
                identity.WorldName);
            saveConfiguration();
            UpdateOutboxState(
                LocalCharacterIdentityReader.TryRead(clientState, playerState, out var current, out _) &&
                configuration.MatchesRegisteredIdentity(current.CharacterName, current.WorldId));
            SetStatus($"{identity.DisplayLabel} registered. Enable automatic sharing when ready; no matches were uploaded.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            EndNetworkOperation();
        }
    }

    private async Task RefreshLeaderboardAsync()
    {
        if (!TryBeginNetworkOperation()) return;
        var requestedJob = selectedLeaderboardJob;
        var requestedServerBaseUrl = NormalizeServerBaseUrl(configuration.ServerBaseUrl);
        lock (stateGate) leaderboardLoadingJob = requestedJob;
        SetStatus($"Loading {requestedJob} community standings...");
        try
        {
            var rows = await leaderboardClient.GetLeaderboardAsync(requestedServerBaseUrl, requestedJob);
            if (rows.Any(row =>
                    row.Job != requestedJob ||
                    !IsNormalizedPublicIdentity(row.CharacterName, row.WorldName)))
            {
                throw new InvalidOperationException("The leaderboard server returned an invalid character identity or job.");
            }

            lock (stateGate)
            {
                leaderboard = rows.ToArray();
                loadedLeaderboardJob = requestedJob;
                loadedLeaderboardServerBaseUrl = requestedServerBaseUrl;
                leaderboardLoadedAtUtc = DateTime.UtcNow;
            }
            SetStatus(rows.Count == 0
                ? $"No {requestedJob} entries exist in the current community season yet."
                : $"Loaded {rows.Count} {requestedJob} community leaderboard entries.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, true);
        }
        finally
        {
            lock (stateGate)
            {
                if (leaderboardLoadingJob == requestedJob) leaderboardLoadingJob = null;
            }
            EndNetworkOperation();
        }
    }

    private async Task DeleteAccountAsync()
    {
        if (!TryBeginNetworkOperation()) return;
        SetStatus("Deleting leaderboard identity and submitted matches...");
        try
        {
            await leaderboardOutbox.PauseAsync();
            await leaderboardClient.DeleteAccountAsync(configuration.ServerBaseUrl, configuration.ApiKey);
            configuration.ClearRegisteredIdentity();
            confirmAccountDeletion = false;
            saveConfiguration();
            UpdateOutboxState(false);
            SetStatus("Leaderboard account and active submissions were deleted. Local history was kept; recovery backups expire within 7 days.");
        }
        catch (Exception exception)
        {
            UpdateOutboxState(
                LocalCharacterIdentityReader.TryRead(clientState, playerState, out var current, out _) &&
                configuration.MatchesRegisteredIdentity(current.CharacterName, current.WorldId));
            SetStatus(exception.Message, true);
        }
        finally
        {
            EndNetworkOperation();
        }
    }

    private bool TryBeginNetworkOperation()
    {
        lock (stateGate)
        {
            if (networkBusy) return false;
            networkBusy = true;
            return true;
        }
    }

    private void EndNetworkOperation()
    {
        lock (stateGate) networkBusy = false;
    }

    private void ClearLeaderboardSnapshot()
    {
        lock (stateGate)
        {
            leaderboard = [];
            loadedLeaderboardJob = null;
            loadedLeaderboardServerBaseUrl = string.Empty;
            leaderboardLoadedAtUtc = null;
        }
    }

    private static bool IsNormalizedPublicIdentity(string characterName, string worldName)
    {
        try
        {
            return string.Equals(
                       Validation.NormalizeCharacterName(characterName),
                       characterName,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       Validation.NormalizeWorldName(worldName),
                       worldName,
                       StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeServerBaseUrl(string value) => value.Trim().TrimEnd('/');

    private static bool IsValidServerBaseUrl(string value)
    {
        if (!Uri.TryCreate(NormalizeServerBaseUrl(value) + "/", UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttps ||
               (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    private void UpdateOutboxState(bool currentIdentityMatchesRegistration) => leaderboardOutbox.UpdateState(
        configuration.ServerBaseUrl,
        configuration.IsRegistered ? configuration.PlayerId : null,
        configuration.IsRegistered ? configuration.ApiKey : string.Empty,
        configuration.ShareLeaderboard && currentIdentityMatchesRegistration);

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static void ColoredCell(string value, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(color, value);
    }
}
