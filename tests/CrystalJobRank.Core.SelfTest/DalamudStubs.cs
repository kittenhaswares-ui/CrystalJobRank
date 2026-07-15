namespace Dalamud.Configuration
{
    public interface IPluginConfiguration
    {
        int Version { get; set; }
    }
}

namespace Dalamud.Plugin
{
    public interface IDalamudPluginInterface
    {
        void SavePluginConfig(object configuration);
    }
}

namespace Dalamud.Plugin.Services
{
    // Selected persistence/network services are linked into the dependency-free
    // self-test project. Production still compiles against Dalamud's real API;
    // these tiny surfaces only let the linked source be checked without loading
    // a game installation.
    public interface IPluginLog
    {
        void Error(string messageTemplate, params object[] values);
        void Error(Exception exception, string messageTemplate, params object[] values);
        void Warning(string messageTemplate, params object[] values);
        void Warning(Exception exception, string messageTemplate, params object[] values);
    }

    public sealed class TestPluginLog : IPluginLog
    {
        public void Error(string messageTemplate, params object[] values)
        {
        }

        public void Error(Exception exception, string messageTemplate, params object[] values)
        {
        }

        public void Warning(string messageTemplate, params object[] values)
        {
        }

        public void Warning(Exception exception, string messageTemplate, params object[] values)
        {
        }
    }
}
