using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PocketPad.Desktop.Security;
using PocketPad.Desktop.Server;
using PocketPad.Shared.Security;

namespace PocketPad.Tests.Desktop;

public sealed class DesktopSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PocketPadTests", Guid.NewGuid().ToString("N"));
    private readonly AppDataPaths _paths;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-16T10:00:00Z"));

    public DesktopSecurityTests() => _paths = new AppDataPaths(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private PairingService NewPairing(TimeSpan? lifetime = null) =>
        new(new ServerIdentity("srv", "PC"), new ServerOptions { PairingTokenLifetime = lifetime ?? TimeSpan.FromMinutes(2) }, _time);

    [Fact]
    public void PairingToken_IsSingleUse()
    {
        var pairing = NewPairing();
        var qr = pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));

        Assert.True(pairing.TryConsume(qr.PairingToken));
        Assert.False(pairing.TryConsume(qr.PairingToken));
    }

    [Fact]
    public void PairingToken_ExpiresAfterLifetime()
    {
        var pairing = NewPairing(TimeSpan.FromMinutes(2));
        var qr = pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));

        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.False(pairing.TryConsume(qr.PairingToken));
        Assert.False(pairing.HasActiveInvitation);
    }

    [Fact]
    public void NewInvitation_InvalidatesPreviousQr()
    {
        var pairing = NewPairing();
        var old = pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));
        var current = pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));

        Assert.NotEqual(old.PairingToken, current.PairingToken);
        Assert.False(pairing.TryConsume(old.PairingToken));
        Assert.True(pairing.TryConsume(current.PairingToken));
    }

    [Fact]
    public void ActiveInvitation_IsAvailableOnlyUntilConsumedOrExpired()
    {
        var pairing = NewPairing(TimeSpan.FromMinutes(2));
        Assert.False(pairing.TryGetActiveInvitation(out _));

        var qr = pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));
        Assert.True(pairing.TryGetActiveInvitation(out var active));
        Assert.Equal(qr, active);

        pairing.TryConsume(qr.PairingToken);
        Assert.False(pairing.TryGetActiveInvitation(out _));

        pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.False(pairing.TryGetActiveInvitation(out _));
    }

    [Fact]
    public void CancelledInvitation_IsRejected()
    {
        var pairing = NewPairing();
        var qr = pairing.CreateInvitation("192.168.1.2", 45900, new string('A', 64));
        pairing.Cancel();
        Assert.False(pairing.TryConsume(qr.PairingToken));
    }

    [Fact]
    public void QrInvitation_CarriesAllRequiredFields()
    {
        var qr = NewPairing().CreateInvitation("192.168.1.2", 45900, new string('A', 64));

        Assert.Equal("srv", qr.ServerId);
        Assert.Equal("192.168.1.2", qr.Host);
        Assert.Equal(45900, qr.Port);
        Assert.Equal(new string('A', 64), qr.CertificateFingerprint);
        Assert.Equal(1, qr.ProtocolVersion);
        Assert.True(qr.PairingToken.Length >= 43);
    }

    [Fact]
    public void DeviceStore_VerifiesOnlyCorrectSecret_AndPersistsEncrypted()
    {
        var store = new PairedDeviceStore(_paths, new DpapiSecretProtector(), _time, NullLogger<PairedDeviceStore>.Instance);
        var (device, secret) = store.Add("Pixel 8", "Android");

        Assert.NotNull(store.Verify(device.DeviceId, secret));
        Assert.Null(store.Verify(device.DeviceId, secret + "x"));
        Assert.Null(store.Verify("unknown", secret));

        var raw = File.ReadAllBytes(_paths.PairedDevices);
        var text = Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("Pixel 8", text);
        Assert.DoesNotContain(secret, text);

        var reloaded = new PairedDeviceStore(_paths, new DpapiSecretProtector(), _time, NullLogger<PairedDeviceStore>.Instance);
        Assert.NotNull(reloaded.Verify(device.DeviceId, secret));
    }

    [Fact]
    public void DeviceStore_StoresOnlySecretHash()
    {
        var protector = new PassThroughProtector();
        var store = new PairedDeviceStore(_paths, protector, _time, NullLogger<PairedDeviceStore>.Instance);
        var (_, secret) = store.Add("Pixel", "Android");

        var json = Encoding.UTF8.GetString(File.ReadAllBytes(_paths.PairedDevices));
        Assert.DoesNotContain(secret, json);
        Assert.Contains("SecretHash", json);
    }

    [Fact]
    public void DeviceStore_RemoveRevokesAccess()
    {
        var store = new PairedDeviceStore(_paths, new DpapiSecretProtector(), _time, NullLogger<PairedDeviceStore>.Instance);
        var (device, secret) = store.Add("Pixel", "Android");

        Assert.True(store.Remove(device.DeviceId));
        Assert.Null(store.Verify(device.DeviceId, secret));
    }

    [Fact]
    public void Certificate_IsCreatedOnce_AndReused()
    {
        string first;
        using (var store = new CertificateStore(_paths, new DpapiSecretProtector(), _time, NullLogger<CertificateStore>.Instance))
        {
            var cert = store.GetCertificate();
            Assert.True(cert.HasPrivateKey);
            first = store.Fingerprint;
        }

        using var again = new CertificateStore(_paths, new DpapiSecretProtector(), _time, NullLogger<CertificateStore>.Instance);
        Assert.Equal(first, again.Fingerprint);
        Assert.Equal(first, CertificateFingerprint.Compute(again.GetCertificate()));
    }

    [Fact]
    public void Certificate_PrivateKeyIsNotStoredInPlainText()
    {
        using var store = new CertificateStore(_paths, new DpapiSecretProtector(), _time, NullLogger<CertificateStore>.Instance);
        store.GetCertificate();

        var onDisk = File.ReadAllBytes(_paths.Certificate);
        // The file is DPAPI ciphertext: it must not be loadable as a PKCS#12/PFX by itself.
        Assert.ThrowsAny<CryptographicException>(() => { X509CertificateLoader.LoadPkcs12(onDisk, null).Dispose(); });
    }

    [Fact]
    public void Certificate_IsRegeneratedWhenStoreIsCorrupt()
    {
        string first;
        using (var store = new CertificateStore(_paths, new DpapiSecretProtector(), _time, NullLogger<CertificateStore>.Instance))
            first = store.Fingerprint;

        File.WriteAllBytes(_paths.Certificate, [1, 2, 3]);

        using var again = new CertificateStore(_paths, new DpapiSecretProtector(), _time, NullLogger<CertificateStore>.Instance);
        Assert.NotEqual(first, again.Fingerprint);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("::ffff:192.168.1.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void LocalNetwork_AllowsOnlyPrivateAddresses(string address, bool allowed) =>
        Assert.Equal(allowed, LocalNetwork.IsAllowedRemote(System.Net.IPAddress.Parse(address)));

    private sealed class PassThroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
