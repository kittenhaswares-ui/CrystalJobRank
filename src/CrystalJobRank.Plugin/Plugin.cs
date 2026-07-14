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
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        IDataManager dataManager,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
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

        commandManager.AddHandler(Command, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Open Crystal Job Rank.",
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

    private void OnMatchCaptured(MatchRecord captured)
    {
        if (disposed) return;

        try
        {
            var saved = matchStore.Add(captured);
            if (saved is null) return;

            mainWindow.NotifyMatch(saved);
            log.Information(
                "Recorded CC match on {Job}: {Outcome}, rating {Before}->{After} ({Delta:+#;-#;0}).",
                saved.LocalJob,
                saved.Outcome,
                saved.RatingBefore,
                saved.RatingAfter,
                saved.RatingDelta);

            if (Configuration.ShareLeaderboard && Configuration.IsRegistered)
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
