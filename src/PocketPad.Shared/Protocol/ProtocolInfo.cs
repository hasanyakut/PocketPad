namespace PocketPad.Shared.Protocol;

/// <summary>Protocol-wide constants shared by the Android client and the Windows server.</summary>
public static class ProtocolInfo
{
    /// <summary>Current envelope/protocol version. Bump when the wire format changes incompatibly.</summary>
    public const int Version = 1;

    /// <summary>Default TCP port of the Windows WSS server.</summary>
    public const int DefaultPort = 45900;

    /// <summary>HTTP path of the WebSocket endpoint.</summary>
    public const string WebSocketPath = "/pocketpad/ws";

    /// <summary>Upper bound for a single WebSocket message (bytes, UTF-8 JSON).</summary>
    public const int MaxMessageBytes = 8 * 1024;

    public const int MaxDeviceNameLength = 64;
    public const int MaxTextLength = 1000;
    public const double MaxPointerDelta = 10_000;
}
