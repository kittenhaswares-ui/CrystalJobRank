using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace CrystalJobRank.Plugin.Models;

public sealed class PluginConfiguration : IPluginConfiguration
{
    private const int CurrentVersion = 3;
    private const string HostedServerBaseUrl = "https://crystal-job-rank-api.kittenhaswares.workers.dev";
    private const string LegacyServerBaseUrl = "https://example.invalid";
    private const string LegacyServerBaseUrlWithSlash = "https://example.invalid/";

    public int Version { get; set; } = CurrentVersion;
    public bool ShareLeaderboard { get; set; }
    public string ServerBaseUrl { get; set; } = HostedServerBaseUrl;
    public Guid? PlayerId { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string RegisteredCharacterName { get; set; } = string.Empty;
    public uint RegisteredWorldId { get; set; }
    public string RegisteredWorldName { get; set; } = string.Empty;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    [NonSerialized]
    private bool clearedLegacyLeaderboardIdentity;

    public void Initialize(IDalamudPluginInterface value) => pluginInterface = value;

    public bool MigrateToCurrentVersion()
    {
        if (Version >= CurrentVersion) return false;

        if (string.Equals(ServerBaseUrl, LegacyServerBaseUrl, StringComparison.Ordinal) ||
            string.Equals(ServerBaseUrl, LegacyServerBaseUrlWithSlash, StringComparison.Ordinal))
        {
            ServerBaseUrl = HostedServerBaseUrl;
        }

        if (Version < 3)
        {
            clearedLegacyLeaderboardIdentity =
                PlayerId.HasValue ||
                !string.IsNullOrWhiteSpace(ApiKey) ||
                ShareLeaderboard;
            PlayerId = null;
            ApiKey = string.Empty;
            ShareLeaderboard = false;
            RegisteredCharacterName = string.Empty;
            RegisteredWorldId = 0;
            RegisteredWorldName = string.Empty;
        }

        Version = CurrentVersion;
        return true;
    }

    public void Save() => pluginInterface?.SavePluginConfig(this);

    [JsonIgnore]
    public bool IsRegistered =>
        PlayerId.HasValue &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(RegisteredCharacterName) &&
        RegisteredWorldId is > 0 and <= ushort.MaxValue &&
        !string.IsNullOrWhiteSpace(RegisteredWorldName);

    [JsonIgnore]
    public bool ClearedLegacyLeaderboardIdentity => clearedLegacyLeaderboardIdentity;

    [JsonIgnore]
    public string RegisteredIdentityLabel =>
        $"{RegisteredCharacterName} · {RegisteredWorldName}";

    public bool MatchesRegisteredIdentity(string characterName, uint worldId) =>
        IsRegistered &&
        RegisteredWorldId == worldId &&
        string.Equals(RegisteredCharacterName, characterName, StringComparison.OrdinalIgnoreCase);

    public void SetRegisteredIdentity(
        Guid playerId,
        string apiKey,
        string characterName,
        uint worldId,
        string worldName)
    {
        PlayerId = playerId;
        ApiKey = apiKey;
        RegisteredCharacterName = characterName;
        RegisteredWorldId = worldId;
        RegisteredWorldName = worldName;
        ShareLeaderboard = false;
    }

    public void ClearRegisteredIdentity()
    {
        PlayerId = null;
        ApiKey = string.Empty;
        ShareLeaderboard = false;
        RegisteredCharacterName = string.Empty;
        RegisteredWorldId = 0;
        RegisteredWorldName = string.Empty;
    }
}
