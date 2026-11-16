using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PocketPad.Desktop.Server;

/// <summary>Keeps the server LAN-only: connections from public addresses are refused.</summary>
public static class LocalNetwork
{
    public static bool IsAllowedRemote(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        return false;
    }

    /// <summary>Private IPv4 addresses of active adapters, the ones with a default gateway first.</summary>
    public static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var candidates = new List<(IPAddress Address, bool HasGateway)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                             && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in props.UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && IsAllowedRemote(ip)
                    && !(ip.GetAddressBytes() is [169, 254, ..]))
                    candidates.Add((ip, hasGateway));
            }
        }

        return candidates.OrderByDescending(c => c.HasGateway).Select(c => c.Address).Distinct().ToList();
    }
}
