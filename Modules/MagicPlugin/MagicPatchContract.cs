using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace InteropFixes;

// Replacing a third-party method requires more than its name/signature. Check only
// the replaced method and its reachable in-assembly helpers, never a whole DLL ID.
internal static class MagicPatchContract
{
    // Same related method/helper contract in inspected MagicPlugin 2.1.9–2.2.2.
    internal const string ProjectileContract = "17BAA2C31BB6E9DD4DA2F3B85E3726AAB53FD4AC8CD7D9F2835E4EBC91A97538";
    internal const string TeleportContract = "21E126B35A09509ADAEE71DB5440E8B20306EEC82EAB236D25ED38564C200AAF";

    internal static bool Matches(MethodInfo method, string expected, out string failure)
    {
        try
        {
            string actual = Fingerprint(method);
            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                failure = string.Empty;
                return true;
            }
            failure = $"the {method.Name} body/helper contract changed ({actual}); its original MagicPlugin patch is retained";
        }
        catch (Exception ex)
        {
            failure = $"the {method.Name} body/helper contract could not be read ({ex.Message}); its original MagicPlugin patch is retained";
        }
        return false;
    }

    internal static string Fingerprint(MethodBase root)
    {
        Queue<MethodBase> pending = new();
        HashSet<MethodBase> visited = new();
        StringBuilder output = new();
        pending.Enqueue(root);
        while (pending.Count != 0)
        {
            MethodBase method = pending.Dequeue();
            if (!visited.Add(method)) continue;
            if (visited.Count > 128) throw new InvalidOperationException("Helper graph exceeds its bounded contract scope");
            if (Harmony.GetPatchInfo(method)?.Owners.Count > 0)
                throw new InvalidOperationException("The replaced method or a reachable helper already has Harmony hooks");
            MethodBody body = method.GetMethodBody() ?? throw new InvalidOperationException("Missing method body");
            output.Append("METHOD ").Append(MethodName(method)).Append('|').Append(method.IsStatic).Append('|').Append(body.InitLocals).Append('\n');
            foreach (LocalVariableInfo local in body.LocalVariables)
                output.Append("LOCAL ").Append(local.LocalIndex).Append(':').Append(TypeName(local.LocalType)).Append(':').Append(local.IsPinned).Append('\n');
            List<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(method);
            if (instructions.Count > 8192) throw new InvalidOperationException("Method exceeds its bounded contract scope");
            Dictionary<Label, int> targets = new();
            int ordinal = 0;
            foreach (CodeInstruction code in instructions)
            {
                foreach (Label label in code.labels) targets[label] = ordinal;
                if (code.opcode != OpCodes.Nop) ordinal++;
            }
            foreach (CodeInstruction code in instructions)
            {
                foreach (ExceptionBlock block in code.blocks)
                    output.Append("EH ").Append(block.blockType).Append(':').Append(block.catchType == null ? string.Empty : TypeName(block.catchType)).Append('\n');
                if (code.opcode == OpCodes.Nop) continue;
                string opcode = code.opcode.Name!;
                object? operand = code.operand;
                NormalizeShortForms(ref opcode, ref operand);
                output.Append(opcode).Append('|');
                switch (operand)
                {
                    case MethodBase called:
                        output.Append(MethodName(called));
                        if (called.Module == root.Module) pending.Enqueue(called);
                        break;
                    case FieldInfo field:
                        output.Append(TypeName(field.DeclaringType!)).Append("::").Append(GeneratedName(field.Name))
                            .Append(':').Append(TypeName(field.FieldType)).Append(':').Append(field.IsStatic);
                        break;
                    case Type type:
                        output.Append(TypeName(type));
                        break;
                    case Label label:
                        output.Append('@').Append(targets[label]);
                        break;
                    case Label[] labels:
                        foreach (Label branch in labels) output.Append('@').Append(targets[branch]).Append(',');
                        break;
                    case LocalVariableInfo local:
                        output.Append(local.LocalIndex);
                        break;
                    case string value:
                        output.Append(value.Length).Append(':').Append(value);
                        break;
                    case float value:
                        output.Append(BitConverter.ToInt32(BitConverter.GetBytes(value), 0).ToString("X8", CultureInfo.InvariantCulture));
                        break;
                    case double value:
                        output.Append(BitConverter.DoubleToInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture));
                        break;
                    case null:
                        break;
                    case IFormattable value:
                        output.Append(value.ToString(null, CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported IL operand " + operand.GetType().FullName);
                }
                output.Append('\n');
            }
        }
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(output.ToString()))).Replace("-", string.Empty);
    }

    private static string MethodName(MethodBase method)
    {
        string arguments = string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)));
        string generic = method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(TypeName)) + ">" : string.Empty;
        string result = method is MethodInfo info ? TypeName(info.ReturnType) : "ctor";
        return TypeName(method.DeclaringType!) + "::" + GeneratedName(method.Name) + generic + "(" + arguments + ")->" + result;
    }

    private static string TypeName(Type type)
    {
        if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
        if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
        if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericType)
            return GeneratedName(type.GetGenericTypeDefinition().FullName!) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
        return GeneratedName(type.FullName ?? type.Name);
    }

    private static string GeneratedName(string value)
    {
        // Compiler-generated class/cache numbering can change when unrelated methods
        // are inserted. Retain captured-field names and lambda ordinal within a method.
        value = Regex.Replace(value, @"(<>c__DisplayClass)\d+_\d+", "$1*", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(<>f__AnonymousType)\d+", "$1*", RegexOptions.CultureInvariant);
        return Regex.Replace(value, @"(<>9__|b__)\d+_(\d+)", "$1*_$2", RegexOptions.CultureInvariant);
    }

    private static void NormalizeShortForms(ref string opcode, ref object? operand)
    {
        foreach (string prefix in new[] { "ldarg.", "ldloc.", "stloc." })
        {
            if (opcode.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(opcode.Substring(prefix.Length), out int index))
            {
                opcode = prefix.TrimEnd('.');
                operand = index;
                return;
            }
        }
        if (opcode.StartsWith("ldc.i4.", StringComparison.Ordinal))
        {
            string suffix = opcode.Substring("ldc.i4.".Length);
            if (suffix == "m1" || int.TryParse(suffix, out _))
            {
                opcode = "ldc.i4";
                operand = suffix == "m1" ? -1 : int.Parse(suffix, CultureInfo.InvariantCulture);
                return;
            }
        }
        if (opcode.EndsWith(".s", StringComparison.Ordinal)) opcode = opcode.Substring(0, opcode.Length - 2);
    }
}
