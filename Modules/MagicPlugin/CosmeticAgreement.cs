using System;
using System.Collections.Generic;

namespace InteropFixes;

// Cached server decision about currently known peers. This is not a network barrier:
// a permission reduction takes effect on a remote sender only after it receives it.
internal sealed class CosmeticAgreement
{
    internal const int AllMask = 127;

    private sealed class Peer
    {
        internal long ReportRevision;
        internal int Mask;
        internal bool Ready;
    }

    private readonly Dictionary<long, Peer> _peers = new();
    internal long Session { get; private set; }
    internal long Revision { get; private set; }
    internal int LocalMask { get; private set; }
    internal bool LocalReady { get; private set; }
    internal int Mask { get; private set; }

    internal void Reset(long session)
    {
        if (session <= 0) throw new ArgumentOutOfRangeException(nameof(session));
        // Reusing the same session must not rewind the revision accepted by clients.
        long revision = Session == session ? checked(Revision + 1) : 1;
        Session = session;
        Revision = revision;
        _peers.Clear();
        LocalMask = 0;
        LocalReady = false;
        Mask = 0;
    }

    internal void SetLocal(int mask, bool ready)
    {
        if (!ValidMask(mask))
        {
            mask = 0;
            ready = false;
        }
        if (LocalMask == mask && LocalReady == ready) return;
        LocalMask = mask;
        LocalReady = ready;
        Recompute();
    }

    // Tokens identify connection instances, not player UIDs. The adapter must allocate
    // a fresh positive token after reconnect and drop reports from a removed ZRpc.
    internal void AddPeer(long token)
    {
        if (token <= 0 || _peers.ContainsKey(token)) return;
        _peers.Add(token, new Peer());
        Recompute();
    }

    internal void RemovePeer(long token)
    {
        if (_peers.Remove(token)) Recompute();
    }

    internal bool Report(long token, long session, long reportRevision, int mask, bool ready)
    {
        if (session <= 0 || session != Session || reportRevision <= 0 || !ValidMask(mask) ||
            !_peers.TryGetValue(token, out Peer? peer) || reportRevision <= peer.ReportRevision)
            return false;
        peer.ReportRevision = reportRevision;
        peer.Mask = mask;
        peer.Ready = ready;
        Recompute();
        return true;
    }

    private void Recompute()
    {
        int agreed = Session > 0 && LocalReady ? LocalMask : 0;
        foreach (Peer peer in _peers.Values)
        {
            if (peer.ReportRevision == 0 || !peer.Ready)
            {
                agreed = 0;
                break;
            }
            agreed &= peer.Mask;
        }
        // Compute only on membership/capability/readiness changes, never during effect creation.
        Mask = agreed;
        Revision = checked(Revision + 1);
    }

    private static bool ValidMask(int mask) => mask >= 0 && (mask & ~AllMask) == 0;
}
