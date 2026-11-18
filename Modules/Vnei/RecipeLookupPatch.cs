using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace InteropFixes.Modules.Vnei;

internal static class RecipeLookupPatch
{
    private static Type? _plugin;
    private static MethodInfo? _lookup;
    internal static bool Ready { get; private set; }

    internal static void Initialize(Type plugin, MethodInfo lookup)
    {
        _plugin = plugin;
        _lookup = lookup;
        Ready = false;
    }

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        Ready = _lookup != null && _plugin != null && HasTooltipFlow(code, _plugin, _lookup);
        if (!Ready) return code;
        MethodInfo resolve = AccessTools.DeclaredMethod(typeof(HoveredItemLookup), nameof(HoveredItemLookup.Resolve));
        var result = new List<CodeInstruction>(code.Count + 3);
        foreach (CodeInstruction original in code)
        {
            var copy = new CodeInstruction(original);
            result.Add(copy);
            if (!original.Calls(_lookup)) continue;
            copy.opcode = OpCodes.Call;
            copy.operand = resolve;
            // Restore VNEI's exact stack type without adding a compile-time VNEI dependency.
            var cast = new CodeInstruction(OpCodes.Castclass, _lookup!.ReturnType);
            // An exception region ending on the replaced call must include its new cast too.
            cast.blocks.AddRange(copy.blocks.Where(b => b.blockType == ExceptionBlockType.EndExceptionBlock));
            copy.blocks.RemoveAll(b => b.blockType == ExceptionBlockType.EndExceptionBlock);
            result.Add(cast);
        }
        return result;
    }

    private static bool HasTooltipFlow(List<CodeInstruction> source, Type plugin, MethodInfo lookup)
    {
        // Inspect the actual incoming Harmony stream. Unrelated earlier work in Update is ignored.
        List<CodeInstruction> code = source.Where(i => i.opcode != OpCodes.Nop).ToList();
        int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(lookup)).ToArray();
        if (calls.Length != 3 || calls.Any(i => i == 0 || i + 1 >= code.Count || code[i].opcode != OpCodes.Call)) return false;
        int topic = TooltipLocal(code, "m_topic"), text = TooltipLocal(code, "m_text");
        if (topic < 0 || text < 0 || topic == text) return false;
        int item = LocalIndex(code[calls[0] + 1], false);
        int stripped = LocalIndex(code[calls[2] - 1], true);
        if (item < 0 || stripped < 0 || stripped == topic || stripped == text ||
            LocalIndex(code[calls[0] - 1], true) != topic || LocalIndex(code[calls[1] - 1], true) != text ||
            calls.Any(i => LocalIndex(code[i + 1], false) != item)) return false;
        int hotkey = code.FindIndex(i => i.opcode == OpCodes.Ldsfld && i.operand is FieldInfo f &&
            f.DeclaringType == plugin && f.Name == "viewRecipeHotkey");
        if (hotkey < 0 || hotkey >= calls[0]) return false;
        // The third candidate is the rich-tag-stripped topic, not another arbitrary lookup in Update.
        bool strippedTopic = false;
        for (int i = calls[1] + 1; i + 5 < calls[2]; i++)
        {
            if (code[i].opcode == OpCodes.Ldsfld && code[i].operand is FieldInfo regex &&
                regex.DeclaringType == plugin && regex.Name == "RichTagRegex" && regex.FieldType == typeof(System.Text.RegularExpressions.Regex) &&
                LocalIndex(code[i + 1], true) == topic && code[i + 2].opcode == OpCodes.Ldsfld &&
                code[i + 2].operand is FieldInfo empty && empty.DeclaringType == typeof(string) && empty.Name == "Empty" &&
                Calls(code[i + 3], typeof(System.Text.RegularExpressions.Regex), "Replace", typeof(string), typeof(string)) &&
                Calls(code[i + 4], typeof(string), "Trim") && LocalIndex(code[i + 5], false) == stripped)
                strippedTopic = true;
        }
        if (!strippedTopic) return false;
        // Preserve the existing recipe/UI responsibility: all candidates feed the same ShowRecipe item.
        for (int i = calls[2] + 1; i + 2 < code.Count; i++)
        {
            if (LocalIndex(code[i], true) == item && code[i + 1].opcode == OpCodes.Ldc_I4_1 &&
                code[i + 2].operand is MethodInfo show && (code[i + 2].opcode == OpCodes.Callvirt || code[i + 2].opcode == OpCodes.Call) && !show.IsStatic &&
                show.DeclaringType?.Assembly == plugin.Assembly && show.DeclaringType.FullName == "VNEI.UI.BaseUI" &&
                show.Name == "ShowRecipe" && show.ReturnType == typeof(void) &&
                show.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { lookup.ReturnType, typeof(bool) }))
                return true;
        }
        return false;
    }

    private static int TooltipLocal(List<CodeInstruction> code, string field)
    {
        int found = -1;
        for (int i = 0; i + 3 < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldsfld || code[i].operand is not FieldInfo current ||
                current.DeclaringType != typeof(UITooltip) || current.Name != "m_current" || current.FieldType != typeof(UITooltip) ||
                code[i + 1].opcode != OpCodes.Ldfld || code[i + 1].operand is not FieldInfo value ||
                value.DeclaringType != typeof(UITooltip) || value.Name != field || value.FieldType != typeof(string) ||
                !Calls(code[i + 2], typeof(string), "Trim")) continue;
            int local = LocalIndex(code[i + 3], false);
            if (local < 0 || found >= 0) return -1;
            found = local;
        }
        return found;
    }

    private static bool Calls(CodeInstruction instruction, Type declaring, string name, params Type[] parameters) =>
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
        instruction.operand is MethodInfo method && method.DeclaringType == declaring && method.Name == name &&
        method.ReturnType == typeof(string) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters);

    private static int LocalIndex(CodeInstruction instruction, bool load)
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

    internal static void Clear()
    {
        _plugin = null;
        _lookup = null;
        Ready = false;
    }
}
