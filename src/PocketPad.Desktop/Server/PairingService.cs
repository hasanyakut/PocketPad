using System.Security.Cryptography;
using System.Text;
using PocketPad.Shared.Pairing;
using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Server;

/// <summary>
/// Issues one-time, short-lived pairing invitations. Only the most recent invitation is valid:
/// creating a new one, consuming it or letting it expire invalidates the previous QR code.
/// </summary>
public sealed class PairingService(ServerIdentity identity, ServerOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private byte[]? _tokenHash;
    private DateTimeOffset _expiresAt;
    private PairingQrPayload? _current;

    /// <summary>Raised when the active invitation is consumed or cancelled.</summary>
    public event EventHandler? InvitationClosed;

    public PairingQrPayload CreateInvitation(string host, int port, string certificateFingerprint)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = time.GetUtcNow().Add(options.PairingTokenLifetime);
        var payload = new PairingQrPayload
        {
            ProtocolVersion = ProtocolInfo.Version,
            ServerId = identity.ServerId,
            ServerName = identity.ServerName,
            Host = host,
            Port = port,
            CertificateFingerprint = certificateFingerprint,
            PairingToken = token,
            ExpiresAt = expiresAt,
        };

        lock (_gate)
        {
            _tokenHash = Hash(token);
            _expiresAt = expiresAt;
            _current = payload;
        }
        return payload;
    }

    /// <summary>The invitation currently shown on screen (for LAN discovery), if still valid.</summary>
    public bool TryGetActiveInvitation(out PairingQrPayload? invitation)
    {
        lock (_gate)
        {
            invitation = _tokenHash is not null && time.GetUtcNow() < _expiresAt ? _current : null;
            return invitation is not null;
        }
    }

    public bool HasActiveInvitation
    {
        get
        {
            lock (_gate) return _tokenHash is not null && time.GetUtcNow() < _expiresAt;
        }
    }

    /// <summary>Validates and burns the token. Succeeds at most once per invitation.</summary>
    public bool TryConsume(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var candidate = Hash(token);
        lock (_gate)
        {
            if (_tokenHash is null) return false;
            if (time.GetUtcNow() >= _expiresAt)
            {
                _tokenHash = null;
                _current = null;
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(candidate, _tokenHash)) return false;
            _tokenHash = null;
            _current = null;
        }
        InvitationClosed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _tokenHash = null;
            _current = null;
        }
        InvitationClosed?.Invoke(this, EventArgs.Empty);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
