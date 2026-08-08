using System;
using System.Threading;
using PluginSdk.Clustering;
using Shared.Transport;

namespace ServerPlugin;

internal sealed class ClusterNodeLinkAdapter : IClusterNodeLink, IDisposable
{
    private UdpPeer2Peer m_peer;
    private Func<ulong, byte[], bool> m_attachValidator;

    public ClusterNodeLinkAdapter()
    {
        DirectTransport.PeerCreated += Attach;
        if (DirectTransport.Peer != null)
            Attach(DirectTransport.Peer);
    }

    public bool IsConnected => Volatile.Read(ref m_peer)?.IsNodeLinkConnected == true;

    public event Action<byte[]> MessageReceived;
    public event Action<bool> ConnectionChanged;
    public event Action<ulong> ClientDetached;

    public Func<ulong, byte[], bool> AttachValidator
    {
        get => m_attachValidator;
        set
        {
            m_attachValidator = value;
            UdpPeer2Peer peer = Volatile.Read(ref m_peer);
            if (peer != null)
                peer.NodeLinkAttachValidator = value;
        }
    }

    public bool Send(byte[] payload) =>
        Volatile.Read(ref m_peer)?.SendNodeLinkMessage(payload) == true;

    public void Dispose()
    {
        DirectTransport.PeerCreated -= Attach;
        UdpPeer2Peer peer = Interlocked.Exchange(ref m_peer, null);
        if (peer == null)
            return;
        peer.NodeLinkMessageReceived -= OnMessageReceived;
        peer.NodeLinkConnectionChanged -= OnConnectionChanged;
        peer.NodeLinkClientDetached -= OnClientDetached;
        peer.NodeLinkAttachValidator = null;
    }

    private void Attach(UdpPeer2Peer peer)
    {
        if (Interlocked.CompareExchange(ref m_peer, peer, null) != null)
            return;
        peer.NodeLinkAttachValidator = m_attachValidator;
        peer.NodeLinkMessageReceived += OnMessageReceived;
        peer.NodeLinkConnectionChanged += OnConnectionChanged;
        peer.NodeLinkClientDetached += OnClientDetached;
    }

    private void OnMessageReceived(byte[] payload) => MessageReceived?.Invoke(payload);
    private void OnConnectionChanged(bool connected) => ConnectionChanged?.Invoke(connected);
    private void OnClientDetached(ulong clientId) => ClientDetached?.Invoke(clientId);
}
