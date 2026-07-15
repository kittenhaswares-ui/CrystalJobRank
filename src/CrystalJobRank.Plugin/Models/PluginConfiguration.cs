using System.Security.Cryptography;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CrystalJobRank.Plugin.Models;

public sealed class PluginConfiguration : IPluginConfiguration
{
    private const int CurrentVersion = 4;
    private const string HostedServerBaseUrl = "https://crystal-job-rank-api.kittenhaswares.workers.dev";
    private const string LegacyServerBaseUrl = "https://example.invalid";
    private const string LegacyServerBaseUrlWithSlash = "https://example.invalid/";

    public int Version { get; set; } = CurrentVersion;
    public string ServerBaseUrl { get; set; } = HostedServerBaseUrl;
    public string InstallationKey { get; set; } = CreateInstallationKey();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface value) => pluginInterface = value;

    public bool MigrateToCurrentVersion()
    {
        var changed = false;
        if (!IsValidInstallationKey(InstallationKey))
        {
            InstallationKey = CreateInstallationKey();
            changed = true;
        }

        if (Version >= CurrentVersion) return changed;

        if (string.Equals(ServerBaseUrl, LegacyServerBaseUrl, StringComparison.Ordinal) ||
            string.Equals(ServerBaseUrl, LegacyServerBaseUrlWithSlash, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(ServerBaseUrl))
        {
            ServerBaseUrl = HostedServerBaseUrl;
            changed = true;
        }

        Version = CurrentVersion;
        return true;
    }

    public void Save() => pluginInterface?.SavePluginConfig(this);

    private static bool IsValidInstallationKey(string? value) =>
        value is { Length: 43 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string CreateInstallationKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
