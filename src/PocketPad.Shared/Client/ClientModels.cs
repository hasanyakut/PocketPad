using PocketPad.Shared.Protocol;
using PocketPad.Shared.Transport;

namespace PocketPad.Shared.Client;

public enum ConnectionState { Disconnected, Connecting, AwaitingApproval, Connected, Reconnecting }

public enum ConnectionFailure
{
    None,
    /// <summary>The server presented a certificate that does not match the pinned fingerprint.</summary>
    CertificateMismatch,
    Unreachable,
    HandshakeTimeout,
    Rejected,
    ConnectionLost,
    ProtocolError,
}

public sealed class ConnectionStateChangedEventArgs(ConnectionState state, ConnectionFailure failure, RejectReason reason) : EventArgs
{
    public ConnectionState State { get; } = state;
    public ConnectionFailure Failure { get; } = failure;
    public RejectReason Reason { get; } = reason;
}

public sealed class PairingException(ConnectionFailure failure, RejectReason reason, Exception? inner = null)
    : Exception($"Pairing failed: {failure}/{reason}", inner)
{
    public ConnectionFailure Failure { get; } = failure;
    public RejectReason Reason { get; } = reason;
}

internal sealed class ConnectionFailedException(ConnectionFailure failure, Exception? inner)
    : Exception($"Connection failed: {failure}", inner)
{
    public ConnectionFailure Failure { get; } = failure;
}

/// <summary>Exponential back-off for automatic reconnection.</summary>
public sealed record ReconnectPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double Multiplier { get; init; } = 2;
    /// <summary>Null = retry until stopped.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>Delay before attempt <paramref name="attempt"/> (0-based), or null when attempts are exhausted.</summary>
    public TimeSpan? GetDelay(int attempt)
    {
        if (MaxAttempts is { } max && attempt >= max) return null;
        var ms = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, Math.Min(attempt, 30));
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }
}

public sealed record ClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Covers the time the Windows user needs to approve the pairing.</summary>
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public ChannelOptions Channel { get; init; } = new();
    public ReconnectPolicy Reconnect { get; init; } = new();
}
