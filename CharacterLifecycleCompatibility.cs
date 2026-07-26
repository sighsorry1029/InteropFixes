using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace InteropFixes;

internal static class CharacterLifecycleCompatibility
{
    private static bool _installed;

    internal static void TryInstall(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        MethodInfo? destroyObject = AccessTools.DeclaredMethod(
            typeof(ZNetScene),
            nameof(ZNetScene.Destroy),
            new[] { typeof(GameObject) });
        MethodInfo? removeStartEffects = AccessTools.DeclaredMethod(typeof(StatusEffect), "RemoveStartEffects");
        MethodInfo? characterOnDestroy = AccessTools.DeclaredMethod(typeof(Character), "OnDestroy");
        MethodInfo? sceneOnDestroy = AccessTools.DeclaredMethod(typeof(ZNetScene), "OnDestroy");
        FieldInfo? statusEffectCharacter = AccessTools.Field(typeof(StatusEffect), "m_character");
        FieldInfo? startEffectInstances = AccessTools.Field(typeof(StatusEffect), "m_startEffectInstances");
        FieldInfo? characterSeMan = AccessTools.Field(typeof(Character), "m_seman");

        MethodInfo? trackPrefix = AccessTools.DeclaredMethod(typeof(CharacterLifecycleCompatibility), nameof(TrackDestroyPrefix));
        MethodInfo? teardownPrefix = AccessTools.DeclaredMethod(typeof(CharacterLifecycleCompatibility), nameof(RemoveStartEffectsPrefix));
        MethodInfo? characterPrefix = AccessTools.DeclaredMethod(typeof(CharacterLifecycleCompatibility), nameof(CharacterOnDestroyPrefix));
        MethodInfo? characterFinalizer = AccessTools.DeclaredMethod(typeof(CharacterLifecycleCompatibility), nameof(CharacterOnDestroyFinalizer));
        MethodInfo? scenePostfix = AccessTools.DeclaredMethod(typeof(CharacterLifecycleCompatibility), nameof(SceneOnDestroyPostfix));

        if (destroyObject == null ||
            removeStartEffects == null ||
            characterOnDestroy == null ||
            sceneOnDestroy == null ||
            statusEffectCharacter == null ||
            startEffectInstances == null ||
            characterSeMan == null ||
            trackPrefix == null ||
            teardownPrefix == null ||
            characterPrefix == null ||
            characterFinalizer == null ||
            scenePostfix == null)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                "Character lifecycle compatibility skipped: one or more required Valheim methods or fields were not found.");
            return;
        }

        try
        {
            harmony.Patch(
                destroyObject,
                prefix: new HarmonyMethod(trackPrefix) { priority = Priority.First });
            harmony.Patch(
                removeStartEffects,
                prefix: new HarmonyMethod(teardownPrefix) { priority = Priority.First });
            harmony.Patch(
                characterOnDestroy,
                prefix: new HarmonyMethod(characterPrefix) { priority = Priority.Last },
                finalizer: new HarmonyMethod(characterFinalizer) { priority = Priority.Last });
            harmony.Patch(sceneOnDestroy, postfix: new HarmonyMethod(scenePostfix));

            _installed = true;
            InteropFixesPlugin.ModLogger.LogInfo(
                "Installed Character removal tracking, StatusEffect teardown guard, and Character.OnDestroy repair.");
        }
        catch (Exception ex)
        {
            SafeUnpatch(harmony, destroyObject, trackPrefix);
            SafeUnpatch(harmony, removeStartEffects, teardownPrefix);
            SafeUnpatch(harmony, characterOnDestroy, characterPrefix);
            SafeUnpatch(harmony, characterOnDestroy, characterFinalizer);
            SafeUnpatch(harmony, sceneOnDestroy, scenePostfix);
            CreatureRemovalTracker.Clear();

            bool rollbackComplete =
                !IsPatchInstalled(destroyObject, trackPrefix) &&
                !IsPatchInstalled(removeStartEffects, teardownPrefix) &&
                !IsPatchInstalled(characterOnDestroy, characterPrefix) &&
                !IsPatchInstalled(characterOnDestroy, characterFinalizer) &&
                !IsPatchInstalled(sceneOnDestroy, scenePostfix);
            if (!rollbackComplete)
            {
                try
                {
                    harmony.UnpatchSelf();
                }
                catch
                {
                    // The verification below determines whether the fallback rollback succeeded.
                }

                rollbackComplete =
                    !IsPatchInstalled(destroyObject, trackPrefix) &&
                    !IsPatchInstalled(removeStartEffects, teardownPrefix) &&
                    !IsPatchInstalled(characterOnDestroy, characterPrefix) &&
                    !IsPatchInstalled(characterOnDestroy, characterFinalizer) &&
                    !IsPatchInstalled(sceneOnDestroy, scenePostfix);
            }

            if (rollbackComplete)
            {
                InteropFixesPlugin.ModLogger.LogWarning(
                    $"Character lifecycle compatibility was rolled back after an installation failure: {ex.Message}");
            }
            else
            {
                InteropFixesPlugin.ModLogger.LogError(
                    $"Character lifecycle compatibility installation failed and rollback was incomplete: {ex}");
            }
        }
    }

    internal static void ResetInstallationState()
    {
        _installed = false;
    }

    private static void TrackDestroyPrefix(GameObject __0)
    {
        CreatureRemovalTracker.Track(__0);
    }

    private static bool RemoveStartEffectsPrefix(
        Character ___m_character,
        ref GameObject[]? ___m_startEffectInstances)
    {
        if (!CreatureRemovalTracker.IsPending(___m_character))
        {
            return true;
        }

        GameObject[]? startEffectInstances = ___m_startEffectInstances;
        if (startEffectInstances == null || !ZNetScene.instance)
        {
            return false;
        }

        foreach (GameObject effectInstance in startEffectInstances)
        {
            if (!effectInstance)
            {
                continue;
            }

            ZNetView? view = effectInstance.GetComponent<ZNetView>();
            if (view && view.IsValid())
            {
                view.ClaimOwnership();
                view.Destroy();
            }
            else
            {
                UnityEngine.Object.Destroy(effectInstance);
            }
        }

        ___m_startEffectInstances = null;
        return false;
    }

    private static bool CharacterOnDestroyPrefix(Character __instance, SEMan? ___m_seman)
    {
        if (___m_seman != null)
        {
            return true;
        }

        try
        {
            CreatureRemovalTracker.RepairCharacterReferences(__instance);
        }
        catch (Exception cleanupException)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"Failed to repair an uninitialized Character.OnDestroy: {cleanupException}");
        }
        finally
        {
            CreatureRemovalTracker.Complete(__instance);
        }

        return false;
    }

    private static Exception? CharacterOnDestroyFinalizer(Character __instance, Exception? __exception)
    {
        bool pending = CreatureRemovalTracker.IsPending(__instance);
        try
        {
            if (pending || __exception != null)
            {
                CreatureRemovalTracker.RepairCharacterReferences(__instance);
            }
        }
        catch (Exception cleanupException)
        {
            InteropFixesPlugin.ModLogger.LogWarning(
                $"Failed to repair Character.OnDestroy bookkeeping: {cleanupException}");
        }
        finally
        {
            CreatureRemovalTracker.Complete(__instance);
        }

        return __exception;
    }

    private static void SceneOnDestroyPostfix()
    {
        CreatureRemovalTracker.Clear();
    }

    private static void SafeUnpatch(Harmony harmony, MethodBase original, MethodInfo patch)
    {
        try
        {
            harmony.Unpatch(original, patch);
        }
        catch
        {
            // Installation already failed; continue rolling back the remaining methods.
        }
    }

    private static bool IsPatchInstalled(MethodBase original, MethodInfo expectedPatch)
    {
        Patches? patchInfo = Harmony.GetPatchInfo(original);
        if (patchInfo == null)
        {
            return false;
        }

        return ContainsPatch(patchInfo.Prefixes, expectedPatch) ||
               ContainsPatch(patchInfo.Postfixes, expectedPatch) ||
               ContainsPatch(patchInfo.Finalizers, expectedPatch);
    }

    private static bool ContainsPatch(IEnumerable<Patch> patches, MethodInfo expectedPatch)
    {
        foreach (Patch patch in patches)
        {
            MethodInfo installedPatch = patch.PatchMethod;
            if (ReferenceEquals(installedPatch, expectedPatch) ||
                installedPatch.Equals(expectedPatch))
            {
                return true;
            }
        }

        return false;
    }
}

internal static class CreatureRemovalTracker
{
    private const float PendingLifetimeSeconds = 15f;
    private const float PruneIntervalSeconds = 1f;

    private sealed class PendingRemoval
    {
        internal PendingRemoval(Character character, float expiresAt)
        {
            Character = character;
            ExpiresAt = expiresAt;
        }

        internal Character Character { get; }
        internal float ExpiresAt { get; set; }
    }

    private static readonly Dictionary<int, PendingRemoval> PendingByInstanceId = new();
    private static readonly List<int> ExpiredInstanceIds = new();
    private static float _nextPruneAt;

    internal static void Track(GameObject gameObject)
    {
        if (!gameObject)
        {
            return;
        }

        Character? character = gameObject.GetComponent<Character>();
        if (!character)
        {
            return;
        }

        int instanceId = character.GetInstanceID();
        float expiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds;
        if (PendingByInstanceId.TryGetValue(instanceId, out PendingRemoval? pending) &&
            ReferenceEquals(pending.Character, character))
        {
            pending.ExpiresAt = expiresAt;
            return;
        }

        PendingByInstanceId[instanceId] = new PendingRemoval(character, expiresAt);
    }

    internal static bool IsPending(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return false;
        }

        int instanceId = character.GetInstanceID();
        return PendingByInstanceId.TryGetValue(instanceId, out PendingRemoval? pending) &&
               ReferenceEquals(pending.Character, character);
    }

    internal static void Complete(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return;
        }

        int instanceId = character.GetInstanceID();
        if (PendingByInstanceId.TryGetValue(instanceId, out PendingRemoval? pending) &&
            ReferenceEquals(pending.Character, character))
        {
            PendingByInstanceId.Remove(instanceId);
        }
    }

    internal static void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (PendingByInstanceId.Count == 0 || now < _nextPruneAt)
        {
            return;
        }

        _nextPruneAt = now + PruneIntervalSeconds;
        ExpiredInstanceIds.Clear();
        foreach (KeyValuePair<int, PendingRemoval> pair in PendingByInstanceId)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                ExpiredInstanceIds.Add(pair.Key);
            }
        }

        foreach (int instanceId in ExpiredInstanceIds)
        {
            PendingByInstanceId.Remove(instanceId);
        }

        ExpiredInstanceIds.Clear();
    }

    internal static void RepairCharacterReferences(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return;
        }

        List<Character> characters = Character.GetAllCharacters();
        for (int i = characters.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(characters[i], character))
            {
                characters.RemoveAt(i);
            }
        }

        TryRemoveCharacterHud(character);
    }

    internal static void Clear()
    {
        PendingByInstanceId.Clear();
        ExpiredInstanceIds.Clear();
        _nextPruneAt = 0f;
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
            InteropFixesPlugin.ModLogger.LogDebug(
                $"Failed to repair a removed Character HUD reference: {ex.Message}");
        }
    }
}
