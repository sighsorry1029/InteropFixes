using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace InteropFixes.Modules.Vnei;

// VNEI is optional: no signature, field or AssemblyRef in this module mentions a VNEI type.
internal static class VneiItemLookupModule
{
    internal const string VneiGuid = "com.maxsch.valheim.vnei";
    internal const string LegacyGuid = "sighsorry.VneiItemLookupFix";
    private static readonly List<(MethodBase Target, MethodInfo Patch)> Installed = new();
    private static Harmony? _harmony;
    private static bool _active;

    internal static bool TryInstall(Harmony harmony)
    {
        if (_active) return true;
        if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null ||
            Chainloader.PluginInfos.ContainsKey(LegacyGuid) ||
            !Chainloader.PluginInfos.TryGetValue(VneiGuid, out var plugin) || plugin.Instance == null)
            return false;
        // An earlier unsuccessful unpatch must not be overwritten with another adapter.
        Stop();
        if (Installed.Count != 0) return false;
        _harmony = harmony;
        try
        {
            Type pluginType = plugin.Instance.GetType();
            if (pluginType.FullName != "VNEI.Plugin") throw new NotSupportedException("Unexpected VNEI entry type.");
            Assembly assembly = pluginType.Assembly;
            Type indexing = assembly.GetType("VNEI.Logic.Indexing", true)!;
            Type item = assembly.GetType("VNEI.Logic.Item", true)!;
            MethodInfo update = AccessTools.DeclaredMethod(pluginType, "Update", Type.EmptyTypes)
                ?? throw new MissingMethodException(pluginType.FullName, "Update");
            MethodInfo lookup = AccessTools.DeclaredMethod(indexing, "GetItem", new[] { typeof(string) })
                ?? throw new MissingMethodException(indexing.FullName, "GetItem(string)");
            if (update.IsStatic || update.ReturnType != typeof(void) || update.ContainsGenericParameters ||
                !lookup.IsStatic || !lookup.IsPublic || lookup.ReturnType != item || item.IsValueType ||
                lookup.ContainsGenericParameters)
                throw new NotSupportedException("VNEI lookup signature changed.");

            // Compile once; the hotkey path does not use MethodInfo.Invoke or allocate argument arrays.
            var name = Expression.Parameter(typeof(string), "name");
            Func<string, object?> getItem = Expression.Lambda<Func<string, object?>>(
                Expression.Convert(Expression.Call(lookup, name), typeof(object)), name).Compile();
            HoveredItemLookup.Initialize(getItem);
            RecipeLookupPatch.Initialize(pluginType, lookup);
            MethodInfo create = AccessTools.DeclaredMethod(typeof(InventoryGrid), "CreateItemTooltip",
                new[] { typeof(ItemDrop.ItemData), typeof(UITooltip) })
                ?? throw new MissingMethodException(typeof(InventoryGrid).FullName, "CreateItemTooltip");
            MethodInfo set = AccessTools.DeclaredMethod(typeof(UITooltip), "Set",
                new[] { typeof(string), typeof(string), typeof(RectTransform), typeof(Vector2) })
                ?? throw new MissingMethodException(typeof(UITooltip).FullName, "Set");
            if (create.IsStatic || create.ReturnType != typeof(void) || set.IsStatic || set.ReturnType != typeof(void))
                throw new NotSupportedException("Inventory tooltip contract changed.");

            Apply(update, AccessTools.DeclaredMethod(typeof(RecipeLookupPatch), nameof(RecipeLookupPatch.Transpiler)), HarmonyPatchType.Transpiler);
            if (!RecipeLookupPatch.Ready) throw new NotSupportedException("VNEI tooltip lookup flow was not recognized.");
            Apply(create, AccessTools.DeclaredMethod(typeof(HoveredItemLookup), nameof(HoveredItemLookup.Capture)), HarmonyPatchType.Postfix);
            Apply(set, AccessTools.DeclaredMethod(typeof(HoveredItemLookup), nameof(HoveredItemLookup.Invalidate)), HarmonyPatchType.Prefix);
            HoveredItemLookup.Enable();
            _active = true;
            InteropFixesPlugin.ModLogger.LogInfo("VNEI item lookup: inventory item identity enabled.");
            return true;
        }
        catch (Exception error)
        {
            Stop();
            InteropFixesPlugin.ModLogger.LogWarning("VNEI item lookup disabled; original VNEI lookup is retained. " + error.Message);
            return false;
        }
    }

    private static void Apply(MethodBase target, MethodInfo patch, HarmonyPatchType type)
    {
        Installed.Add((target, patch)); // Include partially failed Harmony installs in rollback.
        var method = new HarmonyMethod(patch);
        if (type == HarmonyPatchType.Postfix) method.priority = Priority.Last;
        _harmony!.Patch(target, prefix: type == HarmonyPatchType.Prefix ? method : null,
            postfix: type == HarmonyPatchType.Postfix ? method : null,
            transpiler: type == HarmonyPatchType.Transpiler ? method : null);
    }

    internal static void Stop()
    {
        _active = false;
        // Disable identity first, even if another transpiler makes removing our hooks fail.
        HoveredItemLookup.Disable();
        for (int i = Installed.Count - 1; i >= 0; i--)
        {
            try
            {
                _harmony!.Unpatch(Installed[i].Target, Installed[i].Patch);
                Patches? remaining = Harmony.GetPatchInfo(Installed[i].Target);
                if (remaining != null && remaining.Prefixes.Concat(remaining.Postfixes).Concat(remaining.Transpilers).Concat(remaining.Finalizers)
                    .Any(p => p.owner == _harmony.Id && p.PatchMethod == Installed[i].Patch))
                {
                    InteropFixesPlugin.ModLogger.LogWarning("VNEI lookup hook remains after removal; native lookup fallback is retained.");
                    continue;
                }
                Installed.RemoveAt(i);
            }
            catch (Exception error)
            {
                InteropFixesPlugin.ModLogger.LogWarning("Could not remove a VNEI lookup hook: " + error.Message);
            }
        }
        if (Installed.Count != 0) return; // Residual resolver still needs the original lookup delegate.
        _harmony = null;
        RecipeLookupPatch.Clear();
        HoveredItemLookup.Clear();
    }
}
