using System;
using System.Reflection;
using System.Runtime.Serialization;

namespace InteropTests;

internal static class ServerSyncHost
{
    // The real BepInEx loader supplies this component before plugin constructors.
    // Isolated Mono has no Unity GameObject; model only its managed main-thread queue.
    // The queued ServerSync game-start patches are not executed by these unit checks.
    internal static object Prepare(Assembly bep)
    {
        Type type = bep.GetType("BepInEx.ThreadingHelper", true)!;
        object helper = FormatterServices.GetUninitializedObject(type);
        type.GetField("_invokeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(helper, new object());
        type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, helper);
        return helper;
    }
}
