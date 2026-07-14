using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CrystalJobRank.Plugin.Models;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool ShareLeaderboard { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ServerBaseUrl { get; set; } = "https://example.invalid";
    public Guid? PlayerId { get; set; }
    public string ApiKey { get; set; } = string.Empty;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface value) => pluginInterface = value;

    public void Save() => pluginInterface?.SavePluginConfig(this);

    public bool IsRegistered => PlayerId.HasValue && !string.IsNullOrWhiteSpace(ApiKey);
}

