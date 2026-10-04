using System.Net;
using System.Net.Sockets;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// Compiled into the bridges that open a TCP connection to a host the page names — an OBD adapter, a receipt printer.
/// </summary>
static class LocalNetwork
{
    /// <summary>
    /// Whether <paramref name="address"/> is loopback, private or link-local. Holding the page to these keeps it from using
    /// the device to open TCP connections to arbitrary hosts; parse an IP literal rather than resolve a name, which could
    /// resolve somewhere else.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}
