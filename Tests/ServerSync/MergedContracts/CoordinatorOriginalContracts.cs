using Mono.Cecil;

internal static class CoordinatorOriginalContracts
{
    internal static void Verify(string path, Action<bool, string> check)
    {
        using AssemblyDefinition game = AssemblyDefinition.ReadAssembly(path);
        TypeDefinition Type(string name) => game.MainModule.GetType(name)
            ?? throw new InvalidOperationException("Original game type missing: " + name);
        MethodDefinition Method(string type, string name, string result, bool isStatic, params (string Name, string Type)[] args)
        {
            MethodDefinition[] found = Type(type).Methods.Where(m => m.Name == name &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(args.Select(p => p.Type))).ToArray();
            check(found.Length == 1, $"Original target must be uniquely declared: {type}.{name}");
            MethodDefinition method = found.Single();
            check(method.IsStatic == isStatic && method.ReturnType.FullName == result && method.HasBody,
                $"Original target staticness/return/body: {type}.{name}");
            foreach (var expected in args.Where(a => a.Name.Length != 0))
                check(method.Parameters.Any(p => p.Name == expected.Name && p.ParameterType.FullName == expected.Type),
                    $"Harmony named argument contract: {type}.{name}({expected.Name})");
            return method;
        }
        Method("ZNet", "Awake", "System.Void", false);
        Method("ZNet", "OnNewConnection", "System.Void", false, ("peer", "ZNetPeer"));
        Method("ZNet", "RPC_PeerInfo", "System.Void", false, ("rpc", "ZRpc"), ("", "ZPackage"));
        Method("ZNet", "Disconnect", "System.Void", false, ("peer", "ZNetPeer"));
        Method("ZNet", "StopAll", "System.Void", false, ("", "System.Boolean"));
        Method("ZNetScene", "Awake", "System.Void", false);
        Method("ZNetScene", "OnDestroy", "System.Void", false);
        check(Method("ZNet", "GetUID", "System.Int64", true).IsPublic, "Session UID must use the public original API.");
        check(Method("ZNet", "IsServer", "System.Boolean", false).IsPublic, "Authority gate must use public original IsServer.");
        check(Method("ZNet", "GetPeers", "System.Collections.Generic.List`1<ZNetPeer>", false).IsPublic, "Peer inventory must use public original API.");
        check(Method("ZNetPeer", "IsReady", "System.Boolean", false).IsPublic, "Peer readiness must use public original API.");
        check(Method("ZRpc", "Unregister", "System.Void", false, ("", "System.String")).IsPublic, "Own RPC unregister must be supported.");
        FieldDefinition functions = Type("ZRpc").Fields.Single(f => f.Name == "m_functions");
        check(functions.IsPrivate && !functions.IsStatic && functions.FieldType is GenericInstanceType dictionary &&
            dictionary.ElementType.FullName == "System.Collections.Generic.Dictionary`2" &&
            dictionary.GenericArguments[0].FullName == "System.Int32",
            "Private original ZRpc handler dictionary must match cached reflection access; never assume publicized visibility.");
        foreach ((string name, string type) in new[] { ("m_rpc", "ZRpc"), ("m_uid", "System.Int64"), ("m_server", "System.Boolean") })
            check(Type("ZNetPeer").Fields.Any(f => f.Name == name && f.FieldType.FullName == type && f.IsPublic && !f.IsStatic),
                "Original peer authority/member access: " + name);
        MethodDefinition[] registrations = Type("ZRpc").Methods.Where(m => m.Name == "Register" && m.GenericParameters.Count == 1 &&
            m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.String").ToArray();
        check(registrations.Length == 1 && registrations[0].IsPublic && !registrations[0].IsStatic &&
            registrations[0].ReturnType.FullName == "System.Void" &&
            registrations[0].Parameters[1].ParameterType is GenericInstanceType callback &&
            callback.ElementType.FullName == "System.Action`2" && callback.GenericArguments[0].FullName == "ZRpc" &&
            callback.GenericArguments[1] is GenericParameter,
            "Coordinator RPC registration must accept the real Action<ZRpc,T> callback contract.");
        Console.WriteLine("Original coordinator contracts checked: " + Path.GetFullPath(path));
    }
}
