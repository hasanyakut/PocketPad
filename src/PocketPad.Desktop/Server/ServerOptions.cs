using System.Net;
using PocketPad.Shared.Protocol;
using PocketPad.Shared.Transport;

namespace PocketPad.Desktop.Server;

public sealed record ServerOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    /// <summary>0 = pick a free port (tests).</summary>
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    /// <summary>UDP port for LAN discovery; 0 disables discovery.</summary>
    public int DiscoveryPort { get; init; } = Shared.Discovery.DiscoveryProtocol.Port;
    public ChannelOptions Channel { get; init; } = new();
    /// <summary>A new connection must pair or authenticate within this time.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PairingTokenLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Concurrent sockets (only one of them can be the control session).</summary>
    public int MaxConnections { get; init; } = 4;
}
