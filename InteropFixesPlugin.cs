using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using InteropFixes.Modules.Vnei;
using InteropFixes.Modules.EpicLootCraftyBoxes;
using InteropFixes.Modules.PathOfValheimanLookup;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteropFixes;

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInDependency(MagicPluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(VneiGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(PathOfValheimanGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(EpicLootGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(CraftyBoxesGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(BlastedGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(LegacyVneiGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(LegacyBridgeGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class InteropFixesPlugin : BaseUnityPlugin
{
    internal const string ModGuid = "sighsorry.InteropFixes";
    internal const string ModName = "InteropFixes";
    internal const string ModVersion = "1.0.3";
    internal const string MagicPluginGuid = "blacks7ar.MagicPlugin";
    internal const string VneiGuid = "com.maxsch.valheim.vnei";
    internal const string PathOfValheimanGuid = "treextr.pathofvalheiman";
    internal const string EpicLootGuid = "randyknapp.mods.epicloot";
    internal const string CraftyBoxesGuid = "Azumatt.AzuCraftyBoxes";
    internal const string BlastedGuid = "sighsorry.blastedswimmingtarredbugfix";
    internal const string LegacyVneiGuid = "sighsorry.VneiItemLookupFix";
    internal const string LegacyBridgeGuid = "sighsorry.EpicLootCraftyBoxesBridge";

    private static InteropFixesPlugin? _instance;
    private Harmony? _cosmeticNetworkHarmony;
    private Harmony? _magicHarmony;
    private Harmony? _vneiHarmony;
    private Harmony? _povLookupHarmony;
    private Harmony? _bridgeHarmony;
    private bool _stopping;
    internal static ManualLogSource ModLogger { get; private set; } = null!;

    // Automatic eligibility still ends before receiver teardown begins.
    internal static bool CosmeticRuntimeActive => _instance != null && !_instance._stopping;

    private void Awake()
    {
        ModLogger = Logger;
        _instance = this;
        // Install after dependency-ordered PoV Awake, before player/UI callbacks
        // can cache a missing optional type. Independent of the old VNEI item fix.
        TryInitialize("PoV / VNEI optional lookup", () =>
        {
            if (!ModuleIsolation.CanInstall("PoV / VNEI optional lookup", new[] { VneiGuid, PathOfValheimanGuid }, null)) return;
            _povLookupHarmony = new Harmony(ModGuid + ".PovOptionalLookup");
            if (!PathOfValheimanLookupModule.TryInstall(_povLookupHarmony)) StopPovLookup();
        }, StopPovLookup);
        TryInitialize("Cosmetic network coordination", () =>
        {
            _cosmeticNetworkHarmony = new Harmony(ModGuid + ".CosmeticNetwork");
            CosmeticNetworkCoordinator.Install(_cosmeticNetworkHarmony);
        }, StopCosmeticNetwork);
    }

    private IEnumerator Start()
    {
        // Soft dependencies order Awake; one frame also lets their normal Start hooks finish.
        // No background polling or per-frame integration scan is needed.
        yield return null;
        if (_stopping) yield break;
        bool headless = Application.isBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        TryInitialize("MagicPlugin", () =>
        {
            if (!ModuleIsolation.CanInstall("MagicPlugin", new[] { MagicPluginGuid }, null, true)) return;
            _magicHarmony = new Harmony(ModGuid + ".MagicPlugin");
            // Always prepare receive/render hooks. Capability agreement gates new
            // network suppression; a lost grant must not strand existing local effects.
            MagicPluginCompatibility.TryInstall(_magicHarmony, true);
        }, StopMagic);

        if (!headless)
        {
            TryInitialize("VNEI item lookup", () =>
            {
                if (!ModuleIsolation.CanInstall("VNEI item lookup", new[] { VneiGuid }, LegacyVneiGuid)) return;
                _vneiHarmony = new Harmony(ModGuid + ".VneiItemLookup");
                if (!VneiItemLookupModule.TryInstall(_vneiHarmony)) StopVnei();
            }, StopVnei);
            TryInitialize("EpicLoot / CraftyBoxes", () =>
            {
                if (!ModuleIsolation.CanInstall("EpicLoot / CraftyBoxes", new[] { EpicLootGuid, CraftyBoxesGuid }, LegacyBridgeGuid)) return;
                _bridgeHarmony = new Harmony(ModGuid + ".EpicLootCraftyBoxes");
                if (!EpicLootCraftyBoxesModule.TryInstall(_bridgeHarmony)) StopBridge();
            }, StopBridge);
        }
        ModLogger.LogInfo($"{ModName} {ModVersion}: optional integration initialization finished.");
    }

    private static void TryInitialize(string name, Action install, Action rollback)
    {
        try { install(); }
        catch (Exception ex)
        {
            ModLogger.LogError($"{name} initialization failed; rolling back only this integration: {ex}");
            try { rollback(); }
            catch (Exception rollbackError) { ModLogger.LogError($"{name} rollback failed: {rollbackError}"); }
        }
    }

    private void OnDestroy()
    {
        _stopping = true;
        // Revoke new optimization before removing the local render handlers.
        StopSafely("Cosmetic network coordination", StopCosmeticNetwork);
        // Each shutdown is independent even if another integration cannot remove a hook.
        StopSafely("EpicLoot / CraftyBoxes", StopBridge);
        StopSafely("VNEI item lookup", StopVnei);
        StopSafely("PoV / VNEI optional lookup", StopPovLookup);
        StopSafely("MagicPlugin", StopMagic);
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private static void StopSafely(string name, Action stop)
    {
        try { stop(); }
        catch (Exception ex) { ModLogger.LogError($"{name} shutdown failed: {ex}"); }
    }

    private void StopVnei()
    {
        if (_vneiHarmony == null) return;
        try { VneiItemLookupModule.Stop(); }
        finally { if (TryUnpatch(_vneiHarmony, "VNEI item lookup")) _vneiHarmony = null; }
    }

    private void StopPovLookup()
    {
        if (_povLookupHarmony == null) return;
        try { PathOfValheimanLookupModule.Stop(); }
        finally { if (TryUnpatch(_povLookupHarmony, "PoV / VNEI optional lookup")) _povLookupHarmony = null; }
    }

    private void StopBridge()
    {
        if (_bridgeHarmony == null) return;
        try { EpicLootCraftyBoxesModule.Stop(); }
        finally { if (TryUnpatch(_bridgeHarmony, "EpicLoot / CraftyBoxes")) _bridgeHarmony = null; }
    }

    private void StopMagic()
    {
        if (_magicHarmony == null) return;
        // Never restore external prefixes while our replacements might still run.
        if (!TryUnpatch(_magicHarmony, "MagicPlugin")) return;
        if (MagicPluginCompatibility.RestoreExternalPatchesAndReset()) _magicHarmony = null;
    }

    private void StopCosmeticNetwork()
    {
        if (_cosmeticNetworkHarmony == null) return;
        try { CosmeticNetworkCoordinator.Stop(); }
        finally { if (TryUnpatch(_cosmeticNetworkHarmony, "Cosmetic network coordination")) _cosmeticNetworkHarmony = null; }
    }

    internal static bool TryUnpatch(Harmony harmony, string capability) => ModuleIsolation.TryUnpatch(harmony, capability);
}
