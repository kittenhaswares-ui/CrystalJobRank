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
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        Configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        Configuration.Initialize(pluginInterface);

        matchStore = new MatchStore(pluginInterface.GetPluginConfigDirectory(), log);
        leaderboardClient = new LeaderboardClient();
        mainWindow = new MainWindow(Configuration, matchStore, leaderboardClient, SaveConfiguration);
        windowSystem.AddWindow(mainWindow);

        capture = new CrystallineConflictCapture(
            clientState,
            playerState,
            objectTable,
            dataManager,
            interopProvider,
            log);
        capture.MatchCaptured += OnMatchCaptured;

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Crystal Job Rank. Use /cjr reset <JOB|all> to reset local Ranked ratings.",
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
            var resetJobs = matchStore.ResetAllRatings();
            if (resetJobs.Count == 0)
            {
                Print("No active Ranked job ratings to reset.");
                return;
            }

            var jobs = string.Join(", ", resetJobs);
            var message = $"Reset {resetJobs.Count} local Ranked ratings ({jobs}) to 1500 Bronze. Match history kept. Community leaderboard unchanged.";
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

        if (!matchStore.ResetRating(job))
        {
            Print($"{job} has no active Ranked rating to reset.");
            return;
        }

        var success = $"{job} rating reset to 1500 Bronze. Match history kept. Community leaderboard unchanged.";
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
        if (disposed) return;

        try
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

            if (Configuration.ShareLeaderboard && Configuration.IsRegistered && RatingEngine.IsRatedQueue(saved.Queue))
            {
                _ = SubmitSafelyAsync(saved);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to store a captured match.");
            mainWindow.SetStatus("Match capture failed; see the Dalamud log.", true);
        }
    }

    private async Task SubmitSafelyAsync(MatchRecord match)
    {
        try
        {
            await leaderboardClient.SubmitAsync(Configuration.ServerBaseUrl, Configuration.ApiKey, match);
            mainWindow.SetStatus("Match saved locally and shared with the community leaderboard.");
        }
        catch (Exception exception)
        {
            log.Warning(exception, "The match was saved locally but could not be uploaded.");
            mainWindow.SetStatus("Saved locally; leaderboard upload failed.", true);
        }
    }
}
