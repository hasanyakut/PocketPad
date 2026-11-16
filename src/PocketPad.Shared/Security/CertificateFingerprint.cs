using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PocketPad.Shared.Security;

/// <summary>SHA-256 certificate pinning helpers (fingerprint over the DER encoded certificate).</summary>
public static class CertificateFingerprint
{
    public static string Compute(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    public static bool IsValidFormat(string? fingerprint) =>
        fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit);

    /// <summary>Constant-time comparison of the certificate against the pinned fingerprint.</summary>
    public static bool Matches(X509Certificate? certificate, string expectedFingerprint)
    {
        if (certificate is null || !IsValidFormat(expectedFingerprint)) return false;
        var actual = SHA256.HashData(certificate.GetRawCertData());
        var expected = Convert.FromHexString(expectedFingerprint);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>"AB:CD:..." grouping for display.</summary>
    public static string Format(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));
}
