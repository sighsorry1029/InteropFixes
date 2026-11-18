// Historical mandatory-ServerSync artifact checks. For the current optional-install
// DLL use Tests/OptionalNetwork; this program is retained only with its old results.
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length > 0 && args[0] == "--original-only")
{
    int originalChecks = 0;
    foreach (string path in args.Skip(1)) CoordinatorOriginalContracts.Verify(path, (condition, message) =>
    {
        if (!condition) throw new InvalidOperationException(message);
        originalChecks++;
    });
    if (originalChecks == 0) throw new ArgumentException("Provide at least one original assembly_valheim.dll.");
    Console.WriteLine($"PASS: {originalChecks} original coordinator target, injected-argument and RPC contracts. Metadata only.");
    return;
}
if (args.Length < 2 || args.Length > 4) throw new ArgumentException("Arguments: final InteropFixes.dll vendored ServerSync.dll [original client assembly_valheim.dll] [original server assembly_valheim.dll]");
string expected = "B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846";
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))) == expected, "Pinned ServerSync provenance checksum.");
using AssemblyDefinition merged = AssemblyDefinition.ReadAssembly(args[0]);
using AssemblyDefinition vendor = AssemblyDefinition.ReadAssembly(args[1]);
Check(!merged.MainModule.AssemblyReferences.Any(a => a.Name == "ServerSync"), "Final plugin must not depend on a standalone ServerSync.dll.");
TypeDefinition sync = merged.MainModule.GetType("ServerSync.ConfigSync") ?? throw new InvalidOperationException("ServerSync missing from final DLL.");
Check(!sync.IsPublic, "Merged ServerSync implementation must be internalized.");
TypeDefinition plugin = merged.MainModule.GetType("InteropFixes.InteropFixesPlugin")!;
MethodDefinition constructor = plugin.Methods.Single(m => m.Name == ".cctor");
bool Calls(MethodDefinition method, string type, string name) => method.HasBody && method.Body.Instructions.Any(i =>
    i.Operand is MethodReference called && called.DeclaringType.FullName == type && called.Name == name);
Instruction? Previous(Instruction? instruction)
{
    while (instruction?.Previous is { } previous)
    {
        instruction = previous;
        if (instruction.OpCode != OpCodes.Nop) return instruction;
    }
    return null;
}
Check(Calls(constructor, "ServerSync.ConfigSync", ".ctor"), "Plugin must initialize the merged ConfigSync.");
Check(constructor.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "ServerSync.ConfigSync" &&
    m.Name == "set_IsLocked" && i.Previous?.OpCode == OpCodes.Ldc_I4_1), "Synchronized policy must be server locked.");
Check(constructor.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "ModRequired" &&
      i.Previous?.OpCode == OpCodes.Ldc_I4_1), "ServerSync must require this mod on connecting peers.");
foreach (string field in new[] { "CurrentVersion", "MinimumRequiredVersion" })
    Check(constructor.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == field &&
        i.Previous?.OpCode == OpCodes.Ldstr && Equals(i.Previous.Operand, "1.0.3")), "Version handshake must use existing ModVersion: " + field);
MethodDefinition awake = plugin.Methods.Single(m => m.Name == "Awake");
Check(!Calls(awake, "ServerSync.ConfigSync", "AddConfigEntry") &&
    !plugin.Methods.Any(m => Calls(m, "BepInEx.Configuration.ConfigFile", "Bind")),
    "Automatic cosmetics must not register a visible or hidden option.");
Check(!plugin.Fields.Any(f => f.FieldType.FullName.StartsWith("BepInEx.Configuration.ConfigEntry", StringComparison.Ordinal)),
    "The removed setting must not survive as a cached ConfigEntry.");
Check(!Calls(awake, "ZNet", "IsServer") && !Calls(awake, "UnityEngine.Application", "get_isBatchMode") &&
    !Calls(awake, "UnityEngine.SystemInfo", "get_graphicsDeviceType"),
    "Required synchronization setup must not be limited to clients or dedicated servers.");
Check(!plugin.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i =>
    i.Operand is MethodReference m && m.Name is "add_SettingChanged" or "remove_SettingChanged"),
    "Removed configuration event plumbing must be absent.");
MethodDefinition active = plugin.Methods.Single(m => m.Name == "get_CosmeticRuntimeActive");
Check(active.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_stopping") &&
    active.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_instance"),
    "Automatic policy must retain plugin lifetime checks.");
MethodDefinition readiness = plugin.Methods.Single(m => m.Name == "get_CosmeticSessionReady");
Check(Calls(readiness, "ZNet", "IsServer") && Calls(readiness, "ServerSync.ConfigSync", "get_InitialSyncDone") &&
    Calls(readiness, "ServerSync.ConfigSync", "get_IsSourceOfTruth"), "Readiness must distinguish real server authority from completed client sync.");
Check(Calls(readiness, plugin.FullName, "get_CosmeticRuntimeActive"), "Session readiness must retain plugin lifetime checks.");

IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
{
    foreach (TypeDefinition type in types)
    {
        yield return type;
        foreach (TypeDefinition nested in Types(type.NestedTypes)) yield return nested;
    }
}
var syncTypes = Types(merged.MainModule.Types).Where(t => t.Namespace == "ServerSync" || t.FullName.StartsWith("ServerSync.")).ToArray();
var allMethods = Types(merged.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
Check(!allMethods.SelectMany(m => m.Body.Instructions).Any(i => i.OpCode == OpCodes.Ldstr &&
    Equals(i.Operand, "EnableNetworkCosmeticOptimization")), "Retired cosmetic option key must not be read anywhere in the final DLL.");
var receiverInstallCalls = allMethods.SelectMany(method => method.Body.Instructions
    .Where(i => i.Operand is MethodReference called && called.DeclaringType.FullName == "InteropFixes.MagicPluginCompatibility" && called.Name == "TryInstall")
    .Select(call => (Method: method, Call: call))).ToArray();
Check(receiverInstallCalls.Length == 1 && Previous(receiverInstallCalls[0].Call)?.OpCode == OpCodes.Ldc_I4_1,
    "Magic integration must always prepare cosmetic receivers, including when new-work eligibility is unavailable.");
Check(!receiverInstallCalls[0].Method.Body.Instructions.Any(i => i.Operand is FieldReference field && field.Name == "_networkCosmetics"),
    "Receiver installation must not reference a retired configuration field.");
Check(allMethods.Any(m => m.Name.StartsWith("<Awake>", StringComparison.Ordinal) &&
    Calls(m, "InteropFixes.CosmeticNetworkCoordinator", "Install")),
    "Coordinator installation must be reachable from early plugin Awake, before delayed Magic preparation.");
var methods = syncTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
Check(!methods.SelectMany(m => m.Body.Instructions).Any(i => i.Operand is FieldReference f &&
    f.DeclaringType.FullName == "ZRoutedRpc" && f.Name == "Everybody"), "Merged fixed baseline must not reference removed Everybody fields.");
Check(methods.Count(m => Calls(m, "ZNet", "IsAdmin")) >= 3, "Merged baseline must retain public administrator checks.");
MethodDefinition send = methods.Single(m => m.Name == "Send" && m.DeclaringType.Name == "BufferingSocket");
foreach (string message in new[] { "PeerInfo", "PlayerList", "HistoricalPlayerList", "AdminList", "NetTime" })
    Check(send.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, message)), "Buffered join message contract missing: " + message);
foreach (string member in new[] { "get_InitialSyncDone", "get_IsSourceOfTruth", "RPC_FromServerConfigSync", "HandleConfigSyncRPC" })
    Check(sync.Methods.Any(m => m.Name == member), "Required synchronization contract missing: " + member);

// Compare the fixed core bodies after normalizing metadata operands, excluding tokens,
// offsets and assembly identities that legitimately change when internalized.
string Normalize(MethodDefinition method) => string.Join("\n", method.Body.Instructions.Select(i => i.OpCode.Name + " " +
    (i.Operand switch {
        MethodReference m => m.FullName,
        FieldReference f => f.FullName,
        TypeReference t => t.FullName,
        Instruction target => "branch:" + method.Body.Instructions.IndexOf(target),
        Instruction[] targets => "switch:" + string.Join(",", targets.Select(t => method.Body.Instructions.IndexOf(t))),
        _ => i.Operand?.ToString() ?? ""
    })));
TypeDefinition vendorSync = vendor.MainModule.GetType("ServerSync.ConfigSync");
foreach (string methodName in new[] { "RPC_FromServerConfigSync", "HandleConfigSyncRPC", "ReadConfigsFromPackage" })
{
    MethodDefinition expectedMethod = vendorSync.Methods.Single(m => m.Name == methodName);
    MethodDefinition actualMethod = sync.Methods.Single(m => m.Name == methodName);
    Check(Normalize(actualMethod) == Normalize(expectedMethod), "Fixed ServerSync body changed during merge: " + methodName);
}
TypeDefinition versionCheck = merged.MainModule.GetType("ServerSync.VersionCheck");
TypeDefinition vendorVersionCheck = vendor.MainModule.GetType("ServerSync.VersionCheck");
foreach (string methodName in new[] { "Initialize", "IsVersionOk", "GetFailedClient", "GetFailedServer", "RPC_PeerInfo", "RegisterAndCheckVersion" })
    Check(Normalize(versionCheck.Methods.Single(m => m.Name == methodName)) ==
        Normalize(vendorVersionCheck.Methods.Single(m => m.Name == methodName)),
        "Mandatory peer/client handshake must retain the reviewed fixed implementation: " + methodName);
MethodDefinition versionPeerInfo = versionCheck.Methods.Single(m => m.Name == "RPC_PeerInfo");
Check(Calls(versionPeerInfo, "ServerSync.VersionCheck", "GetFailedClient") && Calls(versionPeerInfo, "ServerSync.VersionCheck", "GetFailedServer") &&
    Calls(versionPeerInfo, "ServerSync.VersionCheck", "Logout") && Calls(versionPeerInfo, "ServerSync.VersionCheck", "DisconnectClient"),
    "Required version validation must retain both client rejection and server rejection paths.");

TypeDefinition coordinator = merged.MainModule.GetType("InteropFixes.CosmeticNetworkCoordinator");
MethodDefinition coordinatorInstall = coordinator.Methods.Single(m => m.Name == "Install");
Check(coordinatorInstall.Body.Instructions.Count(i => i.Operand is MethodReference m &&
    m.DeclaringType.FullName == coordinator.FullName && m.Name == "Patch") == 8,
    "Coordinator target inventory changed; review original contracts before updating this assertion.");
foreach (string target in new[] { "Awake", "OnNewConnection", "RPC_PeerInfo", "Disconnect", "StopAll", "OnDestroy", "RPC_FromServerConfigSync" })
    Check(coordinatorInstall.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, target)),
        "Expected coordinator lifecycle target missing: " + target);
MethodDefinition initialSync = sync.Methods.Single(m => m.Name == "RPC_FromServerConfigSync");
Check(initialSync.IsPrivate && initialSync.ReturnType.FullName == "System.Void" &&
    initialSync.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(new[] { "ZRpc", "ZPackage" }),
    "Initial-sync hook must target the preserved private instance contract.");
MethodDefinition afterSync = coordinator.Methods.Single(m => m.Name == "AfterInitialSync");
Check(afterSync.Parameters.Select(p => (p.Name, p.ParameterType.FullName)).SequenceEqual(new[] {
    ("__instance", "ServerSync.ConfigSync"), ("rpc", "ZRpc"), ("__runOriginal", "System.Boolean") }),
    "Initial-sync postfix must receive the owning ConfigSync, actual RPC and original-run flag.");
Check(Calls(afterSync, coordinator.FullName, "RefreshReadiness") && Calls(afterSync, plugin.FullName, "get_CosmeticSessionReady"),
    "Completed initial synchronization must refresh readiness without a configuration change event.");
string clientPath = args.Length > 2 ? args[2] : @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll";
CoordinatorOriginalContracts.Verify(clientPath, Check);
if (args.Length > 3) CoordinatorOriginalContracts.Verify(args[3], Check);
Console.WriteLine($"PASS: {checks} final merged ServerSync provenance, authority/version and core IL contracts. Static inspection only; no game/network execution.");
