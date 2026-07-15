using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CrystalJobRank.Plugin.Models;

public sealed class PluginConfiguration : IPluginConfiguration
{
    private const int CurrentVersion = 2;
    private const string HostedServerBaseUrl = "https://crystal-job-rank-api.kittenhaswares.workers.dev";
    private const string LegacyServerBaseUrl = "https://example.invalid";
    private const string LegacyServerBaseUrlWithSlash = "https://example.invalid/";

    public int Version { get; set; } = CurrentVersion;
    public bool ShareLeaderboard { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ServerBaseUrl { get; set; } = HostedServerBaseUrl;
    public Guid? PlayerId { get; set; }
    public string ApiKey { get; set; } = string.Empty;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface value) => pluginInterface = value;

    public bool MigrateToCurrentVersion()
    {
        if (Version >= CurrentVersion) return false;

        if (string.Equals(ServerBaseUrl, LegacyServerBaseUrl, StringComparison.Ordinal) ||
            string.Equals(ServerBaseUrl, LegacyServerBaseUrlWithSlash, StringComparison.Ordinal))
        {
            ServerBaseUrl = HostedServerBaseUrl;
        }

        Version = CurrentVersion;
        return true;
    }

    public void Save() => pluginInterface?.SavePluginConfig(this);

    public bool IsRegistered => PlayerId.HasValue && !string.IsNullOrWhiteSpace(ApiKey);
}
