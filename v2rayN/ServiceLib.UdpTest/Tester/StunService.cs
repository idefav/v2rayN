using System.Security.Cryptography;

namespace ServiceLib.UdpTest.Tester;

public class StunService : IUdpTest
{
    private const int StunDefaultPort = 3478;
    private const string StunDefaultServer = "stun.cloudflare.com";
    private byte[]? _transaction;

    private static readonly byte[] StunBindingRequestPacket =
    [
        // STUN Binding Request
        0x00, 0x01, // Message Type: Binding Request (0x0001)
        0x00, 0x00, // Message Length: 0 (no attributes)
        0x21, 0x12, 0xA4, 0x42, // Magic Cookie: 0x2112A442
        // Transaction ID: 96 bits (12 bytes) random
        0x66, 0x0E, 0xAB, 0xBC, 0x61, 0x0D,
        0xA4, 0x40, 0x8C, 0x65, 0xC1, 0xBE,
    ];

    public byte[] BuildUdpRequestPacket()
    {
        var packet = (byte[])StunBindingRequestPacket.Clone();
        RandomNumberGenerator.Fill(packet.AsSpan(8));
        _transaction = packet[8..];
        return packet;
    }

    public bool VerifyAndExtractUdpResponse(byte[] stunResponseBytes)
    {
        return _transaction != null && TryExtractMappedAddress(stunResponseBytes, _transaction, out _);
    }

    public static bool TryExtractMappedAddress(ReadOnlySpan<byte> response, ReadOnlySpan<byte> transaction, out IPAddress? address)
    {
        address = null;
        if (response.Length < 20 || transaction.Length != 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != 0x0101
            || BinaryPrimitives.ReadUInt32BigEndian(response[4..]) != 0x2112A442 || !response.Slice(8, 12).SequenceEqual(transaction)) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if (length % 4 != 0 || length != response.Length - 20) return false;
        for (var offset = 20; offset < response.Length;)
        {
            if (offset + 4 > response.Length) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]); offset += 4;
            if (offset + size > response.Length) return false;
            if (type is 0x0020 or 0x0001)
            {
                var value = response.Slice(offset, size);
                if (size < 4 || value[0] != 0 || value[1] is not (1 or 2)) return false;
                var bytes = value[1] == 1 ? 4 : 16;
                if (size != bytes + 4) return false;
                var ip = value[4..].ToArray();
                if (type == 0x0020)
                {
                    Span<byte> mask = stackalloc byte[16];
                    BinaryPrimitives.WriteUInt32BigEndian(mask, 0x2112A442); transaction.CopyTo(mask[4..]);
                    for (var i = 0; i < ip.Length; i++) ip[i] ^= mask[i];
                }
                address = new IPAddress(ip); return true;
            }
            offset += (size + 3) & ~3;
        }
        return false;
    }

    public ushort GetDefaultTargetPort()
    {
        return StunDefaultPort;
    }

    public string GetDefaultTargetHost()
    {
        return StunDefaultServer;
    }
}
