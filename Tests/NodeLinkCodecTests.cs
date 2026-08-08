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
    public void Lifecycle_request_and_ack_match_the_gateway_wire_contract()
    {
        var request = new NodeLifecycleWireRequest(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 1, 1, true, 42, "restart");
        byte[] payload = NodeLifecycleWireCodec.WriteRequest(request);

        Assert.Equal(new byte[]
        {
            1, 0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
            1, 1, 1, 42, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0x72, 0x65, 0x73, 0x74, 0x61, 0x72, 0x74,
        }, payload);
        Assert.Equal(request, NodeLifecycleWireCodec.ReadRequest(payload));

        NodeLinkPacket envelope = NodeLinkCodec.Read(NodeLinkCodec.Write(
            NodeLinkMessage.LifecycleRequest, 0, payload: payload, payloadLength: payload.Length));
        Assert.Equal(NodeLinkMessage.LifecycleRequest, envelope.Kind);
        Assert.Equal(payload, envelope.Payload);

        var acknowledgement = new NodeLifecycleWireAck(request.RequestId, 0, request.RequestId,
            "node_restart_accepted", "Accepted.", "Closed");
        Assert.Equal(acknowledgement,
            NodeLifecycleWireCodec.ReadAck(NodeLifecycleWireCodec.WriteAck(acknowledgement)));
        Assert.Throws<InvalidOperationException>(() =>
            NodeLifecycleWireCodec.ReadRequest(payload[..^1]));
        Assert.Throws<InvalidOperationException>(() =>
            NodeLifecycleWireCodec.ReadAck([1, 2, 3]));
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
