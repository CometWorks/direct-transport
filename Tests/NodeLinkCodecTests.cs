using System;
using LiteNetLib;
using Shared.Transport;
using Xunit;

namespace DirectTransport.Tests;

public sealed class NodeLinkCodecTests
{
    [Fact]
    public void Relay_round_trips_without_changing_payload()
    {
        byte[] payload = { 1, 2, 3, 4 };

        NodeLinkPacket packet = NodeLinkCodec.Read(
            NodeLinkCodec.Write(NodeLinkMessage.Relay, 42, 7, payload, payload.Length));

        Assert.Equal(NodeLinkMessage.Relay, packet.Kind);
        Assert.Equal(42UL, packet.ClientId);
        Assert.Equal(7, packet.Channel);
        Assert.Equal(payload, packet.Payload);
    }

    [Fact]
    public void Invalid_control_and_global_packets_are_rejected()
    {
        byte[] trailingAck = new byte[10];
        trailingAck[0] = (byte)NodeLinkMessage.AttachAck;

        Assert.Throws<InvalidOperationException>(() => NodeLinkCodec.Read(trailingAck));
        Assert.Throws<InvalidOperationException>(() =>
            NodeLinkCodec.Read(NodeLinkCodec.Write(NodeLinkMessage.Global, 0)));
    }

    [Fact]
    public void Relays_wait_for_credit()
    {
        var state = new NodeLinkState<object>();
        var peer = new object();
        var relay = new PendingRelay(new byte[20], 1, DeliveryMethod.ReliableOrdered);

        Assert.True(state.TryConnect(peer));
        Assert.True(state.Attach(42));
        Assert.True(state.SendOrQueue(42, relay, out PendingRelay immediate));
        Assert.Null(immediate.Data);
        Assert.Empty(state.GrantCredit(42, 19));
        Assert.Single(state.GrantCredit(42, 1));
    }
}
