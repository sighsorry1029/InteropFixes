using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace InteropFixes;

[BepInPlugin(ModGuid, ModName, ModVersion)]
[BepInDependency(MagicPluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class InteropFixesPlugin : BaseUnityPlugin
{
    internal const string ModGuid = "sighsorry.InteropFixes";
    internal const string ModName = "InteropFixes";
    internal const string ModVersion = "1.0.3";
    internal const string MagicPluginGuid = "blacks7ar.MagicPlugin";

    private Harmony? _harmony;

    internal static ManualLogSource ModLogger { get; private set; } = null!;

    private void Awake()
    {
        ModLogger = Logger;
        _harmony = new Harmony(ModGuid);

        try
        {
            CharacterLifecycleCompatibility.TryInstall(_harmony);
        }
        catch (System.Exception ex)
        {
            ModLogger.LogError($"Character lifecycle compatibility initialization failed safely: {ex}");
        }

        try
        {
            MagicPluginCompatibility.TryInstall(_harmony);
        }
        catch (System.Exception ex)
        {
            ModLogger.LogError($"MagicPlugin compatibility initialization failed safely: {ex}");
        }
    }

    private void Update()
    {
        CreatureRemovalTracker.Update();
    }

    private void OnDestroy()
    {
        CreatureRemovalTracker.Clear();
        if (_harmony == null)
        {
            return;
        }

        try
        {
            _harmony.UnpatchSelf();
            MagicPluginCompatibility.RestoreExternalPatchesAndReset();
            CharacterLifecycleCompatibility.ResetInstallationState();
        }
        catch (System.Exception ex)
        {
            ModLogger.LogError($"InteropFixes shutdown could not fully restore its patch state: {ex}");
        }
        finally
        {
            _harmony = null;
        }
    }
}
