using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace ServiceLib.Services;

// Shared with the packaged AmazTool so an elevated core can be checked with its
// own credentials, without starting the core or loading a user's configuration.
internal static class ListenerBindProbe
{
    internal const string Command = "check-listener";
    internal const string Marker = "__V2RAYN_BIND_V1__";

    internal static SocketError Check(IPAddress address, int port, ProtocolType protocol)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily,
                protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                socket.DualMode = address.Equals(IPAddress.IPv6Any) || address.IsIPv4MappedToIPv6;
            // Go's Windows listener uses neither SO_REUSEADDR (which can steal
            // a live port) nor SO_EXCLUSIVEADDRUSE (which blocks reusable TIME_WAIT).
            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            }
            // .NET's Unix ReuseAddress=true enables SO_REUSEPORT as well, unlike
            // Go. Keep sharing disabled. SystemNative_Bind itself enables only
            // SO_REUSEADDR for TCP, allowing reusable TIME_WAIT without sharing a
            // live listener. UDP retains both options disabled.
            else socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            socket.Bind(new IPEndPoint(address, port));
            if (protocol == ProtocolType.Tcp) socket.Listen(1);
            return SocketError.Success;
        }
        catch (SocketException ex) { return ex.SocketErrorCode; }
    }

    internal static int Run(string[] args, TextWriter output)
    {
        if (args.Length != 4 || args[0] != Command || args[1] is not ("tcp" or "udp")
            || !IPAddress.TryParse(args[2], out var address)
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return 2;
        var result = Check(address, port, args[1] == "tcp" ? ProtocolType.Tcp : ProtocolType.Udp);
        output.WriteLine($"{Marker}\t{result}");
        // A completed probe is distinct from sudo/helper failure, even when busy.
        return 0;
    }

    internal static SocketError ParseResult(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var prefix = Marker + "\t";
        if (lines.Length != 1 || !lines[0].StartsWith(prefix, StringComparison.Ordinal)
            || !Enum.TryParse<SocketError>(lines[0][prefix.Length..], out var result) || !Enum.IsDefined(result))
            throw new IOException("端口检查帮助程序返回无效结果 / Invalid listener probe response.");
        return result;
    }
}
