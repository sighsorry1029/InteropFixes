using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace InteropFixes.Modules.EpicLootCraftyBoxes;

// Member and relevant call contracts, not a whole-DLL identity/version allowlist.
// Calls verify required data flow landmarks; they do not prove arbitrary code equivalence.
internal static class BridgeContracts
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(code => code.Value);

    internal static Type RequireType(Assembly assembly, string name) => assembly.GetType(name, true)!;

    internal static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] args)
    {
        MethodInfo? method = type.GetMethod(name, Declared, null, args, null);
        if (method == null || method.ReturnType != result || method.IsGenericMethodDefinition)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    internal static FieldInfo RequireField(Type type, string name, Type fieldType, bool isStatic)
    {
        FieldInfo? field = type.GetField(name, Declared);
        if (field == null || field.FieldType != fieldType || field.IsStatic != isStatic)
            throw new MissingFieldException(type.FullName, name);
        return field;
    }

    internal static void RequireStatic(MethodInfo method, bool isStatic)
    {
        if (method.IsStatic != isStatic || (!method.IsAbstract && method.GetMethodBody() == null))
            throw new InvalidOperationException("Unexpected method implementation: " + method);
    }

    internal static void RequireCalls(MethodInfo method, string declaringType, string name)
    {
        if (!CalledMethods(method).Any(c => c.DeclaringType?.FullName == declaringType && c.Name == name))
            throw new InvalidOperationException("Required call changed: " + method + " -> " + declaringType + "." + name);
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        byte[] bytes = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException("No managed method body: " + method);
        int offset = 0;
        while (offset < bytes.Length)
        {
            short value = bytes[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | bytes[offset++]));
            if (!Codes.TryGetValue(value, out OpCode code)) throw new InvalidOperationException("Unknown IL opcode.");
            int size;
            switch (code.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch: size = checked(4 + 4 * BitConverter.ToInt32(bytes, offset)); break;
                default: size = 4; break;
            }
            if (offset + size > bytes.Length || size < 0) throw new InvalidOperationException("Invalid IL operand.");
            if ((code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn) && size == 4)
                yield return method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset),
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments())!;
            offset += size;
        }
    }
}
