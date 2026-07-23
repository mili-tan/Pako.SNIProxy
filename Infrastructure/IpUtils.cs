using System.Net;
using System.Net.Sockets;

namespace Pako.SNIProxy.Infrastructure;

public static class IpUtils
{
    public static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static IPAddress? GetClientIp(Socket socket)
    {
        if (socket.RemoteEndPoint is not IPEndPoint ep)
            return null;
        return Normalize(ep.Address);
    }

    public static bool TryParseNetwork(string input, out IPNetwork network)
    {
        network = default;
        var trimmed = input?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return false;

        var candidate = trimmed;
        if (!trimmed.Contains('/'))
        {
            if (!IPAddress.TryParse(trimmed, out var ip))
                return false;
            candidate = trimmed + (ip.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128");
        }

        return IPNetwork.TryParse(candidate, out network);
    }
}
