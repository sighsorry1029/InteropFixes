using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace InteropFixes.Modules.PathOfValheimanLookup;

internal static class OptionalLookupPatch
{
    private const string Quiver = "PassiveTree.Gear.BetterArcheryQuiver";
    private const string Trade = "PassiveTree.TradeMechanics";
    private const string Recipes = "PassiveTree.Loot.ItemAscension+UpgradeListClean";
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo TypeLookup = typeof(AccessTools).GetMethod(nameof(AccessTools.TypeByName), new[] { typeof(string) })!;
    private static readonly MethodInfo FieldLookup = typeof(AccessTools).GetMethod(nameof(AccessTools.Field), new[] { typeof(Type), typeof(string) })!;
    private static readonly MethodInfo Resolve = typeof(OptionalTypeLookup).GetMethod(nameof(OptionalTypeLookup.Resolve), Declared)!;
    private static readonly MethodInfo FieldIfPresent = typeof(OptionalTypeLookup).GetMethod(nameof(OptionalTypeLookup.FieldIfPresent), Declared)!;
    private static readonly HashSet<MethodBase> Accepted = new();
    private static readonly HashSet<MethodBase> Reported = new();

    internal static MethodInfo[] GetTargets(Assembly assembly) => new[]
    {
        Target(assembly, Quiver, "Tick", Type.EmptyTypes),
        Target(assembly, Quiver, "Sync", Type.EmptyTypes),
        Target(assembly, Trade, "get_ExtraSlots", Type.EmptyTypes),
        Target(assembly, Recipes, "Postfix", new[] { typeof(InventoryGui) })
    };

    private static MethodInfo Target(Assembly assembly, string type, string name, Type[] parameters) =>
        assembly.GetType(type, true)!.GetMethod(name, Declared, null, parameters, null)
        ?? throw new MissingMethodException(type, name);

    internal static bool WasAccepted(MethodBase method) => Accepted.Contains(method);
    internal static void Clear() { Accepted.Clear(); Reported.Clear(); }

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        bool valid = TryRewrite(__originalMethod, instructions, out var result, out string reason);
        if (valid) Accepted.Add(__originalMethod);
        else
        {
            Accepted.Remove(__originalMethod);
            // A later Harmony rebuild can change one independent lookup path. Return
            // that incoming body unchanged rather than break a foreign patch operation.
            if (Reported.Add(__originalMethod))
                InteropFixesPlugin.ModLogger.LogWarning($"PoV optional lookup: retaining original {__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}: {reason}");
        }
        return result;
    }

    internal static bool TryRewrite(MethodBase original, IEnumerable<CodeInstruction> instructions,
        out List<CodeInstruction> rewritten, out string reason)
    {
        var source = instructions.ToList();
        rewritten = source;
        reason = "lookup/cache contract changed";
        if (original is not MethodInfo method || !method.IsStatic || method.ContainsGenericParameters) return false;
        Type? owner = method.DeclaringType;
        bool quiver = owner?.FullName == Quiver && (method.Name == "Tick" || method.Name == "Sync");
        bool trade = owner?.FullName == Trade && method.Name == "get_ExtraSlots";
        bool recipes = owner?.FullName == Recipes && method.Name == "Postfix";
        if (!(quiver || trade || recipes) || method.ReturnType != (trade ? typeof(bool) : typeof(void)) ||
            !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(recipes ? new[] { typeof(InventoryGui) } : Type.EmptyTypes))
            return false;

        int[] indices = Enumerable.Range(0, source.Count).Where(i => source[i].opcode != OpCodes.Nop).ToArray();
        var code = indices.Select(i => new CodeInstruction(source[i])).ToList();
        // A label on a no-op immediately before a cache consumer has the same
        // destination. Normalize for validation only; preserve the emitted body.
        for (int n = 0; n < indices.Length; n++)
            for (int i = indices[n] - 1; i >= 0 && source[i].opcode == OpCodes.Nop; i--)
                code[n].labels.AddRange(source[i].labels);
        int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(TypeLookup)).ToArray();
        string[] names = quiver ? new[] { "BetterArchery.BetterArchery" } : trade
            ? new[] { "ExtraSlots.Slots", "ExtraSlots.API" } : new[] { "SeneaLUI.Inv.CraftState" };
        if (calls.Length != names.Length || calls.Where((c, n) => c < 1 || !String(code[c - 1], names[n])).Any()) return false;
        // The old exact-name fallthrough was followed by a short-name scan. All
        // accepted inputs contain namespaces, so that scan cannot add a match.
        int first = calls[0];
        string checkedField = quiver ? "typeLooked" : trade ? "esChecked" : "looked";
        if (first < 5 || !Field(code[first - 5], OpCodes.Ldsfld, owner!, checkedField, typeof(bool)) ||
            !TrueBranch(code[first - 4]) || code[first - 3].opcode != OpCodes.Ldc_I4_1 ||
            !Field(code[first - 2], OpCodes.Stsfld, owner!, checkedField, typeof(bool))) return false;

        var replacements = new Dictionary<int, MethodInfo>();
        foreach (int call in calls) replacements.Add(indices[call], Resolve);
        if (quiver)
        {
            if (first + 6 >= code.Count || !Field(code[first + 1], OpCodes.Stsfld, owner!, "type", typeof(Type)) ||
                !Field(code[first + 2], OpCodes.Ldsfld, owner!, "type", typeof(Type)) ||
                !BranchesTo(code[first - 4], code[first + 2]) || code[first + 3].opcode != OpCodes.Ldnull ||
                !Call(code[first + 4], typeof(Type), "op_Equality", typeof(bool), typeof(Type), typeof(Type)) ||
                !FalseBranch(code[first + 5])) return false;
            int nullExit = first + 6;
            if (method.Name == "Tick")
            {
                if (nullExit + 2 >= code.Count || code[nullExit].opcode != OpCodes.Ldc_I4_1 ||
                    !Field(code[nullExit + 1], OpCodes.Stsfld, owner!, "done", typeof(bool))) return false;
                nullExit += 2;
            }
            if (code[nullExit].opcode != OpCodes.Ret) return false;
        }
        else if (trade)
        {
            int api = calls[1];
            if (first + 7 >= code.Count || api + 9 >= code.Count) return false;
            int local = Local(code[first + 1], false);
            if (local < 0 || Local(code[first + 2], true) != local || code[first + 3].opcode != OpCodes.Ldnull ||
                !Call(code[first + 4], typeof(Type), "op_Inequality", typeof(bool), typeof(Type), typeof(Type)) ||
                !TrueBranch(code[first + 5]) || code[first + 6].opcode != OpCodes.Ldnull) return false;
            if (!String(code[api + 1], "UpdateSlots") || code[api + 2].opcode != OpCodes.Ldnull ||
                code[api + 3].opcode != OpCodes.Ldnull ||
                !Call(code[api + 4], typeof(AccessTools), "Method", typeof(MethodInfo), typeof(Type), typeof(string), typeof(Type[]), typeof(Type[])) ||
                !Field(code[api + 5], OpCodes.Stsfld, owner!, "esUpdate", typeof(MethodInfo)) ||
                !Field(code[api + 6], OpCodes.Ldsfld, owner!, "esUpdate", typeof(MethodInfo)) ||
                !BranchesTo(code[first - 4], code[api + 6]) || code[api + 7].opcode != OpCodes.Ldnull ||
                !Call(code[api + 8], typeof(MethodInfo), "op_Inequality", typeof(bool), typeof(MethodInfo), typeof(MethodInfo)) ||
                code[api + 9].opcode != OpCodes.Ret) return false;
            // Keep PoV's row hook and its optional API discovery in their original order.
            if (!code.Skip(first + 7).Take(api - first - 7).Any(i => String(i, "GetExtraRows")) ||
                !code.Skip(first + 7).Take(api - first - 7).Any(i => String(i, "ExtraRowsPostfix"))) return false;
        }
        else
        {
            if (first + 4 >= code.Count || !String(code[first + 1], "Entries") || !code[first + 2].Calls(FieldLookup) ||
                !Field(code[first + 3], OpCodes.Stsfld, owner!, "seneal", typeof(FieldInfo)) ||
                !Field(code[first + 4], OpCodes.Ldsfld, owner!, "seneal", typeof(FieldInfo)) ||
                !BranchesTo(code[first - 4], code[first + 4])) return false;
            // This callback also owns native recipe cleanup. Its original pre-lookup
            // and optional-list consumers must remain; never skip the whole postfix.
            bool Pairs(CodeInstruction i) => i.operand is MethodInfo m && i.opcode == OpCodes.Call && m.IsStatic &&
                m.DeclaringType == owner!.DeclaringType && m.Name == "Pairs" && m.ReturnType == typeof(bool) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Recipe), typeof(ItemDrop.ItemData) });
            if (!code.Take(first).Any(Pairs) || !code.Skip(first + 5).Any(Pairs) ||
                !code.Take(first).Any(i => i.opcode == OpCodes.Ldfld && i.operand is FieldInfo f &&
                    f.DeclaringType == typeof(InventoryGui) && f.Name == "m_availableRecipes") ||
                !code.Skip(first + 5).Any(i => i.opcode == OpCodes.Isinst && Equals(i.operand, typeof(System.Collections.IList)))) return false;
            replacements.Add(indices[first + 2], FieldIfPresent);
        }

        rewritten = source.Select((instruction, index) =>
        {
            var copy = new CodeInstruction(instruction);
            if (replacements.TryGetValue(index, out MethodInfo? replacement)) copy.operand = replacement;
            return copy; // Keep opcodes, labels and exception boundaries exactly as received.
        }).ToList();
        reason = "";
        return true;
    }

    private static bool String(CodeInstruction instruction, string text) => instruction.opcode == OpCodes.Ldstr && Equals(instruction.operand, text);
    private static bool Field(CodeInstruction instruction, OpCode op, Type owner, string name, Type type) =>
        instruction.opcode == op && instruction.operand is FieldInfo field && field.DeclaringType == owner &&
        field.Name == name && field.FieldType == type && field.IsStatic;
    private static bool TrueBranch(CodeInstruction instruction) => instruction.opcode == OpCodes.Brtrue || instruction.opcode == OpCodes.Brtrue_S;
    private static bool FalseBranch(CodeInstruction instruction) => instruction.opcode == OpCodes.Brfalse || instruction.opcode == OpCodes.Brfalse_S;
    private static bool BranchesTo(CodeInstruction branch, CodeInstruction target) => branch.operand is Label label && target.labels.Contains(label);
    private static bool Call(CodeInstruction instruction, Type owner, string name, Type returns, params Type[] args) =>
        instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method && method.DeclaringType == owner &&
        method.Name == name && method.ReturnType == returns && method.IsStatic && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(args);
    private static int Local(CodeInstruction instruction, bool load)
    {
        OpCode op = instruction.opcode;
        if (op == (load ? OpCodes.Ldloc_0 : OpCodes.Stloc_0)) return 0;
        if (op == (load ? OpCodes.Ldloc_1 : OpCodes.Stloc_1)) return 1;
        if (op == (load ? OpCodes.Ldloc_2 : OpCodes.Stloc_2)) return 2;
        if (op == (load ? OpCodes.Ldloc_3 : OpCodes.Stloc_3)) return 3;
        if (op != (load ? OpCodes.Ldloc : OpCodes.Stloc) && op != (load ? OpCodes.Ldloc_S : OpCodes.Stloc_S)) return -1;
        if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
        if (instruction.operand is LocalVariableInfo local) return local.LocalIndex;
        return instruction.operand is IConvertible value ? value.ToInt32(null) : -1;
    }
}
