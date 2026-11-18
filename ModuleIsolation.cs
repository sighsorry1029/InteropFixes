using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace InteropFixes;

internal static class ModuleIsolation
{
    internal static bool CanInstall(string name, string[] required, string? legacyGuid, bool checkOldBlasted = false)
    {
        foreach (string guid in required)
            if (!Chainloader.PluginInfos.ContainsKey(guid)) return false;

        if (legacyGuid == null && !checkOldBlasted) return true;

        // Do not stack a second implementation over complete or partially installed legacy hooks.
        if (legacyGuid != null && Chainloader.PluginInfos.ContainsKey(legacyGuid))
        {
            InteropFixesPlugin.ModLogger.LogWarning($"{name} integration skipped: {legacyGuid} is installed. Remove its standalone DLL to use the integrated module.");
            return false;
        }
        foreach (MethodBase target in Harmony.GetAllPatchedMethods())
        {
            Patches? info = Harmony.GetPatchInfo(target);
            if (info == null) continue;
            foreach (Patch patch in Enumerate(info))
            {
                if (!IsLegacyPatch(patch.owner, patch.PatchMethod?.DeclaringType?.FullName, legacyGuid, checkOldBlasted)) continue;
                InteropFixesPlugin.ModLogger.LogWarning($"{name} integration skipped: existing legacy hook on {target.DeclaringType?.FullName}.{target.Name} (owner {patch.owner}). Update/remove that legacy module first; other integrations remain available.");
                return false;
            }
        }
        return true;
    }

    internal static bool IsLegacyPatch(string owner, string? patchType, string? legacyGuid, bool checkOldBlasted)
    {
        if (legacyGuid != null && (owner == legacyGuid || owner.StartsWith(legacyGuid + ".", StringComparison.Ordinal))) return true;
        return checkOldBlasted &&
            (owner == InteropFixesPlugin.BlastedGuid + ".MagicPlugin" ||
             (patchType != null && patchType.StartsWith("BlastedSwimmingTarredBugFix.MagicPlugin", StringComparison.Ordinal)));
    }

    private static IEnumerable<Patch> Enumerate(Patches patches)
    {
        foreach (Patch patch in patches.Prefixes) yield return patch;
        foreach (Patch patch in patches.Postfixes) yield return patch;
        foreach (Patch patch in patches.Transpilers) yield return patch;
        foreach (Patch patch in patches.Finalizers) yield return patch;
    }

    internal static bool TryUnpatch(Harmony harmony, string capability)
    {
        Exception? removalError = null;
        try { harmony.UnpatchSelf(); }
        catch (Exception ex) { removalError = ex; }
        try
        {
            foreach (MethodBase original in harmony.GetPatchedMethods())
            {
                InteropFixesPlugin.ModLogger.LogError($"{capability}: could not remove owned hook on {original.DeclaringType?.FullName}.{original.Name}. External replacements will not be restored. {removalError}");
                return false;
            }
        }
        catch (Exception ex)
        {
            InteropFixesPlugin.ModLogger.LogError($"{capability}: could not verify removal of owned hooks: {ex}. {removalError}");
            return false;
        }
        if (removalError != null)
            InteropFixesPlugin.ModLogger.LogWarning($"{capability}: removal reported an error, but no owned hooks remain: {removalError.Message}");
        return true;
    }
}
