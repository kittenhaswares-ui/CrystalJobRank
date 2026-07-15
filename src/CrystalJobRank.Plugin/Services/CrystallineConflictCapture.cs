using System.Collections.Concurrent;
using System.Text;
using CrystalJobRank.Core;
using CrystalJobRank.Plugin.Infrastructure;
using CrystalJobRank.Plugin.Models;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace CrystalJobRank.Plugin.Services;

internal sealed class CrystallineConflictCapture : IDisposable
{
    private delegate void MatchEndDelegate(nint director, nint results, nint value, uint unknown);

    // Patch 7.5 / Dalamud API 15. This must be re-verified after game updates.
    [Signature(
        "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 85 ?? ?? ?? ?? 0F B6 42",
        DetourName = nameof(OnMatchEnd))]
    private readonly Hook<MatchEndDelegate> matchEndHook = null!;

    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<MatchRecord> pendingRecords = new();
    private volatile bool disposed;

    public event Action<MatchRecord>? MatchCaptured;

    public CrystallineConflictCapture(
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        IDataManager dataManager,
        IFramework framework,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.framework = framework;
        this.log = log;

        interopProvider.InitializeFromAttributes(this);
        framework.Update += OnFrameworkUpdate;
        matchEndHook.Enable();
        log.Information("Crystal Job Rank post-match capture initialized at {Address:X}.", matchEndHook.Address);
    }

    public void Dispose()
    {
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        matchEndHook.Dispose();
    }

    private unsafe void OnMatchEnd(nint director, nint results, nint value, uint unknown)
    {
        MatchRecord? record = null;
        try
        {
            if (results == nint.Zero) return;
            var packet = *(CrystallineConflictResultsPacket*)results;
            record = Parse(packet);
        }
        catch (Exception exception)
        {
            log.Error(exception, "Failed to parse the Crystalline Conflict post-match payload.");
        }
        finally
        {
            matchEndHook.Original(director, results, value, unknown);
            if (record is not null && !disposed) pendingRecords.Enqueue(record);
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        while (!disposed && pendingRecords.TryDequeue(out var record))
        {
            try
            {
                MatchCaptured?.Invoke(record);
            }
            catch (Exception exception)
            {
                log.Error(exception, "A captured Crystalline Conflict match could not be handed to the plugin.");
            }
        }
    }

    private unsafe MatchRecord? Parse(CrystallineConflictResultsPacket packet)
    {
        if (packet.Result is not (1 or 2) || packet.MatchLength is < 10 or > 1800)
        {
            log.Warning("Ignoring an invalid post-match result payload (result={Result}, duration={Duration}).", packet.Result, packet.MatchLength);
            return null;
        }

        var localContentId = playerState.ContentId;
        var localName = objectTable.LocalPlayer?.Name.ToString() ?? string.Empty;
        var localWorldId = (ushort)(objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        var scoreboard = new List<(ulong ContentId, PlayerScoreboardRow Row)>(10);
        var worldSheet = dataManager.GetExcelSheet<World>();

        var players = packet.PlayerSpan;
        for (var index = 0; index < players.Length; index++)
        {
            ref var player = ref players[index];
            var job = JobMapper.FromClassJobId(player.ClassJobId);
            if (job == CombatJob.Unknown) continue;

            var name = ReadName(ref player);
            var world = worldSheet?.GetRow(player.WorldId).Name.ToString() ?? player.WorldId.ToString();
            scoreboard.Add((
                player.ContentId,
                new PlayerScoreboardRow
                {
                    Name = name,
                    WorldId = player.WorldId,
                    World = world,
                    Job = job,
                    Team = player.Team,
                    Stats = new ScoreboardStats(
                        player.Kills,
                        player.Deaths,
                        player.Assists,
                        player.DamageDealt,
                        player.DamageTaken,
                        player.HpRestored,
                        player.TimeOnCrystal),
                }));
        }

        var local = scoreboard.FirstOrDefault(x => localContentId != 0 && x.ContentId == localContentId);
        if (local.Row is null)
        {
            local = scoreboard.FirstOrDefault(x =>
                string.Equals(x.Row.Name, localName, StringComparison.OrdinalIgnoreCase) &&
                (localWorldId == 0 || string.Equals(
                    x.Row.World,
                    worldSheet?.GetRow(localWorldId).Name.ToString(),
                    StringComparison.OrdinalIgnoreCase)));
        }

        if (local.Row is null)
        {
            log.Warning("The local player could not be identified in the post-match payload.");
            return null;
        }

        var territoryId = checked((ushort)clientState.TerritoryType);
        var dutyId = GameMain.Instance() is null ? (ushort)0 : GameMain.Instance()->CurrentContentFinderConditionId;

        return new MatchRecord
        {
            CompletedAtUtc = DateTime.UtcNow,
            CharacterName = Validation.NormalizeCharacterName(local.Row.Name),
            WorldId = Validation.ValidateWorldId(local.Row.WorldId),
            WorldName = Validation.NormalizeWorldName(local.Row.World),
            LocalJob = local.Row.Job,
            Outcome = packet.Result == 1 ? MatchOutcome.Win : MatchOutcome.Loss,
            Queue = MatchMetadata.Queue(dutyId),
            ContentFinderConditionId = dutyId,
            TerritoryId = territoryId,
            Arena = MatchMetadata.ArenaName(territoryId),
            DurationSeconds = packet.MatchLength,
            AstraProgressTenths = checked((int)packet.AstraProgress),
            UmbraProgressTenths = checked((int)packet.UmbraProgress),
            LocalStats = local.Row.Stats,
            Scoreboard = scoreboard.Select(x => x.Row).ToList(),
        };
    }

    private static unsafe string ReadName(ref ResultPlayer player)
    {
        fixed (byte* pointer = player.PlayerName)
        {
            var length = 0;
            while (length < 42 && pointer[length] != 0) length++;
            return Encoding.UTF8.GetString(pointer, length);
        }
    }
}
