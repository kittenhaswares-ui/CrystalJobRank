using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Models;
using CrystalJobRank.Plugin.Services;
using CrystalJobRank.Plugin.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/cjr";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly WindowSystem windowSystem = new("CrystalJobRank");
    private readonly MatchStore matchStore;
    private readonly LeaderboardClient leaderboardClient;
    private readonly LeaderboardOutbox leaderboardOutbox;
    private readonly SerialWorkQueue<MatchRecord> matchPersistence;
    private readonly CrystallineConflictCapture capture;
    private readonly MainWindow mainWindow;
    private bool disposed;

    internal PluginConfiguration Configuration { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        IDataManager dataManager,
        IFramework framework,
        ITextureProvider textureProvider,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        Configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        Configuration.Initialize(pluginInterface);
        if (Configuration.MigrateToCurrentVersion()) Configuration.Save();

        var configDirectory = pluginInterface.GetPluginConfigDirectory();
        matchStore = new MatchStore(configDirectory, log);
        leaderboardClient = new LeaderboardClient();
        leaderboardOutbox = new LeaderboardOutbox(configDirectory, matchStore, leaderboardClient, log);
        mainWindow = new MainWindow(
            Configuration,
            matchStore,
            leaderboardClient,
            leaderboardOutbox,
            textureProvider,
            SaveConfiguration);
        leaderboardOutbox.StatusChanged += OnLeaderboardStatusChanged;
        leaderboardOutbox.UpdateState(Configuration.ServerBaseUrl, Configuration.InstallationKey);
        if (matchStore.AppliedOneTimeUpdateReset)
        {
            mainWindow.SetStatus("New season: ratings reset once to 1500. Match history, records, and badges were kept; leaderboard sharing is now automatic.");
        }
        windowSystem.AddWindow(mainWindow);
        matchPersistence = new SerialWorkQueue<MatchRecord>(StoreCapturedMatch, HandleMatchStoreFailure);

        capture = new CrystallineConflictCapture(
            clientState,
            playerState,
            objectTable,
            dataManager,
            framework,
            interopProvider,
            log);
        capture.MatchCaptured += OnMatchCaptured;

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Crystal Job Rank. Use /cjr reset <JOB|all> to reset local job ratings.",
        });

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public void Dispose()
    {
        disposed = true;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        commandManager.RemoveHandler(Command);
        capture.MatchCaptured -= OnMatchCaptured;
        capture.Dispose();
        // Finish every match that was accepted from the framework before
        // stopping the outbox. Add() returns only after its atomic file replace,
        // so a clean unload preserves the existing atomic-write guarantee.
        matchPersistence.Dispose();
        leaderboardOutbox.StatusChanged -= OnLeaderboardStatusChanged;
        leaderboardOutbox.Dispose();
        leaderboardClient.Dispose();
        windowSystem.RemoveAllWindows();
        Configuration.Save();
    }

    private void Draw() => windowSystem.Draw();

    private void OpenMainUi() => mainWindow.IsOpen = true;

    private void SaveConfiguration() => Configuration.Save();

    private void OnCommand(string _, string arguments)
    {
        try
        {
            HandleCommand(arguments);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Crystal Job Rank command failed.");
            const string message = "The command failed; no rating reset was saved. See the Dalamud log for details.";
            mainWindow.SetStatus(message, true);
            chatGui.PrintError($"[Crystal Job Rank] {message}");
        }
    }

    private void HandleCommand(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            mainWindow.Toggle();
            return;
        }

        if (parts.Length == 1 &&
            (parts[0].Equals("open", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("config", StringComparison.OrdinalIgnoreCase)))
        {
            mainWindow.IsOpen = true;
            return;
        }

        if (parts.Length == 1 && parts[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandHelp();
            return;
        }

        if (parts.Length != 2 || !parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandHelp(true);
            return;
        }

        if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            matchPersistence.Drain();
            var resetJobs = matchStore.ResetAllRatings();
            if (resetJobs.Count == 0)
            {
                Print("No active job ratings to reset for the latest captured character.");
                return;
            }

            var jobs = string.Join(", ", resetJobs);
            var message = $"Reset {resetJobs.Count} local job ratings ({jobs}) for the latest captured character to 1500 Bronze. Match history kept. Community leaderboard unchanged.";
            mainWindow.SetStatus(message);
            Print(message);
            log.Information("Reset all active local rating epochs for {Jobs}.", jobs);
            return;
        }

        if (!CombatJobs.TryParseAbbreviation(parts[1], out var job))
        {
            chatGui.PrintError($"[Crystal Job Rank] Unknown job abbreviation '{parts[1]}'. Use: {CombatJobs.AbbreviationList}, or all.");
            return;
        }

        matchPersistence.Drain();
        if (!matchStore.ResetRating(job))
        {
            Print($"{job} has no active rating to reset for the latest captured character.");
            return;
        }

        var success = $"{job} rating for the latest captured character reset to 1500 Bronze. Match history kept. Community leaderboard unchanged.";
        mainWindow.SetStatus(success);
        Print(success);
        log.Information("Reset local {Job} rating epoch.", job);
    }

    private void PrintCommandHelp(bool error = false)
    {
        const string usage = "Usage: /cjr, /cjr reset <JOB|all>, or /cjr help.";
        if (error) chatGui.PrintError($"[Crystal Job Rank] {usage}");
        else Print($"{usage} Jobs: {CombatJobs.AbbreviationList}.");
    }

    private void Print(string message) => chatGui.Print($"[Crystal Job Rank] {message}");

    private void OnMatchCaptured(MatchRecord captured)
    {
        if (disposed || matchPersistence.TryEnqueue(captured)) return;

        log.Warning("A captured match arrived after the persistence queue stopped and was not accepted.");
    }

    // MatchStore.Add performs an atomic write and may rebuild historical
    // aggregates. The serial worker keeps that growing work off Dalamud's
    // framework update while preserving admission order.
    private void StoreCapturedMatch(MatchRecord captured)
    {
        var saved = matchStore.Add(captured);
        if (saved is null) return;

        mainWindow.NotifyMatch(saved);
        log.Information(
            "Recorded {Queue} CC match on {Job}: {Outcome}, rating {Before}->{After} ({Delta:+#;-#;0}).",
            saved.Queue,
            saved.LocalJob,
            saved.Outcome,
            saved.RatingBefore,
            saved.RatingAfter,
            saved.RatingDelta);

        leaderboardOutbox.Enqueue(saved.Id);
    }

    private void HandleMatchStoreFailure(MatchRecord _, Exception exception)
    {
        log.Error(exception, "Failed to store a captured match.");
        mainWindow.SetStatus("Match capture failed; see the Dalamud log.", true);
    }

    private void OnLeaderboardStatusChanged(string message, bool isError)
    {
        if (!disposed) mainWindow.SetStatus(message, isError);
    }

}
