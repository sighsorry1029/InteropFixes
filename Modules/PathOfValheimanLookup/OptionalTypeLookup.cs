using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace InteropFixes.Modules.PathOfValheimanLookup;

internal static class OptionalTypeLookup
{
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);

    // These callers supply full names, not assembly-qualified or short names. GetType
    // does not enumerate unrelated optional types (including VNEI's EpicLoot types).
    // PoV owns its existing once-only result caches; do not add a negative cache here.
    internal static Type? Resolve(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch (Exception error) when (error is TypeLoadException || error is ReflectionTypeLoadException ||
                                          error is FileNotFoundException || error is FileLoadException ||
                                          error is BadImageFormatException)
            {
                // A broken installed type is different from an absent optional mod.
                // Skip the broken definition, as Harmony's original partial-type
                // enumeration did, while still allowing another loaded definition.
                string key = assembly.FullName + ":" + fullName;
                lock (ReportedFailures)
                    if (ReportedFailures.Add(key))
                        InteropFixesPlugin.ModLogger.LogWarning($"PoV optional lookup: could not load {fullName} from {assembly.GetName().Name}; skipping this definition. {error.GetType().Name}: {error.Message}");
            }
        }
        return null;
    }

    // Preserve Harmony's inherited/private-field lookup and real missing-field
    // diagnostics. Only an absent optional type is an expected, silent result.
    internal static FieldInfo? FieldIfPresent(Type? type, string name) =>
        type == null ? null : AccessTools.Field(type, name);
}
