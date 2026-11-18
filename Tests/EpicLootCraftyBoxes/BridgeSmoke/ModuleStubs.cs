using System.Reflection;

namespace InteropFixes.Modules.EpicLootCraftyBoxes
{
    internal static class ModuleIsolation
    {
        internal static bool TryUnpatch(HarmonyLib.Harmony harmony, string capability)
        {
            try { harmony.UnpatchSelf(); return true; }
            catch { return false; }
        }
    }
}

namespace BepInEx.Bootstrap
{
    public sealed class PluginInfo(object instance) { public object Instance = instance; }
    public static class Chainloader { public static readonly Dictionary<string, PluginInfo> PluginInfos = []; }
}

namespace HarmonyLib
{
    public sealed class HarmonyMethod(Type type, string name)
    {
        public int priority;
        public MethodInfo Method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    }
    public static class Priority { public const int Last = 0; }
    public sealed class Patch(string ownerValue, MethodInfo method)
    {
        public string owner = ownerValue;
        public MethodInfo PatchMethod = method;
    }
    public sealed class Patches
    {
        public readonly List<Patch> Prefixes = [], Postfixes = [], Finalizers = [], Transpilers = [];
    }
    public sealed class Harmony(string id)
    {
        public string Id = id;
        public int UnpatchCount;
        public bool ThrowOnUnpatch;
        private static readonly Dictionary<MethodBase, Patches> Patches = [];
        public static Patches? GetPatchInfo(MethodBase target) => Patches.GetValueOrDefault(target);
        public void Patch(MethodBase target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null, HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null)
        {
            if (!Patches.TryGetValue(target, out var hooks)) Patches[target] = hooks = new();
            if (prefix != null) hooks.Prefixes.Add(new Patch(Id, prefix.Method));
            if (postfix != null) hooks.Postfixes.Add(new Patch(Id, postfix.Method));
            if (finalizer != null) hooks.Finalizers.Add(new Patch(Id, finalizer.Method));
        }
        public void UnpatchSelf()
        {
            UnpatchCount++;
            if (ThrowOnUnpatch) throw new InvalidOperationException("Simulated rollback failure");
            foreach (var hooks in Patches.Values)
            {
                hooks.Prefixes.RemoveAll(p => p.owner == Id);
                hooks.Postfixes.RemoveAll(p => p.owner == Id);
                hooks.Finalizers.RemoveAll(p => p.owner == Id);
            }
        }
    }
}
