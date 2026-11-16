using System.Globalization;
using System.Net;
using System.Text;
using PocketPad.Shared.Protocol;

namespace PocketPad.Shared.Pairing;

/// <summary>
/// Contents of the one-time pairing QR code shown by the Windows app, encoded as
/// <c>pocketpad://pair?v=1&amp;sid=..&amp;n=..&amp;h=..&amp;p=..&amp;fp=..&amp;t=..&amp;exp=..</c>.
/// </summary>
public sealed record PairingQrPayload
{
    public const string Scheme = "pocketpad";
    public const string Action = "pair";

    public required int ProtocolVersion { get; init; }
    /// <summary>Stable id of the Windows installation.</summary>
    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    /// <summary>SHA-256 of the server TLS certificate (DER), 64 uppercase hex chars.</summary>
    public required string CertificateFingerprint { get; init; }
    /// <summary>One-time pairing token. Secret: never log it.</summary>
    public required string PairingToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public string ToUri()
    {
        var sb = new StringBuilder($"{Scheme}://{Action}?");
        Append(sb, "v", ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        Append(sb, "sid", ServerId);
        Append(sb, "n", ServerName);
        Append(sb, "h", Host);
        Append(sb, "p", Port.ToString(CultureInfo.InvariantCulture));
        Append(sb, "fp", CertificateFingerprint);
        Append(sb, "t", PairingToken);
        Append(sb, "exp", ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        sb.Length--; // trailing '&'
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append('=').Append(Uri.EscapeDataString(value)).Append('&');

    public static bool TryParse(string? text, out PairingQrPayload? payload, out string? error)
    {
        payload = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024)
            return Fail("QR content is empty or too long.", out error);
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(Action, StringComparison.OrdinalIgnoreCase))
            return Fail("This is not a PocketPad pairing code.", out error);

        var query = ParseQuery(uri.Query);
        string Get(string key) => query.TryGetValue(key, out var v) ? v : string.Empty;

        if (!int.TryParse(Get("v"), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            return Fail("Protocol version is missing.", out error);
        if (version != ProtocolInfo.Version)
            return Fail($"Unsupported protocol version {version}. Update PocketPad.", out error);
        if (!IPAddress.TryParse(Get("h"), out _))
            return Fail("Server address is invalid.", out error);
        if (!int.TryParse(Get("p"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return Fail("Server port is invalid.", out error);
        if (!Security.CertificateFingerprint.IsValidFormat(Get("fp")))
            return Fail("Certificate fingerprint is invalid.", out error);
        if (Get("t") is not { Length: >= 16 and <= 128 } token)
            return Fail("Pairing token is invalid.", out error);
        if (Get("sid") is not { Length: > 0 and <= 64 } serverId)
            return Fail("Server id is invalid.", out error);
        if (!long.TryParse(Get("exp"), NumberStyles.None, CultureInfo.InvariantCulture, out var exp))
            return Fail("Expiry is invalid.", out error);

        var name = Get("n");
        payload = new PairingQrPayload
        {
            ProtocolVersion = version,
            ServerId = serverId,
            ServerName = name.Length is > 0 and <= ProtocolInfo.MaxDeviceNameLength ? name : "Windows PC",
            Host = Get("h"),
            Port = port,
            CertificateFingerprint = Get("fp").ToUpperInvariant(),
            PairingToken = token,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(exp),
        };
        return true;
    }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            result[part[..idx]] = Uri.UnescapeDataString(part[(idx + 1)..]);
        }
        return result;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    public override string ToString() =>
        $"{nameof(PairingQrPayload)} {{ ServerId = {ServerId}, Host = {Host}, Port = {Port}, ExpiresAt = {ExpiresAt:O}, PairingToken = *** }}";
}
