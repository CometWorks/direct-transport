using VRage.GameServices;

namespace Shared.Transport;

// Wraps the UDP peer transport as an IMyNetworking so it can be registered
// with MyServiceManager and picked up by MyGameService.EnsureNetworking().
// Chat and Invite reuse the engine's built-in trivial implementations; only
// Peer2Peer carries gameplay traffic.
public sealed class UdpNetworking : IMyNetworking
{
    public string ServiceName { get; }
    public IMyPeer2Peer Peer2Peer { get; }
    public IMyNetworkingChat Chat { get; }
    public IMyNetworkingInvite Invite { get; }
    public string ProductName => "DirectTransport";

    public UdpNetworking(IMyGameService service, UdpPeer2Peer peer)
    {
        ServiceName = service.ServiceName;
        Peer2Peer = peer;
        Chat = new SimpleNetworkingChat(service, 512);
        Invite = new MyNullNetworkingInvite();
    }
}
