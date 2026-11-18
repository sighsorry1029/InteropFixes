using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace DispatchProbe;

internal static class Program
{
    private const string OriginalOwner = "interopfixes.probe.pov";
    private const string LookupOwner = "interopfixes.probe.lookup";
    private const string ForeignOwner = "interopfixes.probe.foreign";
    private static readonly MethodInfo Callback = typeof(Fixture).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Target = typeof(Fixture).GetMethod(nameof(Fixture.UpdateRecipeList))!;
    private static readonly MethodInfo TypeLookup = typeof(AccessTools).GetMethod(nameof(AccessTools.TypeByName), new[] { typeof(string) })!;
    private static readonly MethodInfo FieldLookup = typeof(AccessTools).GetMethod(nameof(AccessTools.Field), new[] { typeof(Type), typeof(string) })!;
    private static int _assertions;
    private static int _safeTypes;
    private static int _safeFields;
    private static int _foreignCalls;

    private static int Main(string[] args)
    {
        Console.WriteLine($"Runtime={Environment.Version}; Mono={Type.GetType("Mono.Runtime") != null}; PointerSize={IntPtr.Size}; Harmony={typeof(Harmony).Assembly.GetName().Version}");
        if (args.Length != 1) throw new ArgumentException("Run each mode in its own process: early | cold | warm");
        switch (args[0])
        {
            case "early": Run("before original registration", patchFirst: true, warmFirst: false); break;
            case "cold": Run("registered cold wrapper", patchFirst: false, warmFirst: false); break;
            case "warm": Run("registered warm wrapper", patchFirst: false, warmFirst: true); break;
            default: throw new ArgumentException("Unknown mode: " + args[0]);
        }
        Console.WriteLine($"{_assertions} dispatch assertions passed. Managed Harmony fixture only; no Unity/game session was run.");
        return 0;
    }

    private static void Run(string label, bool patchFirst, bool warmFirst)
    {
        var original = new Harmony(OriginalOwner);
        var lookup = new Harmony(LookupOwner);
        var foreign = new Harmony(ForeignOwner);
        _safeTypes = _safeFields = _foreignCalls = 0;
        Fixture.Reset();
        try
        {
            if (patchFirst) InstallLookup(lookup);
            original.Patch(Target, postfix: new HarmonyMethod(Callback));
            foreign.Patch(Target, postfix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ForeignPostfix), BindingFlags.NonPublic | BindingFlags.Static)!));
            if (warmFirst)
            {
                InvokeAndCheck(11, label + " baseline");
                Check(_safeTypes == 0 && _safeFields == 0, label + " baseline is native");
            }
            if (!patchFirst) InstallLookup(lookup);

            // The outer wrapper is not rebuilt after patching its already registered callback.
            InvokeAndCheck(22, label + " patched dispatch");
            Check(_safeTypes == 1 && _safeFields == 1, label + " both callback lookups are replaced");
            Check(Harmony.GetPatchInfo(Target).Postfixes.Any(x => x.owner == OriginalOwner), label + " original owner is retained");
            Check(Harmony.GetPatchInfo(Target).Postfixes.Any(x => x.owner == ForeignOwner), label + " foreign owner is retained");

            lookup.UnpatchSelf();
            InvokeAndCheck(11, label + " teardown dispatch");
            Check(_safeTypes == 1 && _safeFields == 1, label + " teardown restores original callback without wrapper rebuild");
            Check(Harmony.GetPatchInfo(Callback)?.Owners.Contains(LookupOwner) != true, label + " lookup owner removed");
            Check(Harmony.GetPatchInfo(Target).Postfixes.Count(x => x.owner == OriginalOwner || x.owner == ForeignOwner) == 2,
                label + " teardown preserves both outer postfixes");
            Console.WriteLine($"PASS {label}: patch and teardown both dispatch correctly; outer wrapper was not regenerated.");
        }
        finally
        {
            lookup.UnpatchSelf();
            original.UnpatchSelf();
            foreign.UnpatchSelf();
        }
    }

    private static void InstallLookup(Harmony harmony) => harmony.Patch(Callback,
        transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(ReplaceLookups), BindingFlags.NonPublic | BindingFlags.Static)!));

    private static IEnumerable<CodeInstruction> ReplaceLookups(IEnumerable<CodeInstruction> instructions)
    {
        int types = 0, fields = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(TypeLookup))
            {
                instruction.operand = typeof(Program).GetMethod(nameof(SafeType), BindingFlags.NonPublic | BindingFlags.Static);
                types++;
            }
            else if (instruction.Calls(FieldLookup))
            {
                instruction.operand = typeof(Program).GetMethod(nameof(SafeField), BindingFlags.NonPublic | BindingFlags.Static);
                fields++;
            }
            yield return instruction;
        }
        if (types != 1 || fields != 1) throw new InvalidOperationException($"Unexpected fixture calls: types={types}, fields={fields}");
    }

    private static Type SafeType(string name)
    {
        if (name != typeof(OptionalType).FullName) throw new InvalidOperationException("Lookup argument changed.");
        _safeTypes++;
        return typeof(PatchedOptionalType);
    }

    private static FieldInfo? SafeField(Type? type, string name)
    {
        _safeFields++;
        return type == null ? null : AccessTools.Field(type, name);
    }

    private static void ForeignPostfix() => _foreignCalls++;

    private static void InvokeAndCheck(int value, string message)
    {
        Fixture.ClearLookupCache();
        int beforeNative = Fixture.NativeCalls, beforeCallback = Fixture.CleanupCalls, beforeForeign = _foreignCalls;
        Fixture.UpdateRecipeList();
        Check(Fixture.Value == value, $"{message}: result={Fixture.Value}, expected={value}");
        Check(Fixture.NativeCalls == beforeNative + 1 && Fixture.CleanupCalls == beforeCallback + 1,
            message + ": base method and callback cleanup still run");
        Check(_foreignCalls == beforeForeign + 1, message + ": foreign postfix still runs");
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        _assertions++;
    }

    private static class Fixture
    {
        private static bool _looked;
        private static FieldInfo? _entries;
        internal static int NativeCalls;
        internal static int CleanupCalls;
        internal static int Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void UpdateRecipeList() => NativeCalls++;

        // Deliberately no NoInlining attribute: the real PoV postfix has no such attribute.
        private static void Postfix()
        {
            CleanupCalls++;
            if (!_looked)
            {
                _looked = true;
                _entries = AccessTools.Field(AccessTools.TypeByName("DispatchProbe.OptionalType"), "Entries");
            }
            Value = (int?)_entries?.GetValue(null) ?? -1;
        }

        internal static void ClearLookupCache() { _looked = false; _entries = null; }
        internal static void Reset() { ClearLookupCache(); NativeCalls = CleanupCalls = Value = 0; }
    }
}

internal static class OptionalType { internal static int Entries = 11; }
internal static class PatchedOptionalType { internal static int Entries = 22; }
