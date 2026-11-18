using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace InteropFixes;

// Small event-driven capability reports. This does not schedule gameplay packets,
// scan prefabs each frame, or delay/reject a connection because an optional mod failed.
internal static class CosmeticNetworkCoordinator
{
    // Optional installation uses its own readiness contract, independent of the
    // former mandatory ServerSync handshake. Old coordinators cannot grant v2 work.
    internal const int Protocol = 2;
    private const int AllMask = 127;
    private const int MaxParticipants = 128;
    private const string HelloRpc = InteropFixesPlugin.ModGuid + ".Cosmetics.Hello.v2";
    private const string ReportRpc = InteropFixesPlugin.ModGuid + ".Cosmetics.Report.v2";
    private const string GrantRpc = InteropFixesPlugin.ModGuid + ".Cosmetics.Grant.v2";
    private static readonly CosmeticAgreement Agreement = new();
    private static readonly Dictionary<ZRpc, Connection> Connections = new();
    private static readonly HashSet<long> Participants = new();
    private static readonly Dictionary<long, float> RecentlyLeft = new();
    private static FieldInfo? _rpcFunctions;
    private static Harmony? _harmony;
    private static ZNet? _net;
    private static ZNetScene? _scene;
    private static bool _installed;
    private static bool _sceneReady;
    private static int _localMask;
    private static int _clientGrant;
    private static long _session;
    private static long _lastGrantRevision;
    private static long _wireRevision;
    private static bool _warned;
    private static bool _transmissionFailed;

    private sealed class Connection
    {
        internal readonly ZNetPeer Peer;
        internal readonly Dictionary<string, object> Handlers = new();
        internal long Nonce;
        internal long ReportRevision;
        internal int LastReportMask = -1;
        internal bool LastReportReady;
        internal long PendingRevision;
        internal int PendingMask;
        internal bool PendingReady;
        internal Connection(ZNetPeer peer) { Peer = peer; }
    }

    internal static long SessionToken => _installed ? _session : 0;

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;
        _harmony = harmony;
        try
        {
            _rpcFunctions = AccessTools.DeclaredField(typeof(ZRpc), "m_functions");
            if (_rpcFunctions == null || !typeof(IDictionary).IsAssignableFrom(_rpcFunctions.FieldType))
                throw new MissingFieldException("ZRpc.m_functions");
            Patch(typeof(ZNet), "Awake", Type.EmptyTypes, null, nameof(NetworkAwake));
            Patch(typeof(ZNet), "OnNewConnection", new[] { typeof(ZNetPeer) }, nameof(BeforeNewConnection), nameof(AfterNewConnection));
            Patch(typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) }, null, nameof(AfterPeerInfo));
            Patch(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) }, nameof(BeforeDisconnect), null);
            Patch(typeof(ZNet), "StopAll", new[] { typeof(bool) }, nameof(NetworkStopping), null);
            Patch(typeof(ZNetScene), "Awake", Type.EmptyTypes, nameof(SceneStarting), nameof(SceneReady));
            Patch(typeof(ZNetScene), "OnDestroy", Type.EmptyTypes, nameof(SceneDestroying), null);
            _installed = true;
            if (ZNet.instance != null) NetworkAwake(ZNet.instance);
            if (ZNetScene.instance != null) SceneReady(ZNetScene.instance);
        }
        catch (Exception ex)
        {
            Stop();
            InteropFixesPlugin.ModLogger.LogWarning("Cosmetic capability negotiation is unavailable; original FX creation is retained. " + ex);
        }
    }

    private static void Patch(Type type, string name, Type[] parameters, string? prefix, string? postfix)
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, name, parameters)
            ?? throw new MissingMethodException(type.FullName, name);
        if (method.IsStatic || method.ReturnType != typeof(void)) throw new NotSupportedException(method.ToString());
        _harmony!.Patch(method,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(CosmeticNetworkCoordinator), prefix) { priority = Priority.First },
            postfix: postfix == null ? null : new HarmonyMethod(typeof(CosmeticNetworkCoordinator), postfix) { priority = Priority.Last });
    }

    internal static bool CanOptimize(int mask)
    {
        if (!_installed || _transmissionFailed ||
            mask <= 0 || (mask & ~AllMask) != 0 || _net == null ||
            !ReferenceEquals(_net, ZNet.instance) || !_sceneReady || !ReferenceEquals(_scene, ZNetScene.instance) ||
            (_localMask & mask) != mask || !InteropFixesPlugin.CosmeticRuntimeActive) return false;
        if (_net.IsServer()) return Connections.Count < MaxParticipants && (Agreement.Mask & mask) == mask;
        return _session != 0 && (_clientGrant & mask) == mask;
    }

    internal static bool IsParticipant(long sender)
    {
        if (!_installed || _net == null || _session == 0 || sender == 0) return false;
        if (sender == ZNet.GetUID() || Participants.Contains(sender)) return true;
        if (!RecentlyLeft.TryGetValue(sender, out float until)) return false;
        if (Time.realtimeSinceStartup <= until) return true;
        RecentlyLeft.Remove(sender);
        return false;
    }

    internal static void SetLocalCapabilities(int mask)
    {
        _localMask = mask >= 0 && (mask & ~AllMask) == 0 ? mask : 0;
        RefreshReadiness();
    }

    internal static void RefreshReadiness()
    {
        if (!_installed || _net == null) return;
        try
        {
            if (_net.IsServer())
            {
                Agreement.SetLocal(_localMask, !_transmissionFailed && _sceneReady && InteropFixesPlugin.CosmeticRuntimeActive);
                Publish();
            }
            else Report();
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void NetworkAwake(ZNet __instance)
    {
        if (!_installed) return;
        try
        {
            ResetSession();
            _net = __instance;
            if (__instance.IsServer())
            {
                _session = NewNonce();
                Agreement.Reset(_session);
            }
            _scene = ZNetScene.instance;
            _sceneReady = _scene != null;
            foreach (ZNetPeer peer in __instance.GetPeers())
            {
                BeforeNewConnection(__instance, peer);
                AfterNewConnection(peer);
            }
            RefreshReadiness();
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void BeforeNewConnection(ZNet __instance, ZNetPeer peer)
    {
        if (!_installed || peer?.m_rpc == null || Connections.ContainsKey(peer.m_rpc)) return;
        try
        {
            if (!ReferenceEquals(_net, __instance)) NetworkAwake(__instance);
            if (Connections.ContainsKey(peer.m_rpc)) return;
            var connection = new Connection(peer);
            Connections.Add(peer.m_rpc, connection);
            Register(connection, HelloRpc, ReceiveHello);
            Register(connection, ReportRpc, ReceiveReport);
            Register(connection, GrantRpc, ReceiveGrant);
            if (__instance.IsServer())
            {
                connection.Nonce = NewNonce();
                Agreement.AddPeer(connection.Nonce); // Unknown connections immediately remove TX permission.
                Publish();
            }
            else
            {
                _session = 0;
                _clientGrant = 0;
                _lastGrantRevision = 0;
                Participants.Clear();
                RecentlyLeft.Clear();
            }
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void AfterNewConnection(ZNetPeer peer)
    {
        if (!_installed || _net == null || !_net.IsServer() || !Connections.TryGetValue(peer.m_rpc, out Connection? connection)) return;
        try
        {
            ZPackage message = Header(connection);
            peer.m_rpc.Invoke(HelloRpc, message);
            Publish();
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void AfterPeerInfo(ZRpc rpc)
    {
        if (!_installed || _net == null || !Connections.TryGetValue(rpc, out Connection? connection) ||
            !connection.Peer.IsReady()) return;
        try
        {
            if (_net.IsServer() && connection.PendingRevision > 0)
            {
                Agreement.Report(connection.Nonce, _session, connection.PendingRevision,
                    connection.PendingMask, connection.PendingReady);
                connection.PendingRevision = 0;
            }
            RefreshReadiness();
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void BeforeDisconnect(ZNetPeer peer)
    {
        if (!_installed || peer?.m_rpc == null || !Connections.TryGetValue(peer.m_rpc, out Connection? connection)) return;
        Connections.Remove(peer.m_rpc);
        try { Unregister(connection); }
        catch (Exception ex) { DisableTransmission(ex); }
        try
        {
            if (_net != null && _net.IsServer())
            {
                Agreement.RemovePeer(connection.Nonce);
                Publish();
            }
            else
            {
                _session = 0;
                _clientGrant = 0;
                _lastGrantRevision = 0;
                Participants.Clear();
                RecentlyLeft.Clear();
            }
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void NetworkStopping()
    {
        if (_installed) ResetSession();
    }

    private static void SceneStarting(ZNetScene __instance)
    {
        if (!_installed) return;
        _scene = __instance;
        _sceneReady = false;
        _localMask = 0;
        RefreshReadiness();
    }

    private static void SceneReady(ZNetScene __instance)
    {
        if (!_installed) return;
        _scene = __instance;
        _sceneReady = true;
        RefreshReadiness();
    }

    private static void SceneDestroying(ZNetScene __instance)
    {
        if (!_installed || !ReferenceEquals(_scene, __instance)) return;
        _sceneReady = false;
        _localMask = 0;
        RefreshReadiness();
        _scene = null;
    }

    private static void ReceiveHello(ZRpc rpc, ZPackage message)
    {
        if (!_installed || _net == null || _net.IsServer() ||
            !Connections.TryGetValue(rpc, out Connection? connection) || !connection.Peer.m_server) return;
        try
        {
            int protocol = message.ReadInt();
            long session = message.ReadLong(), nonce = message.ReadLong();
            if (protocol != Protocol || session <= 0 || nonce <= 0 || message.GetPos() != message.Size()) return;
            if (connection.Nonce != 0 && (connection.Nonce != nonce || _session != session)) return;
            _session = session;
            connection.Nonce = nonce;
            Report();
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void Report()
    {
        if (_session == 0 || _net == null || _net.IsServer()) return;
        foreach (Connection connection in Connections.Values)
        {
            if (!connection.Peer.m_server || connection.Nonce == 0) continue;
            bool ready = !_transmissionFailed && _sceneReady && connection.Peer.IsReady() && InteropFixesPlugin.CosmeticRuntimeActive;
            int mask = ready ? _localMask : 0;
            if (connection.LastReportMask == mask && connection.LastReportReady == ready) continue;
            // Restrict immediately, before the server receives the lower local capability.
            _clientGrant &= mask;
            connection.LastReportMask = mask;
            connection.LastReportReady = ready;
            ZPackage message = Header(connection);
            message.Write(++connection.ReportRevision);
            message.Write(mask);
            message.Write(ready);
            connection.Peer.m_rpc.Invoke(ReportRpc, message);
        }
    }

    private static void ReceiveReport(ZRpc rpc, ZPackage message)
    {
        if (!_installed || _net == null || !_net.IsServer() ||
            !Connections.TryGetValue(rpc, out Connection? connection)) return;
        try
        {
            if (!ReadHeader(message, connection)) return;
            long revision = message.ReadLong();
            int mask = message.ReadInt();
            bool ready = message.ReadBool();
            if (message.GetPos() != message.Size() || revision <= connection.ReportRevision || mask < 0 || (mask & ~AllMask) != 0) return;
            connection.ReportRevision = revision;
            if (!connection.Peer.IsReady())
            {
                if (revision > connection.PendingRevision)
                {
                    connection.PendingRevision = revision;
                    connection.PendingMask = mask;
                    connection.PendingReady = ready;
                }
                return;
            }
            if (Agreement.Report(connection.Nonce, _session, revision, mask, ready)) Publish();
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static void ReceiveGrant(ZRpc rpc, ZPackage message)
    {
        if (!_installed || _net == null || _net.IsServer() ||
            !Connections.TryGetValue(rpc, out Connection? connection) || !connection.Peer.m_server) return;
        try
        {
            if (!ReadHeader(message, connection)) return;
            long revision = message.ReadLong();
            int mask = message.ReadInt(), count = message.ReadInt();
            if (revision <= _lastGrantRevision || revision <= 0 || mask < 0 || (mask & ~AllMask) != 0 || count < 1 || count > MaxParticipants) return;
            var participants = new HashSet<long>();
            for (int i = 0; i < count; i++)
            {
                long uid = message.ReadLong();
                if (uid == 0 || !participants.Add(uid)) return;
            }
            if (message.GetPos() != message.Size()) return;
            _lastGrantRevision = revision;
            _clientGrant = _transmissionFailed || !connection.Peer.IsReady() ? 0 : mask & _localMask;
            UpdateParticipants(participants);
        }
        catch (Exception ex) { DisableTransmission(ex); }
    }

    private static bool ReadHeader(ZPackage message, Connection connection) =>
        message.ReadInt() == Protocol && message.ReadLong() == _session &&
        message.ReadLong() == connection.Nonce && _session != 0 && connection.Nonce != 0;

    private static ZPackage Header(Connection connection)
    {
        var message = new ZPackage();
        message.Write(Protocol);
        message.Write(_session);
        message.Write(connection.Nonce);
        return message;
    }

    private static void Publish()
    {
        if (_net == null || !_net.IsServer() || _session == 0) return;
        var members = new HashSet<long> { ZNet.GetUID() };
        foreach (Connection connection in Connections.Values)
            if (connection.Peer.IsReady()) members.Add(connection.Peer.m_uid);
        UpdateParticipants(members);
        int mask = members.Count <= MaxParticipants ? Agreement.Mask : 0;
        if (members.Count > MaxParticipants)
        {
            // Keep the payload bounded; disable this optimization, never the connection.
            members.Clear();
            members.Add(ZNet.GetUID());
        }
        long revision = ++_wireRevision;
        Exception? sendFailure = null;
        foreach (Connection connection in Connections.Values)
        {
            // Silent/unmodded peers still veto Agreement, but never receive repeated
            // grants for an RPC they have not demonstrated support for.
            if (connection.Nonce == 0 || connection.ReportRevision == 0 || !connection.Peer.IsReady()) continue;
            try
            {
                ZPackage message = Header(connection);
                message.Write(revision);
                message.Write(mask);
                message.Write(members.Count);
                foreach (long uid in members) message.Write(uid);
                connection.Peer.m_rpc.Invoke(GrantRpc, message);
            }
            catch (Exception ex) { sendFailure ??= ex; }
        }
        if (sendFailure != null) throw new InvalidOperationException("A cosmetic grant could not be sent.", sendFailure);
    }

    private static void UpdateParticipants(HashSet<long> current)
    {
        float now = Time.realtimeSinceStartup;
        foreach (long old in Participants)
            if (!current.Contains(old)) RecentlyLeft[old] = now + 30f;
        if (RecentlyLeft.Count > MaxParticipants * 2) RecentlyLeft.Clear();
        Participants.Clear();
        foreach (long uid in current) Participants.Add(uid);
    }

    private static void Register(Connection connection, string name, Action<ZRpc, ZPackage> callback)
    {
        IDictionary functions = (IDictionary)_rpcFunctions!.GetValue(connection.Peer.m_rpc)!;
        int key = StableHash(name);
        if (functions.Contains(key)) throw new InvalidOperationException("Cosmetic RPC identifier already registered: " + name);
        connection.Peer.m_rpc.Register(name, callback);
        connection.Handlers.Add(name, functions[key]!);
    }

    private static void Unregister(Connection connection)
    {
        if (_rpcFunctions == null) return;
        IDictionary functions = (IDictionary)_rpcFunctions.GetValue(connection.Peer.m_rpc)!;
        foreach (var handler in connection.Handlers)
            if (ReferenceEquals(functions[StableHash(handler.Key)], handler.Value)) connection.Peer.m_rpc.Unregister(handler.Key);
        connection.Handlers.Clear();
    }

    private static void ResetSession()
    {
        foreach (Connection connection in Connections.Values)
        {
            try { Unregister(connection); }
            catch (Exception ex) { InteropFixesPlugin.ModLogger.LogWarning("Could not remove own cosmetic RPC: " + ex.Message); }
        }
        Connections.Clear();
        Participants.Clear();
        RecentlyLeft.Clear();
        _net = null;
        _session = 0;
        _clientGrant = 0;
        _lastGrantRevision = 0;
        _wireRevision = 0;
        _warned = false;
        _transmissionFailed = false;
    }

    private static void DisableTransmission(Exception error)
    {
        _transmissionFailed = true;
        _clientGrant = 0;
        try
        {
            if (_net != null && _net.IsServer())
            {
                Agreement.SetLocal(0, false);
                Publish();
            }
            else if (_net != null) Report();
        }
        catch { /* A failing cosmetic notification must not stop native connection cleanup. */ }
        if (_warned) return;
        _warned = true;
        InteropFixesPlugin.ModLogger.LogWarning("Cosmetic negotiation failed; new local FX optimization is disabled. " + error);
    }

    internal static void Stop()
    {
        _installed = false;
        ResetSession();
        _scene = null;
        _sceneReady = false;
        _localMask = 0;
        if (_harmony != null && ModuleIsolation.TryUnpatch(_harmony, "Cosmetic capability agreement")) _harmony = null;
    }

    private static long NewNonce()
    {
        long value = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue;
        return value == 0 ? 1 : value;
    }

    private static int StableHash(string text)
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
