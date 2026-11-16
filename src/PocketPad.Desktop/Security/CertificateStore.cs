using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using PocketPad.Shared.Security;

namespace PocketPad.Desktop.Security;

public interface ICertificateProvider
{
    /// <summary>Server TLS certificate with private key. Created on first use and reused afterwards.</summary>
    X509Certificate2 GetCertificate();
    string Fingerprint { get; }
}

/// <summary>
/// Self-signed ECDSA P-256 server certificate. The PKCS#12 (incl. private key) is only kept on disk
/// encrypted with <see cref="ISecretProtector"/> (DPAPI); the phone pins its SHA-256 fingerprint.
/// </summary>
public sealed class CertificateStore(AppDataPaths paths, ISecretProtector protector, TimeProvider time, ILogger<CertificateStore> logger)
    : ICertificateProvider, IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(5 * 365);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private readonly Lock _gate = new();
    private X509Certificate2? _certificate;

    public string Fingerprint => CertificateFingerprint.Compute(GetCertificate());

    public X509Certificate2 GetCertificate()
    {
        lock (_gate)
        {
            return _certificate ??= LoadExisting() ?? CreateAndSave();
        }
    }

    private X509Certificate2? LoadExisting()
    {
        if (!File.Exists(paths.Certificate)) return null;
        try
        {
            var pfx = protector.Unprotect(File.ReadAllBytes(paths.Certificate));
            try
            {
                // UserKeySet without PersistKeySet: the key lives in a temporary container for the process lifetime only.
                var cert = X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.UserKeySet);
                if (!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime() - time.GetUtcNow().UtcDateTime < RenewBefore)
                {
                    logger.LogInformation("Stored certificate is expiring or invalid; generating a new one.");
                    cert.Dispose();
                    return null;
                }
                return cert;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Stored certificate could not be decrypted; generating a new one. Paired phones must pair again.");
            return null;
        }
    }

    private X509Certificate2 CreateAndSave()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=PocketPad {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var now = time.GetUtcNow();
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.Add(Lifetime));
        var pfx = created.Export(X509ContentType.Pkcs12);
        try
        {
            AppDataPaths.WriteAtomic(paths.Certificate, protector.Protect(pfx));
            logger.LogInformation("Generated new server certificate.");
            return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _certificate?.Dispose();
            _certificate = null;
        }
    }
}
