using System.Buffers.Binary;
using System.IO;

namespace Insta360StudioWireless.Services;

public static class Insta360Protocol
{
    // SYNC handshake packet: 0x06, 0x00, 0x00 + "syNceNdinS"
    public static readonly byte[] PktSync = new byte[]
    {
        0x06, 0x00, 0x00,
        (byte)'s', (byte)'y', (byte)'N', (byte)'c', (byte)'e', (byte)'N', (byte)'d', (byte)'i', (byte)'n', (byte)'S'
    };

    // Keepalive packet: 0x05, 0x00, 0x00 (sent every 2 seconds to keep Wi-Fi and HTTP active)
    public static readonly byte[] PktKeepalive = new byte[] { 0x05, 0x00, 0x00 };

    // Command packet type: 0x04, 0x00, 0x00
    public static readonly byte[] TypeCommand = new byte[] { 0x04, 0x00, 0x00 };

    public const ushort CmdGetFileList = 13;
    public const ushort CmdGetMiniThumbnail = 30;

    public const int MediaVideoAndPhoto = 2;
    public const double KeepaliveIntervalSeconds = 2.0;

    /// <summary>
    /// Packs a payload with a 4-byte little-endian length prefix: [u32 LE (len(payload) + 4)][payload]
    /// </summary>
    public static byte[] PackPacket(byte[] payload)
    {
        uint totalLen = (uint)(payload.Length + 4);
        byte[] packet = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), totalLen);
        payload.CopyTo(packet, 4);
        return packet;
    }

    /// <summary>
    /// Constructs a command packet for TCP port 6666:
    /// 04 00 00 | cmd u16 LE | 02 | seq 3-byte LE | 80 00 00 | protobuf
    /// </summary>
    public static byte[] MakeCommand(ushort cmdId, uint seq, byte[]? proto = null)
    {
        proto ??= Array.Empty<byte>();
        using var ms = new MemoryStream();
        ms.Write(TypeCommand);

        byte[] cmdBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(cmdBytes, cmdId);
        ms.Write(cmdBytes);

        ms.WriteByte(0x02);

        byte[] seqBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seqBytes, seq);
        ms.Write(seqBytes, 0, 3); // 3-byte LE sequence

        ms.Write(new byte[] { 0x80, 0x00, 0x00 });

        if (proto.Length > 0)
        {
            ms.Write(proto);
        }

        return ms.ToArray();
    }

    public static byte[] EncodeVarint(ulong value)
    {
        var list = new List<byte>();
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
            {
                list.Add((byte)(b | 0x80));
            }
            else
            {
                list.Add(b);
                break;
            }
        }
        return list.ToArray();
    }

    public static byte[] FieldVarint(int field, ulong value)
    {
        byte[] tag = EncodeVarint((ulong)(field << 3));
        byte[] val = EncodeVarint(value);
        byte[] res = new byte[tag.Length + val.Length];
        tag.CopyTo(res, 0);
        val.CopyTo(res, tag.Length);
        return res;
    }

    public static byte[] FieldBytes(int field, byte[] data)
    {
        byte[] tag = EncodeVarint((ulong)((field << 3) | 2));
        byte[] len = EncodeVarint((ulong)data.Length);
        byte[] res = new byte[tag.Length + len.Length + data.Length];
        tag.CopyTo(res, 0);
        len.CopyTo(res, tag.Length);
        data.CopyTo(res, tag.Length + len.Length);
        return res;
    }

    public static List<(int field, int wire, byte[] data, ulong varintValue)> DecodeFields(byte[] body)
    {
        var results = new List<(int field, int wire, byte[] data, ulong varintValue)>();
        int i = 0;
        int n = body.Length;

        while (i < n)
        {
            ulong tag = 0;
            int shift = 0;
            while (true)
            {
                if (i >= n) return results;
                byte b = body[i++];
                tag |= (ulong)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }

            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);

            if (wire == 0) // varint
            {
                ulong val = 0;
                shift = 0;
                while (i < n)
                {
                    byte b = body[i++];
                    val |= (ulong)(b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0) break;
                }
                results.Add((field, wire, Array.Empty<byte>(), val));
            }
            else if (wire == 2) // length-delimited
            {
                ulong len = 0;
                shift = 0;
                while (i < n)
                {
                    byte b = body[i++];
                    len |= (ulong)(b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0) break;
                }
                int length = (int)len;
                if (i + length <= n)
                {
                    byte[] data = new byte[length];
                    Array.Copy(body, i, data, 0, length);
                    i += length;
                    results.Add((field, wire, data, 0));
                }
                else
                {
                    break;
                }
            }
            else if (wire == 1) // 64-bit (double / fixed64)
            {
                if (i + 8 <= n)
                {
                    byte[] data = new byte[8];
                    Array.Copy(body, i, data, 0, 8);
                    i += 8;
                    results.Add((field, wire, data, 0));
                }
                else
                {
                    break;
                }
            }
            else if (wire == 5) // 32-bit (float / fixed32)
            {
                if (i + 4 <= n)
                {
                    byte[] data = new byte[4];
                    Array.Copy(body, i, data, 0, 4);
                    i += 4;
                    results.Add((field, wire, data, 0));
                }
                else
                {
                    break;
                }
            }
            else
            {
                return results;
            }
        }
        return results;
    }
}
