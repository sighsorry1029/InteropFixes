using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace InteropFixes;

internal static class MagicPluginCosmeticZdoPrevention
{
    private const string FlameStaffName = "BMP_FlameStaff";
    private const string ThunderStaffName = "BMP_ThunderStaff";
    private const string ArcticStaffName = "BMP_ArcticStaff";
    private const string IceScepterName = "BMP_IceScepter";
    private const string FlameProjectileName = "bmp_flamewand_projectile";
    private const string ThunderProjectileName = "bmp_lightningwand_projectile";
    private const string ArcticProjectileName = "BMP_ArcticStaff_Ice_Projectile";
    private const string ArcticIceblockerName = "BMP_ArcticStaff_Iceblocker";
    private const string IceScepterNovaAoeName = "BMP_DvergerStaffNova_aoe";
    private const string FlameLaunchEffectName = "BMP_Fx_FlameStaff_Launch";
    private const string ThunderLaunchEffectName = "BMP_Fx_ThunderStaff_Launch";
    private const string ArcticLaunchEffectName = "BMP_Fx_ArcticStaff_Iceshard_Launch";
    private const string ThunderExplosionEffectName = "bmp_lightningwand_explosion";
    private const string ArcticHitEffectName = "BMP_Vfx_ArcticStaff_Ice_Hit";
    private const string ColdHitSoundName = "BMP_Sfx_Dragon_Coldball_Explode";
    private const string ArcticDestroyedEffectName = "BMP_Vfx_ArcticStaff_Iceblocker_Destroyed";
    private const string ArcticDestroyedSoundName = "BMP_Sfx_ArcticStaff_Ice_Destroyed";
    private const string IceScepterNovaRingName = "bmp_fx_DvergerMage_Nova_ring";
    private const float LaunchEffectLifetime = 8f;
    private const float ArcticHitEffectLifetime = 8f;
    private const float ColdHitSoundLifetime = 3f;
    private const float ArcticDestroyedEffectLifetime = 5f;
    private const float ArcticDestroyedSoundLifetime = 4f;
    private const float MaximumExplicitCosmeticLifetime = 30f;
    private const float NovaSuppressionTokenLifetime = 3f;
    private const string CosmeticEventRpc = "sighsorry.InteropFixes.MagicCosmeticEvent.v1";
    private const int CosmeticEventPacketSize = 52;

    private static readonly System.Version MinimumCosmeticPatchVersion = new(2, 1, 9);
    private static readonly int FlameProjectileHash = FlameProjectileName.GetStableHashCode();
    private static readonly int ThunderProjectileHash = ThunderProjectileName.GetStableHashCode();
    private static readonly int ArcticProjectileHash = ArcticProjectileName.GetStableHashCode();
    private static readonly int ArcticIceblockerHash = ArcticIceblockerName.GetStableHashCode();
    private static readonly int IceScepterNovaAoeHash = IceScepterNovaAoeName.GetStableHashCode();
    // Persisted/network identity from Blasted. Keep the original key when moving modules.
    private static readonly int CosmeticCarrierMarkerHash =
        "BlastedSwimmingTarredBugFix_MagicCosmeticCarrier".GetStableHashCode();

    private static bool _cosmeticZdoPatchInstalled;
    private static bool _reportedCosmeticRuntimeError;
    private static ZNetScene? _probedCosmeticScene;
    private static CosmeticFeature _cosmeticFeatures;
    private static ItemDrop.ItemData.SharedData? _flameStaffSharedData;
    private static ItemDrop.ItemData.SharedData? _thunderStaffSharedData;
    private static ItemDrop.ItemData.SharedData? _arcticStaffSharedData;
    private static ItemDrop.ItemData.SharedData? _iceScepterSharedData;
    private static EffectList? _flameLaunchEffects;
    private static EffectList? _thunderLaunchEffects;
    private static EffectList? _arcticLaunchEffects;
    private static EffectList? _iceScepterNovaEffects;
    private static GameObject? _flameLaunchEffectPrefab;
    private static GameObject? _thunderLaunchEffectPrefab;
    private static GameObject? _arcticLaunchEffectPrefab;
    private static GameObject? _thunderExplosionEffectPrefab;
    private static GameObject? _arcticHitEffectPrefab;
    private static GameObject? _coldHitSoundPrefab;
    private static GameObject? _arcticDestroyedEffectPrefab;
    private static GameObject? _arcticDestroyedSoundPrefab;
    private static GameObject? _iceScepterNovaRingPrefab;
    private static float _iceScepterNovaRingLifetime;
    private static readonly Dictionary<Character, float> PendingNovaSuppressions = new();
    private static readonly CosmeticReplayHistory ReplayHistory = new(4096);
    private static ZRoutedRpc? _registeredRpc;
    private static object? _registeredHandler;
    private static FieldInfo? _rpcFunctions;

    [ThreadStatic]
    private static CosmeticFeature _projectileHitContext;

    [ThreadStatic]
    private static ZNetView? _projectileHitView;

    [ThreadStatic]
    private static bool _insideCosmeticProjectileBurst;

    [ThreadStatic]
    private static CosmeticCarrierKind _suppressedLaunchMarker;

    private static int GetStableHashCode(this string value)
    {
        unchecked
        {
            int hash1 = 5381;
            int hash2 = hash1;
            for (int i = 0; i < value.Length && value[i] != '\0'; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ value[i];
                if (i == value.Length - 1 || value[i + 1] == '\0')
                {
                    break;
                }

                hash2 = ((hash2 << 5) + hash2) ^ value[i + 1];
            }

            return hash1 + hash2 * 1566083941;
        }
    }

    internal static void Reset()
    {
        _cosmeticZdoPatchInstalled = false;
        DetachEventReceiver();
        _reportedCosmeticRuntimeError = false;
        _probedCosmeticScene = null;
        ClearCosmeticFeatureState();
        _projectileHitContext = CosmeticFeature.None;
        _projectileHitView = null;
        _insideCosmeticProjectileBurst = false;
        _suppressedLaunchMarker = CosmeticCarrierKind.None;
    }

    internal static void TryInstall(Harmony harmony, string version)
    {
        if (_cosmeticZdoPatchInstalled)
        {
            return;
        }

        if (!TryParseVersion(version, out System.Version parsedVersion) ||
            parsedVersion.CompareTo(MinimumCosmeticPatchVersion) < 0)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin cosmetic peer-ZDO prevention requires MagicPlugin {MinimumCosmeticPatchVersion} or later; " +
                $"MagicPlugin {version} keeps its original cosmetic ZDO behavior.");
            return;
        }

        MethodInfo? sceneAwake = AccessTools.DeclaredMethod(
            typeof(ZNetScene),
            "Awake",
            Type.EmptyTypes);
        MethodInfo? fireProjectileBurst = AccessTools.DeclaredMethod(
            typeof(Attack),
            "FireProjectileBurst",
            Type.EmptyTypes);
        MethodInfo? effectListCreate = AccessTools.DeclaredMethod(
            typeof(EffectList),
            nameof(EffectList.Create),
            new[] { typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(float), typeof(int), typeof(ZDOID) });
        ConstructorInfo? routedRpcConstructor = AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
        MethodInfo? sceneCreateObject = AccessTools.DeclaredMethod(
            typeof(ZNetScene),
            "CreateObject",
            new[] { typeof(ZDO) });
        MethodInfo? projectileOnHit = AccessTools.DeclaredMethod(
            typeof(Projectile),
            nameof(Projectile.OnHit),
            new[] { typeof(Collider), typeof(Vector3), typeof(bool), typeof(Vector3) });
        MethodInfo? projectileSetup = AccessTools.DeclaredMethod(
            typeof(Projectile),
            nameof(Projectile.Setup),
            new[]
            {
                typeof(Character),
                typeof(Vector3),
                typeof(float),
                typeof(HitData),
                typeof(ItemDrop.ItemData),
                typeof(ItemDrop.ItemData)
            });
        MethodInfo? aoeSetup = AccessTools.DeclaredMethod(
            typeof(Aoe),
            nameof(Aoe.Setup),
            new[]
            {
                typeof(Character),
                typeof(Vector3),
                typeof(float),
                typeof(HitData),
                typeof(ItemDrop.ItemData),
                typeof(ItemDrop.ItemData)
            });
        FieldInfo? forceDisableInit = AccessTools.Field(typeof(ZNetView), nameof(ZNetView.m_forceDisableInit));
        FieldInfo? projectileNView = AccessTools.Field(typeof(Projectile), "m_nview");
        FieldInfo? aoeNView = AccessTools.Field(typeof(Aoe), "m_nview");
        FieldInfo? rpcFunctions = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");

        if (sceneAwake?.ReturnType != typeof(void) ||
            fireProjectileBurst?.ReturnType != typeof(void) ||
            effectListCreate?.ReturnType != typeof(GameObject[]) ||
            routedRpcConstructor == null ||
            sceneCreateObject?.ReturnType != typeof(GameObject) ||
            projectileOnHit?.ReturnType != typeof(void) ||
            projectileSetup?.ReturnType != typeof(void) ||
            aoeSetup?.ReturnType != typeof(void) ||
            forceDisableInit == null ||
            !forceDisableInit.IsStatic ||
            forceDisableInit.FieldType != typeof(bool) ||
            projectileNView?.FieldType != typeof(ZNetView) ||
            aoeNView?.FieldType != typeof(ZNetView) ||
            rpcFunctions == null || !typeof(IDictionary).IsAssignableFrom(rpcFunctions.FieldType) ||
            !rpcFunctions.FieldType.IsGenericType ||
            rpcFunctions.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>) ||
            rpcFunctions.FieldType.GetGenericArguments()[0] != typeof(int) ||
            rpcFunctions.FieldType.GetGenericArguments()[1].FullName != "RoutedMethodBase")
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin cosmetic peer-ZDO prevention skipped: the expected Valheim effect, scene, or projectile structure was not found.");
            return;
        }

        try
        {
            _rpcFunctions = rpcFunctions;
            harmony.Patch(routedRpcConstructor,
                postfix: new HarmonyMethod(typeof(MagicPluginCosmeticZdoPrevention), nameof(RoutedRpcCreatedPostfix)));
            harmony.Patch(
                sceneAwake,
                prefix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProbeZNetSceneAwakePrefix))
                { priority = Priority.First },
                postfix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProbeZNetSceneAwakePostfix))
                {
                    priority = Priority.Last,
                    after = new[]
                    {
                        "org.bepinex.helpers.ItemManager",
                        "com.jotunn.jotunn",
                        InteropFixesPlugin.MagicPluginGuid
                    }
                });
            harmony.Patch(
                effectListCreate,
                prefix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticEffectListCreatePrefix))
                { priority = Priority.Last },
                postfix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticEffectListCreatePostfix))
                { priority = Priority.First },
                finalizer: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticEffectListCreateCleanup))
                { priority = Priority.First });
            harmony.Patch(
                sceneCreateObject,
                postfix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticNetworkObjectCreatePostfix))
                { priority = Priority.Last });
            harmony.Patch(
                projectileOnHit,
                prefix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProjectileHitContextPrefix))
                { priority = Priority.First },
                finalizer: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProjectileHitContextFinalizer))
                { priority = Priority.Last });
            harmony.Patch(
                projectileSetup,
                postfix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProjectileSetupPostfix))
                { priority = Priority.Last });
            harmony.Patch(
                aoeSetup,
                postfix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticAoeSetupPostfix))
                { priority = Priority.Last });
            harmony.Patch(
                fireProjectileBurst,
                prefix: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProjectileBurstContextPrefix))
                { priority = Priority.First },
                finalizer: new HarmonyMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    nameof(CosmeticProjectileBurstContextFinalizer))
                { priority = Priority.Last });

            _cosmeticZdoPatchInstalled = true;
            if (ZRoutedRpc.instance != null) AttachEventReceiver(ZRoutedRpc.instance);
            InteropFixesPlugin.ModLogger.LogInfo(
                $"Installed structurally gated MagicPlugin cosmetic peer-ZDO prevention for MagicPlugin {version}.");

            ZNetScene? scene = ZNetScene.instance;
            if (scene)
            {
                TryProbeCosmeticFeatures(scene);
            }
        }
        catch (Exception ex)
        {
            _cosmeticZdoPatchInstalled = false;
            DetachEventReceiver();
            _probedCosmeticScene = null;
            ClearCosmeticFeatureState();
            TryUnpatchCosmeticBurstContext(harmony, fireProjectileBurst);
            TryUnpatchCosmeticMethods(
                harmony,
                sceneAwake,
                effectListCreate,
                routedRpcConstructor,
                sceneCreateObject,
                projectileOnHit,
                projectileSetup,
                aoeSetup);
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin cosmetic peer-ZDO prevention skipped after a patching failure: {ex.Message}");
        }
    }

    private static bool TryParseVersion(string value, out System.Version version)
    {
        int suffixIndex = value.IndexOfAny(new[] { '-', '+' });
        string normalized = suffixIndex >= 0 ? value.Substring(0, suffixIndex) : value;
        if (System.Version.TryParse(normalized, out System.Version? parsedVersion))
        {
            version = parsedVersion;
            return true;
        }

        version = null!;
        return false;
    }

    private static void CosmeticProbeZNetSceneAwakePrefix()
    {
        _probedCosmeticScene = null;
        ClearCosmeticFeatureState();
    }

    private static void CosmeticProbeZNetSceneAwakePostfix(ZNetScene __instance)
    {
        TryProbeCosmeticFeatures(__instance);
    }

    private static void TryProbeCosmeticFeatures(ZNetScene scene)
    {
        if (!_cosmeticZdoPatchInstalled || !scene || ReferenceEquals(_probedCosmeticScene, scene))
        {
            return;
        }

        _probedCosmeticScene = scene;
        ClearCosmeticFeatureState();

        CosmeticFeature enabled = CosmeticFeature.None;
        if (ProbeCosmeticFeature(
                CosmeticFeature.FlameLaunch,
                () => TryProbeLaunchFeature(
                    scene,
                    FlameStaffName,
                    FlameProjectileName,
                    FlameProjectileHash,
                    FlameLaunchEffectName,
                    ref _flameStaffSharedData,
                    ref _flameLaunchEffects,
                    ref _flameLaunchEffectPrefab)))
        {
            enabled |= CosmeticFeature.FlameLaunch;
        }

        if (ProbeCosmeticFeature(
                CosmeticFeature.ThunderLaunch,
                () => TryProbeLaunchFeature(
                    scene,
                    ThunderStaffName,
                    ThunderProjectileName,
                    ThunderProjectileHash,
                    ThunderLaunchEffectName,
                    ref _thunderStaffSharedData,
                    ref _thunderLaunchEffects,
                    ref _thunderLaunchEffectPrefab)))
        {
            enabled |= CosmeticFeature.ThunderLaunch;
        }

        if (ProbeCosmeticFeature(
                CosmeticFeature.ThunderHit,
                () => TryProbeThunderHitFeature(scene)))
        {
            enabled |= CosmeticFeature.ThunderHit;
        }

        if (ProbeCosmeticFeature(
                CosmeticFeature.ArcticLaunch,
                () => TryProbeLaunchFeature(
                    scene,
                    ArcticStaffName,
                    ArcticProjectileName,
                    ArcticProjectileHash,
                    ArcticLaunchEffectName,
                    ref _arcticStaffSharedData,
                    ref _arcticLaunchEffects,
                    ref _arcticLaunchEffectPrefab)))
        {
            enabled |= CosmeticFeature.ArcticLaunch;
        }

        if (ProbeCosmeticFeature(
                CosmeticFeature.ArcticHit,
                () => TryProbeArcticHitFeature(scene)))
        {
            enabled |= CosmeticFeature.ArcticHit;
        }

        if (ProbeCosmeticFeature(
                CosmeticFeature.ArcticIceblockDestroyed,
                () => TryProbeArcticIceblockDestroyedFeature(scene)))
        {
            enabled |= CosmeticFeature.ArcticIceblockDestroyed;
        }

        if (ProbeCosmeticFeature(
                CosmeticFeature.IceScepterNovaRing,
                () => TryProbeIceScepterNovaFeature(scene)))
        {
            enabled |= CosmeticFeature.IceScepterNovaRing;
        }

        _cosmeticFeatures = enabled;
        PublishLocalCapabilities();
        if (enabled != CosmeticFeature.None)
        {
            InteropFixesPlugin.ModLogger.LogInfo(
                $"MagicPlugin cosmetic structural probe enabled: {FormatCosmeticFeatures(enabled)}.");
        }

        CosmeticFeature disabled = CosmeticFeature.All & ~enabled;
        if (disabled != CosmeticFeature.None)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin cosmetic structural probe kept original behavior for: {FormatCosmeticFeatures(disabled)}.");
        }
    }

    private static bool ProbeCosmeticFeature(CosmeticFeature feature, Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin cosmetic structural probe rejected {FormatCosmeticFeatures(feature)}: {ex.Message}");
            return false;
        }
    }

    private static void ClearCosmeticFeatureState()
    {
        _cosmeticFeatures = CosmeticFeature.None;
        CosmeticNetworkCoordinator.SetLocalCapabilities(0);
        _flameStaffSharedData = null;
        _thunderStaffSharedData = null;
        _arcticStaffSharedData = null;
        _iceScepterSharedData = null;
        _flameLaunchEffects = null;
        _thunderLaunchEffects = null;
        _arcticLaunchEffects = null;
        _iceScepterNovaEffects = null;
        _flameLaunchEffectPrefab = null;
        _thunderLaunchEffectPrefab = null;
        _arcticLaunchEffectPrefab = null;
        _thunderExplosionEffectPrefab = null;
        _arcticHitEffectPrefab = null;
        _coldHitSoundPrefab = null;
        _arcticDestroyedEffectPrefab = null;
        _arcticDestroyedSoundPrefab = null;
        _iceScepterNovaRingPrefab = null;
        _iceScepterNovaRingLifetime = 0f;
        PendingNovaSuppressions.Clear();
        ReplayHistory.Clear();
    }

    private static bool TryProbeLaunchFeature(
        ZNetScene scene,
        string itemName,
        string projectileName,
        int projectileHash,
        string effectName,
        ref ItemDrop.ItemData.SharedData? cachedSharedData,
        ref EffectList? cachedEffects,
        ref GameObject? cachedEffectPrefab)
    {
        if (!TryResolveAttackGraph(
                scene,
                itemName,
                secondaryAttack: false,
                projectileName,
                projectileHash,
                out ItemDrop.ItemData.SharedData sharedData,
                out Attack attack,
                out GameObject projectilePrefab) ||
            !TryGetSafeNetworkProjectile(projectilePrefab, out _) ||
            attack.m_projectileBursts <= 0)
        {
            return false;
        }

        GameObject? effectPrefab = TryResolveExactScenePrefab(scene, effectName);
        if (!effectPrefab ||
            !IsExactSingleEffectList(
                attack.m_burstEffect,
                effectPrefab,
                effectName,
                expectedVariant: 0) ||
            !IsSafeNetworkCosmetic(
                effectPrefab,
                effectName,
                LaunchEffectLifetime,
                requireAudio: false))
        {
            return false;
        }

        cachedSharedData = sharedData;
        cachedEffects = attack.m_burstEffect;
        cachedEffectPrefab = effectPrefab;
        return true;
    }

    private static bool TryProbeThunderHitFeature(ZNetScene scene)
    {
        GameObject? projectilePrefab = TryResolveExactScenePrefab(
            scene,
            ThunderProjectileName,
            ThunderProjectileHash);
        GameObject? explosionPrefab = TryResolveExactScenePrefab(scene, ThunderExplosionEffectName);
        GameObject? soundPrefab = TryResolveExactScenePrefab(scene, ColdHitSoundName);
        if (!TryGetSafeNetworkProjectile(projectilePrefab, out Projectile projectile) ||
            !explosionPrefab ||
            !soundPrefab ||
            !IsExactPairEffectList(
                projectile.m_hitEffects,
                explosionPrefab,
                ThunderExplosionEffectName,
                soundPrefab,
                ColdHitSoundName) ||
            !IsSafeLocalOnlyCosmetic(explosionPrefab, ThunderExplosionEffectName) ||
            !IsSafeNetworkCosmetic(
                soundPrefab,
                ColdHitSoundName,
                ColdHitSoundLifetime,
                requireAudio: true))
        {
            return false;
        }

        _thunderExplosionEffectPrefab = explosionPrefab;
        _coldHitSoundPrefab = soundPrefab;
        return true;
    }

    private static bool TryProbeArcticHitFeature(ZNetScene scene)
    {
        GameObject? projectilePrefab = TryResolveExactScenePrefab(
            scene,
            ArcticProjectileName,
            ArcticProjectileHash);
        GameObject? hitEffectPrefab = TryResolveExactScenePrefab(scene, ArcticHitEffectName);
        GameObject? soundPrefab = TryResolveExactScenePrefab(scene, ColdHitSoundName);
        if (!TryGetSafeNetworkProjectile(projectilePrefab, out Projectile projectile) ||
            !hitEffectPrefab ||
            !soundPrefab ||
            !IsExactPairEffectList(
                projectile.m_hitEffects,
                hitEffectPrefab,
                ArcticHitEffectName,
                soundPrefab,
                ColdHitSoundName) ||
            !IsSafeNetworkCosmetic(
                hitEffectPrefab,
                ArcticHitEffectName,
                ArcticHitEffectLifetime,
                requireAudio: false) ||
            !IsSafeNetworkCosmetic(
                soundPrefab,
                ColdHitSoundName,
                ColdHitSoundLifetime,
                requireAudio: true))
        {
            return false;
        }

        _arcticHitEffectPrefab = hitEffectPrefab;
        _coldHitSoundPrefab = soundPrefab;
        return true;
    }

    private static bool TryProbeArcticIceblockDestroyedFeature(ZNetScene scene)
    {
        GameObject? iceblockerPrefab = TryResolveExactScenePrefab(
            scene,
            ArcticIceblockerName,
            ArcticIceblockerHash);
        GameObject? effectPrefab = TryResolveExactScenePrefab(scene, ArcticDestroyedEffectName);
        GameObject? soundPrefab = TryResolveExactScenePrefab(scene, ArcticDestroyedSoundName);
        if (!TryGetSafePersistentDestructible(iceblockerPrefab, out Destructible destructible) ||
            !effectPrefab ||
            !soundPrefab ||
            !IsExactPairEffectList(
                destructible.m_destroyedEffect,
                effectPrefab,
                ArcticDestroyedEffectName,
                soundPrefab,
                ArcticDestroyedSoundName) ||
            !IsSafeNetworkCosmetic(
                effectPrefab,
                ArcticDestroyedEffectName,
                ArcticDestroyedEffectLifetime,
                requireAudio: false) ||
            !IsSafeNetworkCosmetic(
                soundPrefab,
                ArcticDestroyedSoundName,
                ArcticDestroyedSoundLifetime,
                requireAudio: true))
        {
            return false;
        }

        _arcticDestroyedEffectPrefab = effectPrefab;
        _arcticDestroyedSoundPrefab = soundPrefab;
        return true;
    }

    private static bool TryProbeIceScepterNovaFeature(ZNetScene scene)
    {
        if (!TryResolveAttackGraph(
                scene,
                IceScepterName,
                secondaryAttack: true,
                IceScepterNovaAoeName,
                IceScepterNovaAoeHash,
                out ItemDrop.ItemData.SharedData sharedData,
                out Attack attack,
                out GameObject aoePrefab) ||
            attack.m_projectileBursts != 1 ||
            !TryGetSafeNetworkAoe(aoePrefab, out Aoe aoe))
        {
            return false;
        }

        GameObject? ringPrefab = TryResolveExactScenePrefab(scene, IceScepterNovaRingName);
        if (!ringPrefab ||
            !IsExactSingleEffectList(
                attack.m_startEffect,
                ringPrefab,
                IceScepterNovaRingName,
                expectedVariant: 0,
                expectedInheritParentRotation: true) ||
            !IsSafeExplicitLifetimeCosmetic(ringPrefab, IceScepterNovaRingName))
        {
            return false;
        }

        _iceScepterSharedData = sharedData;
        _iceScepterNovaEffects = attack.m_startEffect;
        _iceScepterNovaRingPrefab = ringPrefab;
        _iceScepterNovaRingLifetime = aoe.m_ttl;
        return true;
    }

    private static bool TryResolveAttackGraph(
        ZNetScene scene,
        string itemName,
        bool secondaryAttack,
        string projectileName,
        int projectileHash,
        out ItemDrop.ItemData.SharedData sharedData,
        out Attack attack,
        out GameObject projectilePrefab)
    {
        sharedData = null!;
        attack = null!;
        projectilePrefab = null!;

        GameObject? itemPrefab = TryResolveExactScenePrefab(scene, itemName);
        GameObject? candidateProjectile = TryResolveExactScenePrefab(scene, projectileName, projectileHash);
        ItemDrop? itemDrop = itemPrefab ? itemPrefab.GetComponent<ItemDrop>() : null;
        ItemDrop.ItemData.SharedData? candidateSharedData = itemDrop?.m_itemData?.m_shared;
        Attack? candidateAttack = secondaryAttack
            ? candidateSharedData?.m_secondaryAttack
            : candidateSharedData?.m_attack;
        if (!itemPrefab ||
            !candidateProjectile ||
            itemDrop == null ||
            candidateSharedData == null ||
            candidateAttack == null ||
            candidateAttack.m_attackType != Attack.AttackType.Projectile ||
            candidateAttack.m_attackProjectile != candidateProjectile ||
            candidateAttack.m_projectiles != 1 ||
            !string.IsNullOrEmpty(candidateSharedData.m_ammoType))
        {
            return false;
        }

        sharedData = candidateSharedData;
        attack = candidateAttack;
        projectilePrefab = candidateProjectile;
        return true;
    }

    private static GameObject? TryResolveExactScenePrefab(
        ZNetScene scene,
        string expectedName,
        int? expectedHash = null)
    {
        GameObject? prefab = scene.GetPrefab(expectedName);
        if (!prefab ||
            !string.Equals(prefab.name, expectedName, StringComparison.Ordinal) ||
            (expectedHash.HasValue && scene.GetPrefab(expectedHash.Value) != prefab))
        {
            return null;
        }

        return prefab;
    }

    private static bool TryGetSafeNetworkProjectile(
        GameObject? prefab,
        out Projectile projectile)
    {
        projectile = null!;
        if (!prefab || !TryGetSafeNetworkCarrier(prefab))
        {
            return false;
        }

        Projectile[] projectiles = prefab.GetComponentsInChildren<Projectile>(includeInactive: true);
        if (projectiles.Length != 1 ||
            projectiles[0].gameObject != prefab ||
            !IsOnlyProjectileImplementation(prefab, projectiles[0]))
        {
            return false;
        }

        projectile = projectiles[0];
        return true;
    }

    private static bool TryGetSafeNetworkAoe(GameObject? prefab, out Aoe aoe)
    {
        aoe = null!;
        if (!prefab || !TryGetSafeNetworkCarrier(prefab))
        {
            return false;
        }

        Aoe[] aoes = prefab.GetComponentsInChildren<Aoe>(includeInactive: true);
        if (aoes.Length != 1 ||
            aoes[0].gameObject != prefab ||
            !IsOnlyProjectileImplementation(prefab, aoes[0]) ||
            float.IsNaN(aoes[0].m_ttl) ||
            float.IsInfinity(aoes[0].m_ttl) ||
            aoes[0].m_ttl <= 0f ||
            aoes[0].m_ttl > MaximumExplicitCosmeticLifetime ||
            aoes[0].m_ttlMax != 0f ||
            aoes[0].m_attachToCaster ||
            !IsEmptyEffectList(aoes[0].m_hitEffects) ||
            !IsEmptyEffectList(aoes[0].m_initiateEffect))
        {
            return false;
        }

        aoe = aoes[0];
        return true;
    }

    private static bool IsOnlyProjectileImplementation(
        GameObject prefab,
        Component expectedImplementation)
    {
        MonoBehaviour[] behaviours =
            prefab.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
        int implementationCount = 0;
        for (int i = 0; i < behaviours.Length; i++)
        {
            if (behaviours[i] is not IProjectile implementation)
            {
                continue;
            }

            implementationCount++;
            if (!ReferenceEquals(implementation, expectedImplementation))
            {
                return false;
            }
        }

        return implementationCount == 1;
    }

    private static bool TryGetSafePersistentDestructible(
        GameObject? prefab,
        out Destructible destructible)
    {
        destructible = null!;
        if (!prefab)
        {
            return false;
        }

        ZNetView[] nviews = prefab.GetComponentsInChildren<ZNetView>(includeInactive: true);
        Destructible[] destructibles = prefab.GetComponentsInChildren<Destructible>(includeInactive: true);
        if (nviews.Length != 1 ||
            nviews[0].gameObject != prefab ||
            !nviews[0].m_persistent ||
            nviews[0].m_distant ||
            destructibles.Length != 1 ||
            destructibles[0].gameObject != prefab)
        {
            return false;
        }

        destructible = destructibles[0];
        return true;
    }

    private static bool TryGetSafeNetworkCarrier(GameObject prefab)
    {
        ZNetView[] nviews = prefab.GetComponentsInChildren<ZNetView>(includeInactive: true);
        ZSyncTransform[] syncTransforms = prefab.GetComponentsInChildren<ZSyncTransform>(includeInactive: true);
        return nviews.Length == 1 &&
               nviews[0].gameObject == prefab &&
               !nviews[0].m_persistent &&
               !nviews[0].m_distant &&
               syncTransforms.Length == 1 &&
               syncTransforms[0].gameObject == prefab;
    }

    private static bool IsExactSingleEffectList(
        EffectList? effects,
        GameObject expectedPrefab,
        string expectedName,
        int expectedVariant,
        bool expectedInheritParentRotation = false)
    {
        EffectList.EffectData[]? entries = effects?.m_effectPrefabs;
        return entries?.Length == 1 &&
               entries[0].m_prefab == expectedPrefab &&
               IsExactEffectEntry(
                   entries[0],
                   expectedName,
                   expectedVariant,
                   requestedVariant: expectedVariant,
                   expectedInheritParentRotation);
    }

    private static bool IsExactPairEffectList(
        EffectList? effects,
        GameObject firstPrefab,
        string firstName,
        GameObject secondPrefab,
        string secondName)
    {
        EffectList.EffectData[]? entries = effects?.m_effectPrefabs;
        return entries?.Length == 2 &&
               IsExactEffectEntry(entries[0], firstName, expectedVariant: -1, requestedVariant: -1) &&
               IsExactEffectEntry(entries[1], secondName, expectedVariant: -1, requestedVariant: -1) &&
               entries[0].m_prefab == firstPrefab &&
               entries[1].m_prefab == secondPrefab;
    }

    private static bool IsEmptyEffectList(EffectList? effects)
    {
        return effects?.m_effectPrefabs == null || effects.m_effectPrefabs.Length == 0;
    }

    private static bool IsSafeLocalOnlyCosmetic(GameObject? prefab, string expectedName)
    {
        if (!prefab ||
            !string.Equals(prefab.name, expectedName, StringComparison.Ordinal) ||
            !HasOnlySafeCosmeticComponents(
                prefab,
                allowSyncTransform: false,
                allowAnimator: true) ||
            prefab.GetComponentsInChildren<ZNetView>(includeInactive: true).Length != 0)
        {
            return false;
        }

        return HasComponentType(prefab, "UnityEngine.ParticleSystem") ||
               HasComponentType(prefab, "UnityEngine.AudioSource");
    }

    private static bool HasOnlySafeCosmeticComponents(
        GameObject prefab,
        bool allowSyncTransform,
        bool allowAnimator)
    {
        Component[] components = prefab.GetComponentsInChildren<Component>(includeInactive: true);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (!component)
            {
                return false;
            }

            Type type = component.GetType();
            if (type == typeof(Transform) ||
                type == typeof(ZNetView) ||
                type == typeof(TimedDestruction) ||
                type == typeof(ZSFX) ||
                type == typeof(CamShaker) ||
                type == typeof(LightFlicker) ||
                (allowSyncTransform && type == typeof(ZSyncTransform)))
            {
                continue;
            }

            string? fullName = type.FullName;
            if ((allowAnimator &&
                 string.Equals(fullName, "UnityEngine.Animator", StringComparison.Ordinal)) ||
                string.Equals(fullName, "UnityEngine.ParticleSystem", StringComparison.Ordinal) ||
                string.Equals(fullName, "UnityEngine.ParticleSystemRenderer", StringComparison.Ordinal) ||
                string.Equals(fullName, "UnityEngine.AudioSource", StringComparison.Ordinal) ||
                string.Equals(fullName, "UnityEngine.Light", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool HasCosmeticFeature(CosmeticFeature feature)
    {
        return _cosmeticZdoPatchInstalled &&
               ReferenceEquals(_probedCosmeticScene, ZNetScene.instance) &&
               (_cosmeticFeatures & feature) == feature;
    }

    // Prepared receivers remain active when a setting/peer changes. Only a new
    // decision to omit a network effect needs the current session agreement.
    private static bool CanOptimizeFeature(CosmeticFeature feature)
    {
        return HasCosmeticFeature(feature) && CosmeticNetworkCoordinator.CanOptimize((int)feature);
    }

    private static bool EventReceiverReady => _registeredHandler != null &&
        ReferenceEquals(_registeredRpc, ZRoutedRpc.instance);

    private static void PublishLocalCapabilities()
    {
        CosmeticFeature mask = _cosmeticFeatures;
        if (!EventReceiverReady)
            mask &= ~(CosmeticFeature.ThunderHit | CosmeticFeature.ArcticHit | CosmeticFeature.ArcticIceblockDestroyed);
        CosmeticNetworkCoordinator.SetLocalCapabilities((int)mask);
    }

    private static void RoutedRpcCreatedPostfix(ZRoutedRpc __instance)
    {
        if (_cosmeticZdoPatchInstalled) AttachEventReceiver(__instance);
    }

    private static void AttachEventReceiver(ZRoutedRpc rpc)
    {
        if (ReferenceEquals(_registeredRpc, rpc) && _registeredHandler != null) return;
        DetachEventReceiver();
        try
        {
            if (_rpcFunctions == null) return;
            IDictionary functions = (IDictionary)_rpcFunctions.GetValue(rpc)!;
            int hash = CosmeticEventRpc.GetStableHashCode();
            if (functions.Contains(hash))
            {
                ReportCosmeticRuntimeError("registering cosmetic events", new InvalidOperationException("the RPC identifier is already owned"));
                return;
            }
            rpc.Register<ZPackage>(CosmeticEventRpc, ReceiveCosmeticEvent);
            _registeredRpc = rpc;
            _registeredHandler = functions[hash];
        }
        catch (Exception ex) { ReportCosmeticRuntimeError("registering cosmetic events", ex); }
        finally { PublishLocalCapabilities(); }
    }

    private static void DetachEventReceiver()
    {
        try
        {
            if (_registeredRpc != null && _registeredHandler != null && _rpcFunctions != null)
            {
                IDictionary functions = (IDictionary)_rpcFunctions.GetValue(_registeredRpc)!;
                int hash = CosmeticEventRpc.GetStableHashCode();
                if (ReferenceEquals(functions[hash], _registeredHandler))
                    functions.Remove(hash);
            }
        }
        finally { _registeredRpc = null; _registeredHandler = null; }
    }

    private static bool TryCaptureCosmeticEvent(ZNetView? view, CosmeticFeature feature,
        Vector3 position, Quaternion rotation, out CosmeticEvent? replay)
    {
        replay = null;
        long session = CosmeticNetworkCoordinator.SessionToken;
        if (session == 0 || !EventReceiverReady || !CanOptimizeFeature(feature) ||
            !view || !view.IsValid() || !view.IsOwner()) return false;
        ZDO zdo = view.GetZDO();
        if (zdo.GetPrefab() != EventPrefabHash(feature) || !IsValidCosmeticEvent((int)feature, zdo.m_uid, position, rotation))
            return false;
        replay = new CosmeticEvent(session, zdo.m_uid, feature, position, rotation);
        return true;
    }

    private static int EventPrefabHash(CosmeticFeature feature) => feature switch
    {
        CosmeticFeature.ThunderHit => ThunderProjectileHash,
        CosmeticFeature.ArcticHit => ArcticProjectileHash,
        CosmeticFeature.ArcticIceblockDestroyed => ArcticIceblockerHash,
        _ => 0
    };

    private static bool IsValidCosmeticEvent(int feature, ZDOID source, Vector3 position, Quaternion rotation)
    {
        bool bounded(float value, float limit) => !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= limit;
        if (source == ZDOID.None || EventPrefabHash((CosmeticFeature)feature) == 0 ||
            !bounded(position.x, 1000000f) || !bounded(position.y, 1000000f) || !bounded(position.z, 1000000f) ||
            !bounded(rotation.x, 1.01f) || !bounded(rotation.y, 1.01f) || !bounded(rotation.z, 1.01f) || !bounded(rotation.w, 1.01f))
            return false;
        float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        return length > 0.9f && length < 1.1f;
    }

    private static ZPackage EncodeCosmeticEvent(CosmeticEvent replay)
    {
        ZPackage package = new();
        package.Write(replay.Session);
        package.Write(replay.Source);
        package.Write((int)replay.Feature);
        package.Write(replay.Position);
        package.Write(replay.Rotation);
        return package;
    }

    private static bool TryDecodeCosmeticEvent(ZPackage package, long session, out CosmeticEvent replay)
    {
        replay = default;
        if (session == 0 || package.Size() != CosmeticEventPacketSize) return false;
        long receivedSession = package.ReadLong();
        ZDOID source = package.ReadZDOID();
        int feature = package.ReadInt();
        Vector3 position = package.ReadVector3();
        Quaternion rotation = package.ReadQuaternion();
        if (receivedSession != session || !IsValidCosmeticEvent(feature, source, position, rotation)) return false;
        replay = new CosmeticEvent(session, source, (CosmeticFeature)feature, position, rotation);
        return true;
    }

    private static void BroadcastCosmeticEvent(CosmeticEvent replay)
    {
        // This is a committed omission: setting/permit changes must not cancel its
        // notification. A new world/session must never receive the previous event.
        if (!EventReceiverReady || replay.Session != CosmeticNetworkCoordinator.SessionToken) return;
        _registeredRpc!.InvokeRoutedRPC(ZRoutedRpc.Everybody, CosmeticEventRpc, EncodeCosmeticEvent(replay));
    }

    private static void ReceiveCosmeticEvent(long sender, ZPackage package)
    {
        if (!_cosmeticZdoPatchInstalled || !EventReceiverReady || IsDedicatedServer() ||
            ZNet.instance == null || ZNetScene.instance == null || sender == ZNet.GetUID() ||
            !CosmeticNetworkCoordinator.IsParticipant(sender)) return;
        try
        {
            if (!TryDecodeCosmeticEvent(package, CosmeticNetworkCoordinator.SessionToken, out CosmeticEvent replay) ||
                !HasCosmeticFeature(replay.Feature) ||
                ZNetScene.instance.OutsideActiveArea(replay.Position)) return;
            ZDO? source = ZDOMan.instance?.GetZDO(replay.Source);
            if (source != null && (source.GetOwner() != sender || source.GetPrefab() != EventPrefabHash(replay.Feature))) return;
            if (!ReplayHistory.TryRecord(replay.Session, replay.Source, (int)replay.Feature)) return;

            // No current server toggle/permit check: a received event proves that
            // its owner already omitted this network effect in the current session.
            if (replay.Feature == CosmeticFeature.ThunderHit)
                SpawnLocalCosmetic(_coldHitSoundPrefab, ColdHitSoundName, replay.Position, replay.Rotation);
            else if (replay.Feature == CosmeticFeature.ArcticHit)
            {
                SpawnLocalCosmetic(_arcticHitEffectPrefab, ArcticHitEffectName, replay.Position, replay.Rotation);
                SpawnLocalCosmetic(_coldHitSoundPrefab, ColdHitSoundName, replay.Position, replay.Rotation);
            }
            else if (replay.Feature == CosmeticFeature.ArcticIceblockDestroyed)
            {
                SpawnLocalCosmetic(_arcticDestroyedEffectPrefab, ArcticDestroyedEffectName, replay.Position, replay.Rotation);
                SpawnLocalCosmetic(_arcticDestroyedSoundPrefab, ArcticDestroyedSoundName, replay.Position, replay.Rotation);
            }
        }
        catch (Exception ex) { ReportCosmeticRuntimeError("receiving a committed cosmetic event", ex); }
    }

    private static string FormatCosmeticFeatures(CosmeticFeature features)
    {
        var names = new List<string>();
        foreach (CosmeticFeature feature in new[]
                 {
                     CosmeticFeature.FlameLaunch,
                     CosmeticFeature.ThunderLaunch,
                     CosmeticFeature.ThunderHit,
                     CosmeticFeature.ArcticLaunch,
                     CosmeticFeature.ArcticHit,
                     CosmeticFeature.ArcticIceblockDestroyed,
                     CosmeticFeature.IceScepterNovaRing
                 })
        {
            if ((features & feature) != 0)
            {
                names.Add(feature.ToString());
            }
        }

        return string.Join(", ", names);
    }

    private static void CosmeticProjectileSetupPostfix(
        ZNetView ___m_nview,
        ItemDrop.ItemData item)
    {
        if (!_cosmeticZdoPatchInstalled ||
            !_insideCosmeticProjectileBurst ||
            _suppressedLaunchMarker == CosmeticCarrierKind.None ||
            item?.m_shared == null ||
            !___m_nview ||
            !___m_nview.IsValid() ||
            !___m_nview.IsOwner())
        {
            return;
        }

        try
        {
            ZDO zdo = ___m_nview.GetZDO();
            int prefabHash = zdo.GetPrefab();
            CosmeticCarrierKind marker = _suppressedLaunchMarker;
            bool matchesSuppressedLaunch =
                (marker == CosmeticCarrierKind.FlameStaffLaunch &&
                 prefabHash == FlameProjectileHash &&
                 ReferenceEquals(item.m_shared, _flameStaffSharedData)) ||
                (marker == CosmeticCarrierKind.ThunderStaffLaunch &&
                 prefabHash == ThunderProjectileHash &&
                 ReferenceEquals(item.m_shared, _thunderStaffSharedData)) ||
                (marker == CosmeticCarrierKind.ArcticStaffLaunch &&
                 prefabHash == ArcticProjectileHash &&
                 ReferenceEquals(item.m_shared, _arcticStaffSharedData));
            if (matchesSuppressedLaunch)
            {
                _suppressedLaunchMarker = CosmeticCarrierKind.None;
                zdo.Set(CosmeticCarrierMarkerHash, (int)marker);
            }
        }
        catch (Exception ex)
        {
            ReportCosmeticRuntimeError("marking a staff projectile", ex);
        }
    }

    private static void CosmeticAoeSetupPostfix(
        Aoe __instance,
        ZNetView ___m_nview,
        Character owner,
        ItemDrop.ItemData item)
    {
        if (!_cosmeticZdoPatchInstalled ||
            !HasCosmeticFeature(CosmeticFeature.IceScepterNovaRing) ||
            item?.m_shared == null ||
            !ReferenceEquals(item.m_shared, _iceScepterSharedData) ||
            !ReferenceEquals(item.m_shared.m_secondaryAttack?.m_startEffect, _iceScepterNovaEffects) ||
            !IsExactSingleEffectList(
                item.m_shared.m_secondaryAttack!.m_startEffect,
                _iceScepterNovaRingPrefab!,
                IceScepterNovaRingName,
                expectedVariant: 0,
                expectedInheritParentRotation: true) ||
            !__instance ||
            !___m_nview ||
            !___m_nview.IsValid() ||
            !___m_nview.IsOwner())
        {
            return;
        }

        try
        {
            ZDO zdo = ___m_nview.GetZDO();
            if (zdo.GetPrefab() == IceScepterNovaAoeHash &&
                TryConsumeNovaSuppression(owner))
            {
                zdo.Set(
                    CosmeticCarrierMarkerHash,
                    (int)CosmeticCarrierKind.IceScepterNovaRing);
            }
        }
        catch (Exception ex)
        {
            ReportCosmeticRuntimeError("marking an Ice Scepter Nova", ex);
        }
    }

    private static void CosmeticProjectileBurstContextPrefix(
        Attack __instance,
        ItemDrop.ItemData ___m_weapon,
        out CosmeticProjectileBurstState __state)
    {
        __state = new CosmeticProjectileBurstState(
            _insideCosmeticProjectileBurst,
            _suppressedLaunchMarker);
        ItemDrop.ItemData.SharedData? sharedData = ___m_weapon?.m_shared;
        _insideCosmeticProjectileBurst =
            (CanOptimizeFeature(CosmeticFeature.FlameLaunch) &&
             ReferenceEquals(sharedData, _flameStaffSharedData) &&
             ReferenceEquals(__instance.m_burstEffect, _flameLaunchEffects)) ||
            (CanOptimizeFeature(CosmeticFeature.ThunderLaunch) &&
             ReferenceEquals(sharedData, _thunderStaffSharedData) &&
             ReferenceEquals(__instance.m_burstEffect, _thunderLaunchEffects)) ||
            (CanOptimizeFeature(CosmeticFeature.ArcticLaunch) &&
             ReferenceEquals(sharedData, _arcticStaffSharedData) &&
             ReferenceEquals(__instance.m_burstEffect, _arcticLaunchEffects));
        _suppressedLaunchMarker = CosmeticCarrierKind.None;
    }

    private static void CosmeticProjectileBurstContextFinalizer(
        CosmeticProjectileBurstState __state)
    {
        _insideCosmeticProjectileBurst = __state.WasInside;
        _suppressedLaunchMarker = __state.PreviousMarker;
    }

    private static void CosmeticEffectListCreatePrefix(
        EffectList __instance,
        Vector3 basePos,
        Quaternion baseRot,
        Transform baseParent,
        int variant,
        ZDOID gamepadEffectsExclusiveToPlayer,
        out CosmeticEffectState __state)
    {
        __state = default;
        // Valheim 1.0.7 and 1.0.12 write the recipient into the effect ZDO after instantiation.
        // Suppressing that ZDO would lose its player-specific gamepad effect policy.
        if (gamepadEffectsExclusiveToPlayer != ZDOID.None)
        {
            return;
        }

        if (!_cosmeticZdoPatchInstalled ||
            _cosmeticFeatures == CosmeticFeature.None ||
            ZNetView.m_forceDisableInit ||
            ZNetView.m_useInitZDO ||
            ZNetView.m_initZDO != null ||
            ZNet.instance == null ||
            ZNetScene.instance == null)
        {
            return;
        }

        try
        {
            LocalizedEffectKind kind = ClassifyLocalCosmeticEffectList(
                __instance,
                baseParent,
                variant,
                basePos,
                baseRot,
                out CosmeticEvent? replay);
            if (kind == LocalizedEffectKind.None)
            {
                return;
            }

            ZNetView.m_forceDisableInit = true;
            __state = new CosmeticEffectState(kind, replay);
        }
        catch (Exception ex)
        {
            ReportCosmeticRuntimeError("classifying an effect list", ex);
        }
    }

    private static void CosmeticEffectListCreatePostfix(
        CosmeticEffectState __state,
        GameObject[]? __result)
    {
        CosmeticEffectListCreateCleanup(__state);
        if (__result == null || __result.Length == 0)
        {
            return;
        }

        try
        {
            if (__state.Replay.HasValue) BroadcastCosmeticEvent(__state.Replay.Value);
            if (__state.Kind != LocalizedEffectKind.ExplicitLifetime) return;
            for (int i = 0; i < __result.Length; i++)
            {
                GameObject spawned = __result[i];
                if (spawned)
                {
                    UnityEngine.Object.Destroy(spawned, _iceScepterNovaRingLifetime);
                }
            }
        }
        catch (Exception ex)
        {
            ReportCosmeticRuntimeError("finalizing a localized MagicPlugin effect", ex);
        }
    }

    private static void CosmeticEffectListCreateCleanup(CosmeticEffectState __state)
    {
        if (__state.Kind != LocalizedEffectKind.None)
        {
            ZNetView.m_forceDisableInit = false;
        }
    }

    private static void CosmeticProjectileHitContextPrefix(
        Projectile __instance,
        ZNetView ___m_nview,
        out CosmeticProjectileHitState __state)
    {
        __state = new CosmeticProjectileHitState(
            _projectileHitContext,
            _projectileHitView);
        _projectileHitContext = CosmeticFeature.None;
        _projectileHitView = null;
        if (!_cosmeticZdoPatchInstalled ||
            // Piercing projectiles can hit repeatedly. Keep their original network
            // effects rather than treating the carrier as a single hit event.
            __instance.m_onlyStopOnTerrain ||
            !___m_nview ||
            !___m_nview.IsValid() ||
            !___m_nview.IsOwner())
        {
            return;
        }

        try
        {
            int prefabHash = ___m_nview.GetZDO().GetPrefab();
            if (prefabHash == ThunderProjectileHash &&
                CanOptimizeFeature(CosmeticFeature.ThunderHit) &&
                IsExactPairEffectList(
                    __instance.m_hitEffects,
                    _thunderExplosionEffectPrefab!,
                    ThunderExplosionEffectName,
                    _coldHitSoundPrefab!,
                    ColdHitSoundName))
            {
                _projectileHitContext = CosmeticFeature.ThunderHit;
            }
            else if (prefabHash == ArcticProjectileHash &&
                     CanOptimizeFeature(CosmeticFeature.ArcticHit) &&
                     IsExactPairEffectList(
                         __instance.m_hitEffects,
                         _arcticHitEffectPrefab!,
                         ArcticHitEffectName,
                         _coldHitSoundPrefab!,
                         ColdHitSoundName))
            {
                _projectileHitContext = CosmeticFeature.ArcticHit;
            }
            if (_projectileHitContext != CosmeticFeature.None) _projectileHitView = ___m_nview;
        }
        catch (Exception ex)
        {
            ReportCosmeticRuntimeError("entering a MagicPlugin projectile hit", ex);
        }
    }

    private static void CosmeticProjectileHitContextFinalizer(CosmeticProjectileHitState __state)
    {
        _projectileHitContext = __state.PreviousContext;
        _projectileHitView = __state.PreviousView;
    }

    private static void CosmeticNetworkObjectCreatePostfix(ZDO zdo, GameObject? __result)
    {
        if (!_cosmeticZdoPatchInstalled || !__result || IsDedicatedServer())
        {
            return;
        }

        try
        {
            int prefabHash = zdo.GetPrefab();
            CosmeticCarrierKind marker =
                (CosmeticCarrierKind)zdo.GetInt(CosmeticCarrierMarkerHash, 0);
            if (marker == CosmeticCarrierKind.None)
            {
                return;
            }

            ZNetView? nview = __result.GetComponent<ZNetView>();
            if (!nview || !nview.IsValid() || nview.IsOwner())
            {
                return;
            }

            if (marker == CosmeticCarrierKind.FlameStaffLaunch &&
                prefabHash == FlameProjectileHash &&
                HasCosmeticFeature(CosmeticFeature.FlameLaunch) &&
                ReplayHistory.TryRecord(CosmeticNetworkCoordinator.SessionToken, zdo.m_uid, (int)CosmeticFeature.FlameLaunch))
            {
                SpawnLocalCosmetic(
                    _flameLaunchEffectPrefab,
                    FlameLaunchEffectName,
                    __result.transform.position,
                    __result.transform.rotation);
            }
            else if (marker == CosmeticCarrierKind.ThunderStaffLaunch &&
                     prefabHash == ThunderProjectileHash &&
                     HasCosmeticFeature(CosmeticFeature.ThunderLaunch) &&
                     ReplayHistory.TryRecord(CosmeticNetworkCoordinator.SessionToken, zdo.m_uid, (int)CosmeticFeature.ThunderLaunch))
            {
                SpawnLocalCosmetic(
                    _thunderLaunchEffectPrefab,
                    ThunderLaunchEffectName,
                    __result.transform.position,
                    __result.transform.rotation);
            }
            else if (marker == CosmeticCarrierKind.ArcticStaffLaunch &&
                     prefabHash == ArcticProjectileHash &&
                     HasCosmeticFeature(CosmeticFeature.ArcticLaunch) &&
                     ReplayHistory.TryRecord(CosmeticNetworkCoordinator.SessionToken, zdo.m_uid, (int)CosmeticFeature.ArcticLaunch))
            {
                SpawnLocalCosmetic(
                    _arcticLaunchEffectPrefab,
                    ArcticLaunchEffectName,
                    __result.transform.position,
                    __result.transform.rotation);
            }
            else if (marker == CosmeticCarrierKind.IceScepterNovaRing &&
                     prefabHash == IceScepterNovaAoeHash &&
                     HasCosmeticFeature(CosmeticFeature.IceScepterNovaRing) &&
                     TryFindPlayerOwnedBy(zdo.GetOwner(), out Player caster) &&
                     ReplayHistory.TryRecord(CosmeticNetworkCoordinator.SessionToken, zdo.m_uid, (int)CosmeticFeature.IceScepterNovaRing))
            {
                SpawnLocalCosmetic(
                    _iceScepterNovaRingPrefab,
                    IceScepterNovaRingName,
                    caster.transform.position,
                    caster.transform.rotation,
                    _iceScepterNovaRingLifetime);
            }
        }
        catch (Exception ex)
        {
            ReportCosmeticRuntimeError("replaying a remote MagicPlugin effect", ex);
        }
    }

    private static LocalizedEffectKind ClassifyLocalCosmeticEffectList(
        EffectList effects,
        Transform? baseParent,
        int variant,
        Vector3 position,
        Quaternion rotation,
        out CosmeticEvent? replay)
    {
        replay = null;
        EffectList.EffectData[]? entries = effects?.m_effectPrefabs;
        if (entries == null)
        {
            return LocalizedEffectKind.None;
        }

        if (_insideCosmeticProjectileBurst &&
            CanOptimizeFeature(CosmeticFeature.FlameLaunch) &&
            ReferenceEquals(effects, _flameLaunchEffects) &&
            entries.Length == 1 &&
            IsExactEffectEntry(entries[0], FlameLaunchEffectName, expectedVariant: 0, variant) &&
            entries[0].m_prefab == _flameLaunchEffectPrefab)
        {
            _suppressedLaunchMarker = CosmeticCarrierKind.FlameStaffLaunch;
            return LocalizedEffectKind.DefaultLifetime;
        }

        if (_insideCosmeticProjectileBurst &&
            CanOptimizeFeature(CosmeticFeature.ThunderLaunch) &&
            ReferenceEquals(effects, _thunderLaunchEffects) &&
            entries.Length == 1 &&
            IsExactEffectEntry(entries[0], ThunderLaunchEffectName, expectedVariant: 0, variant) &&
            entries[0].m_prefab == _thunderLaunchEffectPrefab)
        {
            _suppressedLaunchMarker = CosmeticCarrierKind.ThunderStaffLaunch;
            return LocalizedEffectKind.DefaultLifetime;
        }

        if (_insideCosmeticProjectileBurst &&
            CanOptimizeFeature(CosmeticFeature.ArcticLaunch) &&
            ReferenceEquals(effects, _arcticLaunchEffects) &&
            entries.Length == 1 &&
            IsExactEffectEntry(entries[0], ArcticLaunchEffectName, expectedVariant: 0, variant) &&
            entries[0].m_prefab == _arcticLaunchEffectPrefab)
        {
            _suppressedLaunchMarker = CosmeticCarrierKind.ArcticStaffLaunch;
            return LocalizedEffectKind.DefaultLifetime;
        }

        if (_projectileHitContext == CosmeticFeature.ThunderHit &&
            HasCosmeticFeature(CosmeticFeature.ThunderHit) &&
            IsExactPairEffectList(
                effects,
                _thunderExplosionEffectPrefab!,
                ThunderExplosionEffectName,
                _coldHitSoundPrefab!,
                ColdHitSoundName) &&
            TryCaptureCosmeticEvent(_projectileHitView, CosmeticFeature.ThunderHit, position, rotation, out replay))
        {
            return LocalizedEffectKind.DefaultLifetime;
        }

        if (_projectileHitContext == CosmeticFeature.ArcticHit &&
            HasCosmeticFeature(CosmeticFeature.ArcticHit) &&
            IsExactPairEffectList(
                effects,
                _arcticHitEffectPrefab!,
                ArcticHitEffectName,
                _coldHitSoundPrefab!,
                ColdHitSoundName) &&
            TryCaptureCosmeticEvent(_projectileHitView, CosmeticFeature.ArcticHit, position, rotation, out replay))
        {
            return LocalizedEffectKind.DefaultLifetime;
        }

        if (HasCosmeticFeature(CosmeticFeature.ArcticIceblockDestroyed) &&
            IsExactPairEffectList(
                effects,
                _arcticDestroyedEffectPrefab!,
                ArcticDestroyedEffectName,
                _arcticDestroyedSoundPrefab!,
                ArcticDestroyedSoundName) &&
            TryCaptureCosmeticEvent(baseParent ? baseParent.GetComponent<ZNetView>() : null,
                CosmeticFeature.ArcticIceblockDestroyed, position, rotation, out replay))
        {
            return LocalizedEffectKind.DefaultLifetime;
        }

        if (CanOptimizeFeature(CosmeticFeature.IceScepterNovaRing) &&
            ReferenceEquals(effects, _iceScepterNovaEffects) &&
            entries.Length == 1 &&
            IsExactEffectEntry(
                entries[0],
                IceScepterNovaRingName,
                expectedVariant: 0,
                variant,
                expectedInheritParentRotation: true) &&
            entries[0].m_prefab == _iceScepterNovaRingPrefab &&
            TryRegisterNovaSuppression(baseParent))
        {
            return LocalizedEffectKind.ExplicitLifetime;
        }

        return LocalizedEffectKind.None;
    }

    private static bool IsExactEffectEntry(
        EffectList.EffectData? entry,
        string expectedName,
        int expectedVariant,
        int requestedVariant,
        bool expectedInheritParentRotation = false)
    {
        return entry != null &&
               entry.m_enabled &&
               entry.m_prefab &&
               string.Equals(entry.m_prefab.name, expectedName, StringComparison.Ordinal) &&
               entry.m_variant == expectedVariant &&
               (requestedVariant < 0 || expectedVariant < 0 || requestedVariant == expectedVariant) &&
               !entry.m_attach &&
               !entry.m_follow &&
               entry.m_inheritParentRotation == expectedInheritParentRotation &&
               !entry.m_inheritParentScale &&
               !entry.m_multiplyParentVisualScale &&
               !entry.m_randomRotation &&
               !entry.m_scale &&
               string.IsNullOrEmpty(entry.m_childTransform);
    }

    private static bool IsSafeNetworkCosmetic(
        GameObject? prefab,
        string expectedName,
        float expectedLifetime,
        bool requireAudio)
    {
        if (!prefab ||
            !string.Equals(prefab.name, expectedName, StringComparison.Ordinal) ||
            !HasOnlySafeCosmeticComponents(
                prefab,
                allowSyncTransform: false,
                allowAnimator: false))
        {
            return false;
        }

        ZNetView[] nviews = prefab.GetComponentsInChildren<ZNetView>(includeInactive: true);
        TimedDestruction? timedDestruction = prefab.GetComponent<TimedDestruction>();
        if (nviews.Length != 1 ||
            nviews[0].gameObject != prefab ||
            nviews[0].m_persistent ||
            nviews[0].m_distant ||
            !timedDestruction ||
            !timedDestruction.m_triggerOnAwake ||
            float.IsNaN(timedDestruction.m_timeout) ||
            float.IsInfinity(timedDestruction.m_timeout) ||
            Mathf.Abs(timedDestruction.m_timeout - expectedLifetime) > 0.01f)
        {
            return false;
        }

        return HasComponentType(
            prefab,
            requireAudio ? "UnityEngine.AudioSource" : "UnityEngine.ParticleSystem");
    }

    private static bool IsSafeExplicitLifetimeCosmetic(GameObject? prefab, string expectedName)
    {
        if (!prefab ||
            !string.Equals(prefab.name, expectedName, StringComparison.Ordinal) ||
            !HasOnlySafeCosmeticComponents(
                prefab,
                allowSyncTransform: true,
                allowAnimator: false))
        {
            return false;
        }

        ZNetView[] nviews = prefab.GetComponentsInChildren<ZNetView>(includeInactive: true);
        ZSyncTransform[] syncTransforms = prefab.GetComponentsInChildren<ZSyncTransform>(includeInactive: true);
        if (nviews.Length != 1 ||
            nviews[0].gameObject != prefab ||
            nviews[0].m_persistent ||
            nviews[0].m_distant ||
            syncTransforms.Length != 1 ||
            syncTransforms[0].gameObject != prefab ||
            prefab.GetComponent<TimedDestruction>())
        {
            return false;
        }

        return HasComponentType(prefab, "UnityEngine.ParticleSystem") ||
               HasComponentType(prefab, "UnityEngine.AudioSource");
    }

    private static bool HasComponentType(GameObject prefab, string fullTypeName)
    {
        Component[] components = prefab.GetComponentsInChildren<Component>(includeInactive: true);
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component && string.Equals(component.GetType().FullName, fullTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void SpawnLocalCosmetic(
        GameObject? prefab,
        string expectedName,
        Vector3 position,
        Quaternion rotation,
        float? lifetime = null)
    {
        if (!prefab)
        {
            throw new InvalidOperationException(
                $"the validated {expectedName} prefab is unavailable");
        }

        bool previousForceDisableInit = ZNetView.m_forceDisableInit;
        try
        {
            ZNetView.m_forceDisableInit = true;
            GameObject spawned = UnityEngine.Object.Instantiate(prefab, position, rotation);
            if (lifetime.HasValue)
            {
                UnityEngine.Object.Destroy(spawned, lifetime.Value);
            }
        }
        finally
        {
            ZNetView.m_forceDisableInit = previousForceDisableInit;
        }
    }

    private static bool TryRegisterNovaSuppression(Transform? baseParent)
    {
        Player? caster = baseParent ? baseParent.GetComponentInParent<Player>() : null;
        if (!caster)
        {
            return false;
        }

        PendingNovaSuppressions[caster] = Time.time + NovaSuppressionTokenLifetime;
        return true;
    }

    private static bool TryConsumeNovaSuppression(Character? owner)
    {
        if (!owner || !PendingNovaSuppressions.TryGetValue(owner, out float expiresAt))
        {
            return false;
        }

        PendingNovaSuppressions.Remove(owner);
        return Time.time <= expiresAt;
    }

    private static bool TryFindPlayerOwnedBy(long owner, out Player player)
    {
        if (owner != 0L)
        {
            List<Player> players = Player.GetAllPlayers();
            for (int i = 0; i < players.Count; i++)
            {
                Player candidate = players[i];
                if (candidate && candidate.GetOwner() == owner)
                {
                    player = candidate;
                    return true;
                }
            }
        }

        player = null!;
        return false;
    }

    private static bool IsDedicatedServer()
    {
        ZNet? net = ZNet.instance;
        return net != null && net.IsDedicated();
    }

    private static void ReportCosmeticRuntimeError(string operation, Exception ex)
    {
        if (_reportedCosmeticRuntimeError)
        {
            return;
        }

        _reportedCosmeticRuntimeError = true;
        InteropFixesPlugin.ModLogger.LogWarning(
            $"MagicPlugin cosmetic peer-ZDO prevention failed safely while {operation}: {ex.Message}");
    }

    private static void TryUnpatchCosmeticMethods(
        Harmony harmony,
        params MethodBase[] originals)
    {
        for (int i = 0; i < originals.Length; i++)
        {
            try
            {
                harmony.Unpatch(originals[i], HarmonyPatchType.All, harmony.Id);
            }
            catch
            {
                // The plugin shutdown path verifies whether any owned patch remains.
            }
        }
    }

    private static void TryUnpatchCosmeticBurstContext(
        Harmony harmony,
        MethodBase original)
    {
        foreach (string patchName in new[]
                 {
                     nameof(CosmeticProjectileBurstContextFinalizer),
                     nameof(CosmeticProjectileBurstContextPrefix)
                 })
        {
            try
            {
                MethodInfo? patch = AccessTools.DeclaredMethod(
                    typeof(MagicPluginCosmeticZdoPrevention),
                    patchName);
                if (patch != null)
                {
                    harmony.Unpatch(original, patch);
                }
            }
            catch
            {
                // The plugin shutdown path verifies whether any owned patch remains.
            }
        }
    }

    private enum LocalizedEffectKind
    {
        None,
        DefaultLifetime,
        ExplicitLifetime
    }

    [Flags]
    private enum CosmeticFeature
    {
        None = 0,
        FlameLaunch = 1 << 0,
        ThunderLaunch = 1 << 1,
        ThunderHit = 1 << 2,
        ArcticLaunch = 1 << 3,
        ArcticHit = 1 << 4,
        ArcticIceblockDestroyed = 1 << 5,
        IceScepterNovaRing = 1 << 6,
        All = FlameLaunch |
              ThunderLaunch |
              ThunderHit |
              ArcticLaunch |
              ArcticHit |
              ArcticIceblockDestroyed |
              IceScepterNovaRing
    }

    [Flags]
    private enum CosmeticCarrierKind
    {
        None = 0,
        FlameStaffLaunch = 1 << 0,
        ThunderStaffLaunch = 1 << 1,
        ArcticStaffLaunch = 1 << 2,
        IceScepterNovaRing = 1 << 3,
        // Historical value only. An old creation-time marker is not evidence
        // that destruction actually suppressed network effects (gamepad paths
        // can deliberately retain them). New builds use committed events.
        ArcticIceblockDestroyed = 1 << 4
    }

    private readonly struct CosmeticEffectState
    {
        internal CosmeticEffectState(LocalizedEffectKind kind, CosmeticEvent? replay)
        {
            Kind = kind;
            Replay = replay;
        }
        internal LocalizedEffectKind Kind { get; }
        internal CosmeticEvent? Replay { get; }
    }

    private readonly struct CosmeticEvent
    {
        internal CosmeticEvent(long session, ZDOID source, CosmeticFeature feature, Vector3 position, Quaternion rotation)
        {
            Session = session;
            Source = source;
            Feature = feature;
            Position = position;
            Rotation = rotation;
        }
        internal long Session { get; }
        internal ZDOID Source { get; }
        internal CosmeticFeature Feature { get; }
        internal Vector3 Position { get; }
        internal Quaternion Rotation { get; }
    }

    private sealed class CosmeticReplayHistory
    {
        private readonly int _capacity;
        private readonly HashSet<(ZDOID Source, int Feature)> _seen = new();
        private readonly Queue<(ZDOID Source, int Feature)> _order = new();
        private long _session = long.MinValue;
        internal CosmeticReplayHistory(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }
        internal int Count => _seen.Count;
        internal bool TryRecord(long session, ZDOID source, int feature)
        {
            if (_session != session) { Clear(); _session = session; }
            var key = (source, feature);
            if (!_seen.Add(key)) return false;
            _order.Enqueue(key);
            if (_order.Count > _capacity) _seen.Remove(_order.Dequeue());
            return true;
        }
        internal void Clear() { _seen.Clear(); _order.Clear(); _session = long.MinValue; }
    }

    private readonly struct CosmeticProjectileBurstState
    {
        internal CosmeticProjectileBurstState(
            bool wasInside,
            CosmeticCarrierKind previousMarker)
        {
            WasInside = wasInside;
            PreviousMarker = previousMarker;
        }

        internal bool WasInside { get; }

        internal CosmeticCarrierKind PreviousMarker { get; }
    }

    private readonly struct CosmeticProjectileHitState
    {
        internal CosmeticProjectileHitState(
            CosmeticFeature previousContext,
            ZNetView? previousView)
        {
            PreviousContext = previousContext;
            PreviousView = previousView;
        }

        internal CosmeticFeature PreviousContext { get; }

        internal ZNetView? PreviousView { get; }
    }

}
