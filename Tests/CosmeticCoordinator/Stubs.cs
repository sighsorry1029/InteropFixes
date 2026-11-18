using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BepInEx { internal static class Placeholder { } }
namespace HarmonyLib
{
    public sealed class Harmony
    {
        public readonly string Id;
        public Harmony(string id) { Id = id; }
        public void Patch(MethodInfo method, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null) { }
    }
    public sealed class HarmonyMethod { public int priority; public HarmonyMethod(Type type, string name) { } }
    public static class Priority { public const int First = 800, Last = 0; }
    public static class AccessTools
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        public static FieldInfo? DeclaredField(Type type, string name) => type.GetField(name, Flags);
        public static MethodInfo? DeclaredMethod(Type type, string name, Type[] args) => type.GetMethod(name, Flags, null, args, null);
    }
}
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
namespace InteropFixes
{
    internal static class InteropFixesPlugin
    {
        internal const string ModGuid = "sighsorry.InteropFixes";
        internal static bool CosmeticRuntimeActive = true;
        internal static readonly StubLogger ModLogger = new();
    }
    internal sealed class StubLogger { internal readonly List<string> Messages = new(); public void LogWarning(object text) => Messages.Add(text.ToString()!); }
    internal static class ModuleIsolation { internal static bool TryUnpatch(HarmonyLib.Harmony harmony, string name) => true; }
}

public sealed class ZPackage
{
    private readonly MemoryStream _stream;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    public ZPackage() : this(Array.Empty<byte>()) { }
    public ZPackage(byte[] bytes)
    {
        _stream = new MemoryStream(); _stream.Write(bytes, 0, bytes.Length); _stream.Position = 0;
        _reader = new BinaryReader(_stream); _writer = new BinaryWriter(_stream);
    }
    public void Write(int value) => _writer.Write(value);
    public void Write(long value) => _writer.Write(value);
    public void Write(bool value) => _writer.Write(value);
    public int ReadInt() => _reader.ReadInt32();
    public long ReadLong() => _reader.ReadInt64();
    public bool ReadBool() => _reader.ReadBoolean();
    public int GetPos() => checked((int)_stream.Position);
    public int Size() => checked((int)_stream.Length);
    public byte[] Bytes() => _stream.ToArray();
}

public sealed class ZRpc
{
    private readonly Dictionary<int, Handler> m_functions = new();
    public readonly List<(string Name, byte[] Data)> Outbox = new();
    public bool FailInvoke, FailUnregister;
    public sealed class Handler { public readonly Action<ZRpc, ZPackage> Callback; public Handler(Action<ZRpc, ZPackage> callback) { Callback = callback; } }
    public void Register(string name, Action<ZRpc, ZPackage> callback) => m_functions[Hash(name)] = new Handler(callback);
    public void Unregister(string name)
    {
        if (FailUnregister) throw new IOException("Simulated handler cleanup failure");
        m_functions.Remove(Hash(name));
    }
    public void Invoke(string name, ZPackage package)
    {
        if (FailInvoke) throw new IOException("Simulated send failure");
        Outbox.Add((name, package.Bytes()));
    }
    public int Deliver(string name, byte[] bytes)
    {
        var package = new ZPackage(bytes);
        if (m_functions.TryGetValue(Hash(name), out Handler? handler)) handler.Callback(this, package);
        return package.GetPos();
    }
    public bool Has(string name) => m_functions.ContainsKey(Hash(name));
    public int HandlerCount => m_functions.Count;
    public static int Hash(string text)
    {
        unchecked
        {
            int first = 5381, second = first;
            for (int i = 0; i < text.Length && text[i] != '\0'; i += 2)
            {
                first = ((first << 5) + first) ^ text[i];
                if (i == text.Length - 1 || text[i + 1] == '\0') break;
                second = ((second << 5) + second) ^ text[i + 1];
            }
            return first + second * 1566083941;
        }
    }
}

public sealed class ZNetPeer
{
    public readonly ZRpc m_rpc = new();
    public long m_uid;
    public bool m_server;
    public bool IsReady() => m_uid != 0;
}
public sealed class ZNet
{
    public static ZNet? instance;
    public static long UID;
    public bool Server;
    public int DisconnectCalls;
    public readonly List<ZNetPeer> Peers = new();
    public bool IsServer() => Server;
    public static long GetUID() => UID;
    public List<ZNetPeer> GetPeers() => Peers;
    private void Awake() { }
    private void OnNewConnection(ZNetPeer peer) { }
    private void RPC_PeerInfo(ZRpc rpc, ZPackage package) { }
    public void Disconnect(ZNetPeer peer) { DisconnectCalls++; Peers.Remove(peer); }
    private void StopAll(bool save) { }
}
public sealed class ZNetScene
{
    public static ZNetScene? instance;
    private void Awake() { }
    private void OnDestroy() { }
}
