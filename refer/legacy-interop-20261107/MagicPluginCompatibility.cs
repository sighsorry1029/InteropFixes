using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace InteropFixes;

internal static class MagicPluginCompatibility
{
    private const string PluginTypeName = "MagicPlugin.Plugin";
    private const string ConfigTypeName = "MagicPlugin.Functions.ConfigSetup";
    private const string AttackPatchTypeName = "MagicPlugin.Patches.AttackPatch";
    private const string PlayerPatchTypeName = "MagicPlugin.Patches.PlayerPatch";

    private static ProjectileConfiguration? _projectileConfiguration;
    private static bool _projectilePatchInstalled;
    private static bool _teleportPatchInstalled;
    private static bool _registryGuardInstalled;
    private static bool _reportedProjectileRuntimeError;
    private static bool _reportedTeleportRuntimeError;
    private static bool _reportedRegistryRuntimeError;
    private static PatchReplacement? _projectileReplacement;
    private static PatchReplacement? _teleportReplacement;
    private static AccessTools.FieldRef<Character, ZNetView>? _characterNViewAccessor;
    private static AccessTools.FieldRef<Tameable, MonsterAI>? _tameableMonsterAiAccessor;

    internal static void TryInstall(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(InteropFixesPlugin.MagicPluginGuid, out var pluginInfo))
        {
            InteropFixesPlugin.ModLogger.LogDebug(
                "MagicPlugin is not loaded; MagicPlugin-specific compatibility fixes are inactive.");
            return;
        }

        Assembly? magicAssembly = pluginInfo.Instance?.GetType().Assembly ?? FindAssemblyWithType(PluginTypeName);
        if (magicAssembly == null)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin compatibility skipped: the plugin assembly could not be resolved.");
            return;
        }

        string version = pluginInfo.Metadata.Version?.ToString() ?? "unknown";
        TryInstallCapability(
            "projectile compatibility",
            () => TryInstallProjectilePatch(harmony, magicAssembly, version));
        TryInstallCapability(
            "teleport compatibility",
            () => TryInstallTeleportPatch(harmony, magicAssembly, version));
        TryInstallCapability(
            "Character registry guard",
            () => TryInstallRegistryGuard(harmony, magicAssembly, version));
    }

    internal static void RestoreExternalPatchesAndReset()
    {
        RestoreExternalPatch(_projectileReplacement);
        RestoreExternalPatch(_teleportReplacement);

        _projectileConfiguration = null;
        _projectileReplacement = null;
        _teleportReplacement = null;
        _characterNViewAccessor = null;
        _tameableMonsterAiAccessor = null;
        _projectilePatchInstalled = false;
        _teleportPatchInstalled = false;
        _registryGuardInstalled = false;
        _reportedProjectileRuntimeError = false;
        _reportedTeleportRuntimeError = false;
        _reportedRegistryRuntimeError = false;
    }

    private static void TryInstallProjectilePatch(Harmony harmony, Assembly magicAssembly, string version)
    {
        if (_projectilePatchInstalled)
        {
            return;
        }

        MethodInfo? original = AccessTools.DeclaredMethod(typeof(Attack), nameof(Attack.FireProjectileBurst));
        Type? patchType = magicAssembly.GetType(AttackPatchTypeName, throwOnError: false);
        MethodInfo? magicPrefix = patchType?.GetMethod(
            "FireProjectileBurst_Prefix",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(Attack) },
            modifiers: null);
        if (original == null || magicPrefix == null || magicPrefix.ReturnType != typeof(bool))
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin projectile compatibility skipped: the expected FireProjectileBurst patch structure was not found.");
            return;
        }

        Patch? installedPrefix = FindOwnedPatch(original, HarmonyPatchType.Prefix, magicPrefix);
        if (installedPrefix == null)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin projectile compatibility skipped: the exact MagicPlugin FireProjectileBurst prefix is not installed.");
            return;
        }

        Type? pluginType = magicAssembly.GetType(PluginTypeName, throwOnError: false);
        Type? configType = magicAssembly.GetType(ConfigTypeName, throwOnError: false);
        if (!ProjectileConfiguration.TryCreate(pluginType, configType, out ProjectileConfiguration? configuration, out string missingField))
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin projectile compatibility skipped: required float config '{missingField}' was not found.");
            return;
        }

        MethodInfo? replacement = AccessTools.DeclaredMethod(
            typeof(MagicPluginCompatibility),
            nameof(FireProjectileBurstPrefix));
        string failure = "the InteropFixes replacement prefix was not found.";
        if (replacement == null ||
            !TryReplacePatch(harmony, original, installedPrefix, replacement, HarmonyPatchType.Prefix, out failure))
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin projectile compatibility skipped: {failure}");
            return;
        }

        _projectileConfiguration = configuration;
        _projectileReplacement = new PatchReplacement(
            original,
            installedPrefix,
            HarmonyPatchType.Prefix);
        _projectilePatchInstalled = true;
        InteropFixesPlugin.ModLogger.LogInfo(
            $"Installed MagicPlugin FireProjectileBurst compatibility for MagicPlugin {version}.");
    }

    private static void TryInstallTeleportPatch(Harmony harmony, Assembly magicAssembly, string version)
    {
        if (_teleportPatchInstalled)
        {
            return;
        }

        MethodInfo? original = AccessTools.DeclaredMethod(
            typeof(Player),
            nameof(Player.TeleportTo),
            new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) });
        Type? patchType = magicAssembly.GetType(PlayerPatchTypeName, throwOnError: false);
        MethodInfo? magicPostfix = patchType?.GetMethod(
            "TeleportTo_Postfix",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[]
            {
                typeof(Player),
                typeof(Vector3).MakeByRefType(),
                typeof(Quaternion).MakeByRefType()
            },
            modifiers: null);
        if (original == null || magicPostfix == null || magicPostfix.ReturnType != typeof(void))
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin teleport compatibility skipped: the expected Player.TeleportTo patch structure was not found.");
            return;
        }

        Patch? installedPostfix = FindOwnedPatch(original, HarmonyPatchType.Postfix, magicPostfix);
        if (installedPostfix == null)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin teleport compatibility skipped: the exact MagicPlugin TeleportTo postfix is not installed.");
            return;
        }

        AccessTools.FieldRef<Character, ZNetView> characterNViewAccessor;
        AccessTools.FieldRef<Tameable, MonsterAI> tameableMonsterAiAccessor;
        try
        {
            characterNViewAccessor =
                AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
            tameableMonsterAiAccessor =
                AccessTools.FieldRefAccess<Tameable, MonsterAI>("m_monsterAI");
        }
        catch (Exception ex)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin teleport compatibility skipped: required Valheim field accessors could not be created ({ex.Message}).");
            return;
        }

        MethodInfo? replacement = AccessTools.DeclaredMethod(
            typeof(MagicPluginCompatibility),
            nameof(TeleportToPostfix));
        string failure = "the InteropFixes replacement postfix was not found.";
        _characterNViewAccessor = characterNViewAccessor;
        _tameableMonsterAiAccessor = tameableMonsterAiAccessor;
        if (replacement == null ||
            !TryReplacePatch(harmony, original, installedPostfix, replacement, HarmonyPatchType.Postfix, out failure))
        {
            _characterNViewAccessor = null;
            _tameableMonsterAiAccessor = null;
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin teleport compatibility skipped: {failure}");
            return;
        }

        _teleportReplacement = new PatchReplacement(
            original,
            installedPostfix,
            HarmonyPatchType.Postfix);
        _teleportPatchInstalled = true;
        InteropFixesPlugin.ModLogger.LogInfo(
            $"Installed MagicPlugin safe teleport compatibility for MagicPlugin {version}.");
    }

    private static void TryInstallRegistryGuard(Harmony harmony, Assembly magicAssembly, string version)
    {
        if (_registryGuardInstalled)
        {
            return;
        }

        Type? pluginType = magicAssembly.GetType(PluginTypeName, throwOnError: false);
        MethodInfo? update = pluginType?.GetMethod(
            "Update",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        MethodInfo? prefix = AccessTools.DeclaredMethod(
            typeof(MagicPluginCompatibility),
            nameof(MagicPluginUpdatePrefix));
        if (update == null || update.ReturnType != typeof(void) || prefix == null)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "MagicPlugin Character registry guard skipped: MagicPlugin.Plugin.Update did not match the expected structure.");
            return;
        }

        try
        {
            harmony.Patch(update, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
            _registryGuardInstalled = true;
            InteropFixesPlugin.ModLogger.LogInfo(
                $"Installed MagicPlugin Character registry guard for MagicPlugin {version}.");
        }
        catch (Exception ex)
        {
            harmony.Unpatch(update, prefix);
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin Character registry guard skipped after a patching failure: {ex.Message}");
        }
    }

    private static bool FireProjectileBurstPrefix(
        Attack __instance,
        Humanoid ___m_character,
        ItemDrop.ItemData ___m_weapon)
    {
        try
        {
            ProjectileConfiguration? configuration = _projectileConfiguration;
            if (configuration != null)
            {
                ApplyFireProjectileBurstAdjustments(
                    __instance,
                    ___m_character,
                    ___m_weapon,
                    configuration);
            }
        }
        catch (Exception ex)
        {
            if (!_reportedProjectileRuntimeError)
            {
                _reportedProjectileRuntimeError = true;
                InteropFixesPlugin.ModLogger.LogWarning(
                    $"MagicPlugin projectile compatibility failed while applying an adjustment: {ex.Message}");
            }
        }

        return true;
    }

    private static void ApplyFireProjectileBurstAdjustments(
        Attack attack,
        Humanoid character,
        ItemDrop.ItemData weapon,
        ProjectileConfiguration configuration)
    {
        if (attack == null || character is not Player player)
        {
            return;
        }

        ItemDrop.ItemData.SharedData? shared = weapon?.m_shared;
        if (shared == null || shared.m_skillType != Skills.SkillType.ElementalMagic)
        {
            return;
        }

        string weaponName = shared.m_name ?? "";
        if (weaponName.EndsWith("scepter", StringComparison.Ordinal) ||
            weaponName.Contains("flamestaff") ||
            weaponName.Contains("thunderstaff") ||
            (shared.m_ammoType?.Contains("ammo_spells") ?? false))
        {
            return;
        }

        if (shared.m_secondaryAttack?.m_attackProjectile != null &&
            shared.m_secondaryAttack.m_attackProjectile.name == "BDS_DvergerStaffFire_clusterbomb_projectile")
        {
            return;
        }

        if (weaponName == "$bmp_arcticstaff" || weaponName == "$item_stafficeshards")
        {
            attack.m_projectileVel *= 1.2f;
            attack.m_projectileVelMin *= 1.2f;
            attack.m_projectileAccuracy *= 0f;
            attack.m_projectileAccuracyMin *= 0f;
        }
        else
        {
            attack.m_projectileVel *= configuration.MagicVelocity.Value;
            attack.m_projectileVelMin *= configuration.MagicVelocity.Value;
            attack.m_projectileAccuracy *= configuration.MagicAccuracy.Value;
            attack.m_projectileAccuracyMin *= configuration.MagicAccuracy.Value;
        }

        GameObject? projectilePrefab = attack.m_attackProjectile;
        if (!projectilePrefab)
        {
            return;
        }

        string projectileName = projectilePrefab.name ?? "";
        string lowerProjectileName = projectileName.ToLowerInvariant();
        float elementalSkillFactor = player.GetSkillFactor(Skills.SkillType.ElementalMagic);
        if (lowerProjectileName.Contains("surtlingstaff"))
        {
            ApplyProjectileAoe(projectilePrefab, configuration.SurtlingStaffAoe, elementalSkillFactor);
        }

        if (lowerProjectileName.Contains("eikthyrsstaff"))
        {
            ApplyProjectileAoe(projectilePrefab, configuration.EikthyrStaffAoe, elementalSkillFactor);
        }

        if (lowerProjectileName.Contains("lightningstaff"))
        {
            ApplyProjectileAoe(projectilePrefab, configuration.LightningStaffAoe, elementalSkillFactor);
        }

        if (lowerProjectileName.Contains("poisonstaff"))
        {
            ApplyProjectileAoe(projectilePrefab, configuration.PoisonStaffAoe, elementalSkillFactor);
        }

        if (lowerProjectileName.Contains("arcticstaff"))
        {
            ApplyProjectileAoe(projectilePrefab, configuration.ArcticStaffAoe, elementalSkillFactor);
        }

        if (projectileName == "bmp_modersheritage_projectile")
        {
            ApplyModersHeritage(projectilePrefab, elementalSkillFactor, configuration);
        }

        if (projectileName == "bmp_yagluthsheritage_projectile")
        {
            ApplyYagluthsHeritage(projectilePrefab, elementalSkillFactor, configuration);
        }
    }

    private static void ApplyProjectileAoe(
        GameObject projectilePrefab,
        ConfigEntry<float> aoeConfig,
        float elementalSkillFactor)
    {
        Projectile? projectile = projectilePrefab.GetComponent<Projectile>();
        if (projectile)
        {
            projectile.m_aoe = aoeConfig.Value + 2f * elementalSkillFactor;
        }
    }

    private static void ApplyModersHeritage(
        GameObject projectilePrefab,
        float elementalSkillFactor,
        ProjectileConfiguration configuration)
    {
        Projectile? iceblast = TryGetSpawnedProjectile(projectilePrefab);
        if (!iceblast)
        {
            return;
        }

        iceblast.m_aoe = configuration.ModersAoeRange.Value + 2f * elementalSkillFactor;
        iceblast.m_damage.m_chop = configuration.ModersChopDamage.Value;
        iceblast.m_damage.m_frost =
            configuration.ModersFrostDamage.Value * (1f + elementalSkillFactor);
        iceblast.m_damage.m_pickaxe = configuration.ModersPickaxeDamage.Value;
        iceblast.m_damage.m_pierce = configuration.ModersPierceDamage.Value;
    }

    private static void ApplyYagluthsHeritage(
        GameObject projectilePrefab,
        float elementalSkillFactor,
        ProjectileConfiguration configuration)
    {
        Projectile? meteors = TryGetSpawnedProjectile(projectilePrefab);
        if (!meteors)
        {
            return;
        }

        meteors.m_aoe = configuration.YagluthAoeRange.Value + 2f * elementalSkillFactor;
        meteors.m_damage.m_blunt = configuration.YagluthBluntDamage.Value;
        meteors.m_damage.m_chop = configuration.YagluthChopDamage.Value;
        meteors.m_damage.m_fire =
            configuration.YagluthFireDamage.Value * (1f + elementalSkillFactor);
        meteors.m_damage.m_pickaxe = configuration.YagluthPickaxeDamage.Value;
    }

    private static Projectile? TryGetSpawnedProjectile(GameObject projectilePrefab)
    {
        Projectile? projectile = projectilePrefab.GetComponent<Projectile>();
        GameObject? spawnOnHit = projectile?.m_spawnOnHit;
        SpawnAbility? spawnAbility = spawnOnHit?.GetComponent<SpawnAbility>();
        if (spawnAbility?.m_spawnPrefab == null || spawnAbility.m_spawnPrefab.Length == 0)
        {
            return null;
        }

        return spawnAbility.m_spawnPrefab[0]?.GetComponent<Projectile>();
    }

    private static void TeleportToPostfix(Player __instance, ref Vector3 pos, ref Quaternion rot)
    {
        try
        {
            TeleportFollowingMagicSummons(__instance, pos, rot);
        }
        catch (Exception ex)
        {
            if (!_reportedTeleportRuntimeError)
            {
                _reportedTeleportRuntimeError = true;
                InteropFixesPlugin.ModLogger.LogWarning(
                    $"MagicPlugin teleport compatibility failed while moving followed summons: {ex.Message}");
            }
        }
    }

    private static void TeleportFollowingMagicSummons(Player player, Vector3 position, Quaternion rotation)
    {
        if (!player)
        {
            return;
        }

        GameObject owner = player.gameObject;
        if (!owner)
        {
            return;
        }

        Vector3 summonPosition = position + player.transform.forward;
        List<Character> characters = Character.GetAllCharacters();
        for (int i = 0; i < characters.Count; i++)
        {
            Character character = characters[i];
            if (!IsFollowedMagicSummon(character, owner))
            {
                continue;
            }

            Transform summonTransform = character.transform;
            summonTransform.position = summonPosition;
            summonTransform.rotation = rotation;
        }
    }

    private static bool IsFollowedMagicSummon(Character character, GameObject owner)
    {
        if (!character || !character.gameObject)
        {
            return false;
        }

        AccessTools.FieldRef<Character, ZNetView>? characterNViewAccessor =
            _characterNViewAccessor;
        if (characterNViewAccessor == null)
        {
            return false;
        }

        ZNetView? nview = characterNViewAccessor(character);
        if (!nview || !nview.IsValid())
        {
            return false;
        }

        string name = character.gameObject.name ?? "";
        if (!name.StartsWith("BMP_", StringComparison.Ordinal) || !character.IsTamed())
        {
            return false;
        }

        Tameable? tameable = character.GetComponent<Tameable>();
        AccessTools.FieldRef<Tameable, MonsterAI>? tameableMonsterAiAccessor =
            _tameableMonsterAiAccessor;
        MonsterAI? monsterAi =
            tameable && tameableMonsterAiAccessor != null
                ? tameableMonsterAiAccessor(tameable)
                : null;
        GameObject? followTarget = monsterAi ? monsterAi.GetFollowTarget() : null;
        return followTarget == owner;
    }

    private static void MagicPluginUpdatePrefix()
    {
        try
        {
            List<Character> characters = Character.GetAllCharacters();
            for (int i = characters.Count - 1; i >= 0; i--)
            {
                Character character = characters[i];
                if (!ReferenceEquals(character, null) && character)
                {
                    continue;
                }

                characters.RemoveAt(i);
                if (!ReferenceEquals(character, null))
                {
                    TryRemoveCharacterHud(character);
                }
            }
        }
        catch (Exception ex)
        {
            ReportRegistryRuntimeError(ex);
        }
    }

    private static void TryRemoveCharacterHud(Character character)
    {
        try
        {
            if (EnemyHud.instance)
            {
                EnemyHud.instance.RemoveCharacterHud(character);
            }
        }
        catch (Exception ex)
        {
            ReportRegistryRuntimeError(ex);
        }
    }

    private static void ReportRegistryRuntimeError(Exception ex)
    {
        if (_reportedRegistryRuntimeError)
        {
            return;
        }

        _reportedRegistryRuntimeError = true;
        InteropFixesPlugin.ModLogger.LogWarning(
            $"MagicPlugin Character registry guard failed while pruning stale references: {ex.Message}");
    }

    private static Patch? FindOwnedPatch(
        MethodBase original,
        HarmonyPatchType patchType,
        MethodInfo expectedMethod)
    {
        Patches? patchInfo = Harmony.GetPatchInfo(original);
        if (patchInfo == null)
        {
            return null;
        }

        IEnumerable<Patch> patches = patchType == HarmonyPatchType.Prefix
            ? patchInfo.Prefixes
            : patchInfo.Postfixes;
        foreach (Patch patch in patches)
        {
            if (patch.owner == InteropFixesPlugin.MagicPluginGuid &&
                IsSameMethod(patch.PatchMethod, expectedMethod))
            {
                return patch;
            }
        }

        return null;
    }

    private static bool TryReplacePatch(
        Harmony harmony,
        MethodInfo original,
        Patch externalPatch,
        MethodInfo replacement,
        HarmonyPatchType patchType,
        out string failure)
    {
        bool externalRemoved = false;
        try
        {
            harmony.Unpatch(original, externalPatch.PatchMethod);
            externalRemoved = FindOwnedPatch(original, patchType, externalPatch.PatchMethod) == null;
            if (!externalRemoved)
            {
                failure = $"the exact external {patchType} could not be removed.";
                return false;
            }

            HarmonyMethod replacementPatch = new(replacement)
            {
                priority = externalPatch.priority,
                before = externalPatch.before,
                after = externalPatch.after
            };
            if (patchType == HarmonyPatchType.Prefix)
            {
                harmony.Patch(original, prefix: replacementPatch);
            }
            else
            {
                harmony.Patch(original, postfix: replacementPatch);
            }

            failure = "";
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                harmony.Unpatch(original, replacement);
            }
            catch
            {
                // Continue with restoration of the external patch.
            }

            if (externalRemoved)
            {
                TryRestoreExternalPatch(original, externalPatch, patchType);
            }

            failure = $"patch replacement failed ({ex.Message}).";
            return false;
        }
    }

    private static void TryRestoreExternalPatch(
        MethodInfo original,
        Patch externalPatch,
        HarmonyPatchType patchType)
    {
        try
        {
            HarmonyMethod restoredPatch = new(externalPatch.PatchMethod)
            {
                priority = externalPatch.priority,
                before = externalPatch.before,
                after = externalPatch.after
            };
            Harmony externalHarmony = new(externalPatch.owner);
            if (patchType == HarmonyPatchType.Prefix)
            {
                externalHarmony.Patch(original, prefix: restoredPatch);
            }
            else
            {
                externalHarmony.Patch(original, postfix: restoredPatch);
            }
        }
        catch (Exception restoreException)
        {
            InteropFixesPlugin.ModLogger.LogError(
                $"Failed to restore a MagicPlugin patch after compatibility installation failed: {restoreException}");
        }
    }

    private static void RestoreExternalPatch(PatchReplacement? replacement)
    {
        if (replacement == null)
        {
            return;
        }

        try
        {
            if (FindOwnedPatch(
                    replacement.Original,
                    replacement.PatchType,
                    replacement.ExternalPatch.PatchMethod) == null)
            {
                TryRestoreExternalPatch(
                    replacement.Original,
                    replacement.ExternalPatch,
                    replacement.PatchType);
            }
        }
        catch (Exception ex)
        {
            InteropFixesPlugin.ModLogger.LogError(
                $"Failed to restore a replaced MagicPlugin patch while InteropFixes was shutting down: {ex}");
        }
    }

    private static void TryInstallCapability(string capability, Action install)
    {
        try
        {
            install();
        }
        catch (Exception ex)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"MagicPlugin {capability} skipped after a structural probe failed: {ex.Message}");
        }
    }

    private static bool IsSameMethod(MethodInfo left, MethodInfo right)
    {
        if (ReferenceEquals(left, right) || left.Equals(right))
        {
            return true;
        }

        try
        {
            return left.Module.Equals(right.Module) && left.MetadataToken == right.MetadataToken;
        }
        catch
        {
            return false;
        }
    }

    private static Assembly? FindAssemblyWithType(string fullTypeName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetType(fullTypeName, throwOnError: false) != null)
            {
                return assembly;
            }
        }

        return null;
    }

    private sealed class ProjectileConfiguration
    {
        private ProjectileConfiguration(
            ConfigEntry<float> magicVelocity,
            ConfigEntry<float> magicAccuracy,
            ConfigEntry<float> surtlingStaffAoe,
            ConfigEntry<float> eikthyrStaffAoe,
            ConfigEntry<float> lightningStaffAoe,
            ConfigEntry<float> poisonStaffAoe,
            ConfigEntry<float> arcticStaffAoe,
            ConfigEntry<float> modersAoeRange,
            ConfigEntry<float> modersChopDamage,
            ConfigEntry<float> modersFrostDamage,
            ConfigEntry<float> modersPickaxeDamage,
            ConfigEntry<float> modersPierceDamage,
            ConfigEntry<float> yagluthAoeRange,
            ConfigEntry<float> yagluthBluntDamage,
            ConfigEntry<float> yagluthChopDamage,
            ConfigEntry<float> yagluthFireDamage,
            ConfigEntry<float> yagluthPickaxeDamage)
        {
            MagicVelocity = magicVelocity;
            MagicAccuracy = magicAccuracy;
            SurtlingStaffAoe = surtlingStaffAoe;
            EikthyrStaffAoe = eikthyrStaffAoe;
            LightningStaffAoe = lightningStaffAoe;
            PoisonStaffAoe = poisonStaffAoe;
            ArcticStaffAoe = arcticStaffAoe;
            ModersAoeRange = modersAoeRange;
            ModersChopDamage = modersChopDamage;
            ModersFrostDamage = modersFrostDamage;
            ModersPickaxeDamage = modersPickaxeDamage;
            ModersPierceDamage = modersPierceDamage;
            YagluthAoeRange = yagluthAoeRange;
            YagluthBluntDamage = yagluthBluntDamage;
            YagluthChopDamage = yagluthChopDamage;
            YagluthFireDamage = yagluthFireDamage;
            YagluthPickaxeDamage = yagluthPickaxeDamage;
        }

        internal ConfigEntry<float> MagicVelocity { get; }
        internal ConfigEntry<float> MagicAccuracy { get; }
        internal ConfigEntry<float> SurtlingStaffAoe { get; }
        internal ConfigEntry<float> EikthyrStaffAoe { get; }
        internal ConfigEntry<float> LightningStaffAoe { get; }
        internal ConfigEntry<float> PoisonStaffAoe { get; }
        internal ConfigEntry<float> ArcticStaffAoe { get; }
        internal ConfigEntry<float> ModersAoeRange { get; }
        internal ConfigEntry<float> ModersChopDamage { get; }
        internal ConfigEntry<float> ModersFrostDamage { get; }
        internal ConfigEntry<float> ModersPickaxeDamage { get; }
        internal ConfigEntry<float> ModersPierceDamage { get; }
        internal ConfigEntry<float> YagluthAoeRange { get; }
        internal ConfigEntry<float> YagluthBluntDamage { get; }
        internal ConfigEntry<float> YagluthChopDamage { get; }
        internal ConfigEntry<float> YagluthFireDamage { get; }
        internal ConfigEntry<float> YagluthPickaxeDamage { get; }

        internal static bool TryCreate(
            Type? pluginType,
            Type? configType,
            out ProjectileConfiguration? configuration,
            out string missingField)
        {
            configuration = null;
            missingField = PluginTypeName;
            if (pluginType == null)
            {
                return false;
            }

            missingField = ConfigTypeName;
            if (configType == null)
            {
                return false;
            }

            if (!TryGetFloatConfig(pluginType, "_magicVelocity", out ConfigEntry<float> magicVelocity, out missingField) ||
                !TryGetFloatConfig(pluginType, "_magciAccuracy", out ConfigEntry<float> magicAccuracy, out missingField) ||
                !TryGetFloatConfig(configType, "_surtlingStaffAOE", out ConfigEntry<float> surtlingStaffAoe, out missingField) ||
                !TryGetFloatConfig(configType, "_eikthyrStaffAOE", out ConfigEntry<float> eikthyrStaffAoe, out missingField) ||
                !TryGetFloatConfig(configType, "_lightningStaffAOE", out ConfigEntry<float> lightningStaffAoe, out missingField) ||
                !TryGetFloatConfig(configType, "_poisonStaffAOE", out ConfigEntry<float> poisonStaffAoe, out missingField) ||
                !TryGetFloatConfig(configType, "_arcticStaffAOE", out ConfigEntry<float> arcticStaffAoe, out missingField) ||
                !TryGetFloatConfig(configType, "_modersAoeRange", out ConfigEntry<float> modersAoeRange, out missingField) ||
                !TryGetFloatConfig(configType, "_modersChopDamage", out ConfigEntry<float> modersChopDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_modersFrostDamage", out ConfigEntry<float> modersFrostDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_modersPickaxeDamage", out ConfigEntry<float> modersPickaxeDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_modersPierceDamage", out ConfigEntry<float> modersPierceDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_yagluthAoeRange", out ConfigEntry<float> yagluthAoeRange, out missingField) ||
                !TryGetFloatConfig(configType, "_yagluthBluntDamage", out ConfigEntry<float> yagluthBluntDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_yagluthChopDamage", out ConfigEntry<float> yagluthChopDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_yagluthFireDamage", out ConfigEntry<float> yagluthFireDamage, out missingField) ||
                !TryGetFloatConfig(configType, "_yagluthPickaxeDamage", out ConfigEntry<float> yagluthPickaxeDamage, out missingField))
            {
                return false;
            }

            configuration = new ProjectileConfiguration(
                magicVelocity,
                magicAccuracy,
                surtlingStaffAoe,
                eikthyrStaffAoe,
                lightningStaffAoe,
                poisonStaffAoe,
                arcticStaffAoe,
                modersAoeRange,
                modersChopDamage,
                modersFrostDamage,
                modersPickaxeDamage,
                modersPierceDamage,
                yagluthAoeRange,
                yagluthBluntDamage,
                yagluthChopDamage,
                yagluthFireDamage,
                yagluthPickaxeDamage);
            return true;
        }

        private static bool TryGetFloatConfig(
            Type ownerType,
            string fieldName,
            out ConfigEntry<float> entry,
            out string missingField)
        {
            missingField = $"{ownerType.FullName}.{fieldName}";
            FieldInfo? field = AccessTools.Field(ownerType, fieldName);
            if (field == null ||
                !field.IsStatic ||
                field.FieldType != typeof(ConfigEntry<float>) ||
                field.GetValue(null) is not ConfigEntry<float> value)
            {
                entry = null!;
                return false;
            }

            entry = value;
            return true;
        }
    }

    private sealed class PatchReplacement
    {
        internal PatchReplacement(
            MethodInfo original,
            Patch externalPatch,
            HarmonyPatchType patchType)
        {
            Original = original;
            ExternalPatch = externalPatch;
            PatchType = patchType;
        }

        internal MethodInfo Original { get; }
        internal Patch ExternalPatch { get; }
        internal HarmonyPatchType PatchType { get; }
    }
}
