using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace InteropFixes.Modules.PathOfValheimanLookup;

internal static class PathOfValheimanLookupModule
{
    internal const string PovGuid = "treextr.pathofvalheiman";
    internal const string VneiGuid = "com.maxsch.valheim.vnei";
    private static readonly List<MethodInfo> Installed = new();
    private static readonly MethodInfo Patch = typeof(OptionalLookupPatch).GetMethod(nameof(OptionalLookupPatch.Transpiler), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static Harmony? _harmony;
    private static bool _active;

    internal static bool TryInstall(Harmony harmony)
    {
        if (_active) return true;
        if (!Chainloader.PluginInfos.ContainsKey(VneiGuid) ||
            !Chainloader.PluginInfos.TryGetValue(PovGuid, out var plugin) || plugin.Instance == null) return false;
        Stop();
        if (Installed.Count != 0) return false;
        _harmony = harmony;
        try
        {
            Type entry = plugin.Instance.GetType();
            if (entry.FullName != "PassiveTree.Plugin") throw new NotSupportedException("Unexpected PoV entry type.");
            MethodInfo[] targets = OptionalLookupPatch.GetTargets(entry.Assembly);
            // Verify every original before modifying anything. The transpiler also
            // checks the live incoming stream after other owners' transformations.
            foreach (MethodInfo target in targets)
                if (!OptionalLookupPatch.TryRewrite(target, PatchProcessor.GetOriginalInstructions(target), out _, out string reason))
                    throw new NotSupportedException(target.DeclaringType?.FullName + "." + target.Name + ": " + reason);
            foreach (MethodInfo target in targets)
            {
                Installed.Add(target); // Include partial Harmony failures in rollback.
                harmony.Patch(target, transpiler: new HarmonyMethod(Patch) { priority = Priority.Last });
                if (!OptionalLookupPatch.WasAccepted(target)) throw new NotSupportedException("Active PoV lookup instructions changed.");
            }
            _active = true;
            InteropFixesPlugin.ModLogger.LogInfo("PoV / VNEI optional lookup enabled: BetterArchery, ExtraSlots and SeneaLUI use exact type names; PoV retains equipment and recipe handling.");
            return true;
        }
        catch (Exception error)
        {
            Stop();
            InteropFixesPlugin.ModLogger.LogWarning("PoV / VNEI optional lookup disabled; original PoV behavior is retained. " + error.Message);
            return false;
        }
    }

    internal static void Stop()
    {
        _active = false;
        for (int i = Installed.Count - 1; i >= 0; i--)
        {
            try
            {
                _harmony!.Unpatch(Installed[i], HarmonyPatchType.Transpiler, _harmony.Id);
                if (Harmony.GetPatchInfo(Installed[i])?.Transpilers.Any(p => p.owner == _harmony.Id && p.PatchMethod == Patch) == true)
                {
                    InteropFixesPlugin.ModLogger.LogWarning("PoV optional lookup hook remains after removal; its stateless lookup helpers remain available.");
                    continue;
                }
                Installed.RemoveAt(i);
            }
            catch (Exception error)
            {
                InteropFixesPlugin.ModLogger.LogWarning("Could not remove a PoV optional lookup hook: " + error.Message);
            }
        }
        // Never reset PoV's own once-only caches or undo its optional-mod hooks.
        if (Installed.Count != 0) return;
        _harmony = null;
        OptionalLookupPatch.Clear();
    }
}
