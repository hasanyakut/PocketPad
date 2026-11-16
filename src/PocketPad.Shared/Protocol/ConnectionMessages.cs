namespace PocketPad.Shared.Protocol;

/// <summary>Why the server refused a pairing/authentication attempt or closed a session.</summary>
public enum RejectReason
{
    None,
    InvalidPairingToken,
    UserDenied,
    ApprovalTimeout,
    UnknownDevice,
    Busy,
    Unauthorized,
    ProtocolError,
    ServerShuttingDown,
    Replaced,
    DeviceRemoved,
}

public sealed record PingMessage : ProtocolMessage
{
    /// <summary>Sender clock (Unix ms). Echoed back in <see cref="PongMessage"/>.</summary>
    public required long Timestamp { get; init; }
}

public sealed record PongMessage : ProtocolMessage
{
    public required long Timestamp { get; init; }
}

/// <summary>Graceful close notice, sent by either side right before closing the socket.</summary>
public sealed record DisconnectMessage : ProtocolMessage
{
    public RejectReason Reason { get; init; }

    public override string? Validate() => ValidateEnum(Reason);
}

public sealed record ErrorMessage : ProtocolMessage
{
    public required RejectReason Reason { get; init; }
    public string? Detail { get; init; }

    public override string? Validate() =>
        FirstError(ValidateEnum(Reason), Detail is { Length: > 256 } ? "Detail is too long." : null);
}

/// <summary>First message of a new phone: proves it scanned the current QR code.</summary>
public sealed record PairRequestMessage : ProtocolMessage
{
    public required string PairingToken { get; init; }
    public required string DeviceName { get; init; }
    public string Platform { get; init; } = "Android";

    public override string? Validate() => FirstError(
        PairingToken is { Length: > 0 and <= 128 } ? null : "Pairing token is invalid.",
        ValidateDeviceName(DeviceName),
        Platform is { Length: <= 32 } ? null : "Platform is invalid.");

    public override string ToString() =>
        $"{nameof(PairRequestMessage)} {{ DeviceName = {DeviceName}, Platform = {Platform}, PairingToken = *** }}";
}

/// <summary>Server answer to <see cref="PairRequestMessage"/>; on success carries the long-lived device credentials.</summary>
public sealed record PairResultMessage : ProtocolMessage
{
    public required bool Accepted { get; init; }
    public RejectReason Reason { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceSecret { get; init; }
    public string? ServerId { get; init; }
    public string? ServerName { get; init; }

    public override string? Validate()
    {
        if (ValidateEnum(Reason) is { } enumError) return enumError;
        if (!Accepted) return null;
        return string.IsNullOrEmpty(DeviceId) || string.IsNullOrEmpty(DeviceSecret) || string.IsNullOrEmpty(ServerId)
            ? "Accepted pairing result must carry credentials."
            : null;
    }

    public override string ToString() =>
        $"{nameof(PairResultMessage)} {{ Accepted = {Accepted}, Reason = {Reason}, DeviceId = {DeviceId}, ServerId = {ServerId}, DeviceSecret = *** }}";
}

/// <summary>First message of an already paired phone.</summary>
public sealed record AuthRequestMessage : ProtocolMessage
{
    public required string DeviceId { get; init; }
    public required string DeviceSecret { get; init; }
    public required string DeviceName { get; init; }

    public override string? Validate() => FirstError(
        DeviceId is { Length: > 0 and <= 64 } ? null : "Device id is invalid.",
        DeviceSecret is { Length: > 0 and <= 128 } ? null : "Device secret is invalid.",
        ValidateDeviceName(DeviceName));

    public override string ToString() =>
        $"{nameof(AuthRequestMessage)} {{ DeviceId = {DeviceId}, DeviceName = {DeviceName}, DeviceSecret = *** }}";
}

public sealed record AuthResultMessage : ProtocolMessage
{
    public required bool Accepted { get; init; }
    public RejectReason Reason { get; init; }
    public string? ServerName { get; init; }

    public override string? Validate() => ValidateEnum(Reason);
}
