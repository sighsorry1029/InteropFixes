using System.Collections.Generic;

// The fixture does not construct Unity objects or start BepInEx. The lookup and
// transpiler sources themselves are linked unchanged from the production folder.
namespace InteropFixes
{
    internal static class InteropFixesPlugin
    {
        internal const string VneiGuid = "com.maxsch.valheim.vnei";
        internal const string PathOfValheimanGuid = "treextr.pathofvalheiman";
        internal static readonly StubLogger ModLogger = new();
    }

    internal sealed class StubLogger
    {
        internal readonly List<string> Messages = new();
        public void LogInfo(object message) => Messages.Add(message.ToString()!);
        public void LogWarning(object message) => Messages.Add(message.ToString()!);
        public void LogError(object message) => Messages.Add(message.ToString()!);
    }
}

namespace BepInEx.Bootstrap
{
    public sealed class PluginInfo { public object? Instance; }
    public static class Chainloader { public static readonly Dictionary<string, PluginInfo> PluginInfos = new(); }
}

