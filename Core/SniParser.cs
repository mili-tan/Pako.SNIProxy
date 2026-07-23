using System.Buffers.Binary;
using System.Text;

namespace Pako.SNIProxy.Core;

public static class SniParser
{
    private const byte ContentTypeHandshake = 0x16;
    private const byte HandshakeTypeClientHello = 0x01;
    private const ushort ExtensionTypeServerName = 0x0000;
    private const byte ServerNameTypeHostName = 0x00;

    public static bool TryExtractSni(ReadOnlySpan<byte> buffer, out string? sni)
    {
        sni = null;

        if (buffer.Length < 5 || buffer[0] != ContentTypeHandshake)
            return false;

        int recordLength = BinaryPrimitives.ReadUInt16BigEndian(buffer[3..]);
        if (buffer.Length < 5 + recordLength)
            return false;

        var record = buffer.Slice(5, recordLength);
        return TryParseClientHello(record, out sni);
    }

    private static bool TryParseClientHello(ReadOnlySpan<byte> record, out string? sni)
    {
        sni = null;

        if (record.Length < 4 || record[0] != HandshakeTypeClientHello)
            return false;

        int handshakeLength = (record[1] << 16) | (record[2] << 8) | record[3];
        if (record.Length < 4 + handshakeLength)
            return false;

        var hello = record[4..];
        int pos = 0;

        if (!TryAdvance(hello, ref pos, 2 + 32))
            return false;

        if (!TryReadUint8(hello, ref pos, out int sessionIdLen))
            return false;
        if (!TryAdvance(hello, ref pos, sessionIdLen))
            return false;

        if (!TryReadUint16(hello, ref pos, out int cipherLen))
            return false;
        if (!TryAdvance(hello, ref pos, cipherLen))
            return false;

        if (!TryReadUint8(hello, ref pos, out int compLen))
            return false;
        if (!TryAdvance(hello, ref pos, compLen))
            return false;

        if (!TryReadUint16(hello, ref pos, out int extensionsLen))
            return false;

        int extensionsEnd = pos + extensionsLen;
        if (extensionsEnd > hello.Length)
            return false;

        return TryFindSniExtension(hello, ref pos, extensionsEnd, out sni);
    }

    private static bool TryFindSniExtension(ReadOnlySpan<byte> hello, ref int pos, int end, out string? sni)
    {
        sni = null;

        while (pos + 4 <= end)
        {
            ushort extType = BinaryPrimitives.ReadUInt16BigEndian(hello[pos..]);
            ushort extDataLen = BinaryPrimitives.ReadUInt16BigEndian(hello[(pos + 2)..]);
            pos += 4;

            if (pos + extDataLen > end)
                return false;

            if (extType == ExtensionTypeServerName)
            {
                return TryParseServerNameList(hello.Slice(pos, extDataLen), out sni);
            }

            pos += extDataLen;
        }

        return false;
    }

    private static bool TryParseServerNameList(ReadOnlySpan<byte> extData, out string? sni)
    {
        sni = null;

        if (extData.Length < 2)
            return false;

        int listLen = BinaryPrimitives.ReadUInt16BigEndian(extData);
        if (extData.Length < 2 + listLen)
            return false;

        var list = extData.Slice(2, listLen);
        int pos = 0;

        while (pos + 3 <= list.Length)
        {
            byte nameType = list[pos];
            int nameLen = BinaryPrimitives.ReadUInt16BigEndian(list[(pos + 1)..]);
            pos += 3;

            if (pos + nameLen > list.Length)
                return false;

            if (nameType == ServerNameTypeHostName && nameLen > 0)
            {
                var nameBytes = list.Slice(pos, nameLen);
                sni = Encoding.ASCII.GetString(nameBytes).ToLowerInvariant();
                return IsValidHostName(sni);
            }

            pos += nameLen;
        }

        return false;
    }

    private static bool IsValidHostName(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253)
            return false;

        foreach (char c in host)
        {
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_'))
                return false;
        }

        return true;
    }

    private static bool TryAdvance(ReadOnlySpan<byte> buffer, ref int pos, int count)
    {
        if (count < 0 || pos + count > buffer.Length)
            return false;
        pos += count;
        return true;
    }

    private static bool TryReadUint8(ReadOnlySpan<byte> buffer, ref int pos, out int value)
    {
        value = 0;
        if (pos + 1 > buffer.Length)
            return false;
        value = buffer[pos];
        pos += 1;
        return true;
    }

    private static bool TryReadUint16(ReadOnlySpan<byte> buffer, ref int pos, out int value)
    {
        value = 0;
        if (pos + 2 > buffer.Length)
            return false;
        value = BinaryPrimitives.ReadUInt16BigEndian(buffer[pos..]);
        pos += 2;
        return true;
    }
}
