using System.Buffers.Binary;
using MouseMotionLab.Capture.Interop;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Capture.Tests;

public sealed class RawInputParserTests
{
    [Fact]
    public void ParsesX64MousePacket()
    {
        Assert.Equal(24, RawInputParser.HeaderSize);
        Assert.Equal(24, RawInputParser.MousePayloadSize);
        var packet = CreatePacket(
            type: 0,
            device: 0x1234,
            dx: 17,
            dy: -9,
            buttonFlags: (ushort)(MouseButtonFlags.LeftDown | MouseButtonFlags.Wheel),
            buttonData: unchecked((ushort)-120));

        var parsed = RawInputParser.ParseMouseInput(packet, 99_001, handle => $"device-{handle:X}");

        Assert.NotNull(parsed);
        Assert.Equal(17, parsed.Dx);
        Assert.Equal(-9, parsed.Dy);
        Assert.Equal(-120, parsed.WheelDelta);
        Assert.True(parsed.HasLeftDown);
        Assert.Equal("device-1234", parsed.DeviceId);
        Assert.Equal(99_001, parsed.QpcTicks);
    }

    [Fact]
    public void IgnoresNonMousePacket()
    {
        var packet = CreatePacket(type: 1, device: 1, dx: 0, dy: 0, buttonFlags: 0, buttonData: 0);

        Assert.Null(RawInputParser.ParseMouseInput(packet, 1, _ => "keyboard"));
    }

    [Fact]
    public void RejectsTruncatedPacket()
    {
        var packet = new byte[RawInputParser.HeaderSize + RawInputParser.MousePayloadSize - 1];

        Assert.Throws<InvalidDataException>(() => RawInputParser.ParseMouseInput(packet, 1, _ => "mouse"));
    }

    private static byte[] CreatePacket(
        uint type,
        long device,
        int dx,
        int dy,
        ushort buttonFlags,
        ushort buttonData)
    {
        var packet = new byte[RawInputParser.HeaderSize + RawInputParser.MousePayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), (uint)packet.Length);
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8, 8), device);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(RawInputParser.HeaderSize + 4, 2), buttonFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(RawInputParser.HeaderSize + 6, 2), buttonData);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(RawInputParser.HeaderSize + 12, 4), dx);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(RawInputParser.HeaderSize + 16, 4), dy);
        return packet;
    }
}
