using System;
using System.Buffers.Binary;
using System.Text;

namespace Shared.Transport;

internal readonly record struct NodeLifecycleWireRequest(
    Guid RequestId, byte Kind, byte Origin, bool SaveFirst, ulong CallerId, string Reason);

internal readonly record struct NodeLifecycleWireAck(
    Guid RequestId, byte Disposition, Guid OperationId, string ReasonCode, string Message, string NodeState);

internal static class NodeLifecycleWireCodec
{
    public static byte[] WriteRequest(NodeLifecycleWireRequest request)
    {
        if (request.RequestId == Guid.Empty || request.Kind > 1 || request.Origin > 1)
            throw new InvalidOperationException("Lifecycle request is invalid");
        byte[] reason = Encoding.UTF8.GetBytes(request.Reason ?? string.Empty);
        if (reason.Length > 2048)
            throw new InvalidOperationException("Lifecycle request reason is too large");

        byte[] payload = new byte[30 + reason.Length];
        payload[0] = 1;
        request.RequestId.TryWriteBytes(payload.AsSpan(1, 16));
        payload[17] = request.Kind;
        payload[18] = request.Origin;
        payload[19] = request.SaveFirst ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20, 8), request.CallerId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(28, 2), checked((ushort)reason.Length));
        reason.CopyTo(payload, 30);
        return payload;
    }

    public static NodeLifecycleWireRequest ReadRequest(byte[] payload)
    {
        if (payload == null || payload.Length < 30 || payload[0] != 1)
            throw new InvalidOperationException("Lifecycle request is truncated or has an unsupported version");
        var requestId = new Guid(payload.AsSpan(1, 16));
        byte kind = payload[17], origin = payload[18], saveFirst = payload[19];
        int reasonLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(28, 2));
        if (requestId == Guid.Empty || kind > 1 || origin > 1 || saveFirst > 1
            || payload.Length != 30 + reasonLength)
            throw new InvalidOperationException("Lifecycle request is invalid");
        return new NodeLifecycleWireRequest(requestId, kind, origin, saveFirst != 0,
            BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(20, 8)),
            Encoding.UTF8.GetString(payload, 30, reasonLength));
    }

    public static byte[] WriteAck(NodeLifecycleWireAck acknowledgement)
    {
        if (acknowledgement.RequestId == Guid.Empty || acknowledgement.Disposition > 2)
            throw new InvalidOperationException("Lifecycle acknowledgement is invalid");
        byte[] reasonCode = EncodeString(acknowledgement.ReasonCode, 128, "reason code");
        byte[] message = EncodeString(acknowledgement.Message, 1024, "message");
        byte[] nodeState = EncodeString(acknowledgement.NodeState, 128, "node state");
        byte[] payload = new byte[40 + reasonCode.Length + message.Length + nodeState.Length];
        payload[0] = 1;
        acknowledgement.RequestId.TryWriteBytes(payload.AsSpan(1, 16));
        payload[17] = acknowledgement.Disposition;
        acknowledgement.OperationId.TryWriteBytes(payload.AsSpan(18, 16));
        WriteString(payload, 34, reasonCode, out int next);
        WriteString(payload, next, message, out next);
        WriteString(payload, next, nodeState, out _);
        return payload;
    }

    public static NodeLifecycleWireAck ReadAck(byte[] payload)
    {
        if (payload == null || payload.Length < 40 || payload[0] != 1)
            throw new InvalidOperationException("Lifecycle acknowledgement is truncated or has an unsupported version");
        var requestId = new Guid(payload.AsSpan(1, 16));
        byte disposition = payload[17];
        if (requestId == Guid.Empty || disposition > 2)
            throw new InvalidOperationException("Lifecycle acknowledgement is invalid");
        int offset = 34;
        string reasonCode = ReadString(payload, ref offset);
        string message = ReadString(payload, ref offset);
        string nodeState = ReadString(payload, ref offset);
        if (offset != payload.Length)
            throw new InvalidOperationException("Lifecycle acknowledgement has trailing data");
        return new NodeLifecycleWireAck(requestId, disposition,
            new Guid(payload.AsSpan(18, 16)), reasonCode, message, nodeState);
    }

    private static byte[] EncodeString(string value, int limit, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > limit)
            throw new InvalidOperationException($"Lifecycle {name} is too large");
        return bytes;
    }

    private static void WriteString(byte[] destination, int offset, byte[] value, out int next)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, 2), checked((ushort)value.Length));
        value.CopyTo(destination, offset + 2);
        next = offset + 2 + value.Length;
    }

    private static string ReadString(byte[] payload, ref int offset)
    {
        if (offset > payload.Length - 2)
            throw new InvalidOperationException("Lifecycle string length is truncated");
        int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        offset += 2;
        if (offset > payload.Length - length)
            throw new InvalidOperationException("Lifecycle string is truncated");
        string value = Encoding.UTF8.GetString(payload, offset, length);
        offset += length;
        return value;
    }
}
