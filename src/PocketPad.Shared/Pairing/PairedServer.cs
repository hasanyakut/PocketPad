namespace PocketPad.Shared.Pairing;

/// <summary>Everything the phone needs to reconnect to a paired Windows PC. Stored in Android SecureStorage.</summary>
public sealed record PairedServer
{
    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string CertificateFingerprint { get; init; }
    public required string DeviceId { get; init; }
    /// <summary>Long-lived secret issued by the server after user approval. Never log it.</summary>
    public required string DeviceSecret { get; init; }
    public DateTimeOffset PairedAt { get; init; }

    public override string ToString() =>
        $"{nameof(PairedServer)} {{ ServerId = {ServerId}, ServerName = {ServerName}, Host = {Host}, Port = {Port}, DeviceId = {DeviceId}, DeviceSecret = *** }}";
}
