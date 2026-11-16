using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PocketPad.Shared.Client;
using PocketPad.Shared.Pairing;
using PocketPad.Shared.Protocol;
using PocketPad.Shared.Security;

namespace PocketPad.Tests.Protocol;

public class PairingModelTests
{
    private static PairingQrPayload Sample() => new()
    {
        ProtocolVersion = ProtocolInfo.Version,
        ServerId = "0123456789abcdef0123456789abcdef",
        ServerName = "Masaüstü PC",
        Host = "192.168.1.20",
        Port = 45900,
        CertificateFingerprint = new string('A', 64),
        PairingToken = "abcDEF0123456789_-abcDEF0123456789_-abcdef",
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
    };

    [Fact]
    public void QrPayload_RoundTripsThroughUri()
    {
        var payload = Sample();
        var uri = payload.ToUri();

        Assert.StartsWith("pocketpad://pair?", uri);
        Assert.True(PairingQrPayload.TryParse(uri, out var parsed, out var error), error);
        Assert.Equal(payload, parsed);
    }

    [Theory]
    [InlineData("https://example.com/?v=1")]
    [InlineData("pocketpad://other?v=1")]
    [InlineData("")]
    [InlineData("garbage")]
    public void QrPayload_RejectsForeignCodes(string text) =>
        Assert.False(PairingQrPayload.TryParse(text, out _, out _));

    [Theory]
    [InlineData("v", "2")]
    [InlineData("h", "not-an-ip")]
    [InlineData("p", "70000")]
    [InlineData("fp", "ABC")]
    [InlineData("t", "short")]
    public void QrPayload_RejectsInvalidFields(string key, string value)
    {
        var uri = Sample().ToUri();
        var start = uri.IndexOf($"{key}=", StringComparison.Ordinal);
        var end = uri.IndexOf('&', start);
        var tampered = uri[..start] + $"{key}={value}" + (end < 0 ? "" : uri[end..]);

        Assert.False(PairingQrPayload.TryParse(tampered, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void QrPayload_ToString_HidesToken() =>
        Assert.DoesNotContain(Sample().PairingToken, Sample().ToString());

    [Fact]
    public void PairedServer_RoundTripsWithSourceGeneratedContext()
    {
        var server = new PairedServer
        {
            ServerId = "s", ServerName = "PC", Host = "10.0.0.2", Port = 45900,
            CertificateFingerprint = new string('B', 64), DeviceId = "d", DeviceSecret = "secret",
            PairedAt = DateTimeOffset.UnixEpoch,
        };
        var json = JsonSerializer.Serialize(new List<PairedServer> { server }, ProtocolJsonContext.Default.ListPairedServer);
        var back = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ListPairedServer);

        Assert.Equal(server, Assert.Single(back!));
        Assert.DoesNotContain("secret", server.ToString());
    }

    [Fact]
    public void Fingerprint_MatchesOnlyTheSameCertificate()
    {
        using var a = CreateCert();
        using var b = CreateCert();
        var fpA = CertificateFingerprint.Compute(a);

        Assert.True(CertificateFingerprint.IsValidFormat(fpA));
        Assert.True(CertificateFingerprint.Matches(a, fpA));
        Assert.True(CertificateFingerprint.Matches(a, fpA.ToLowerInvariant()));
        Assert.False(CertificateFingerprint.Matches(b, fpA));
        Assert.False(CertificateFingerprint.Matches(null, fpA));
        Assert.False(CertificateFingerprint.Matches(a, "not-hex"));
    }

    [Fact]
    public void ReconnectPolicy_BacksOffExponentiallyUpToMax()
    {
        var policy = new ReconnectPolicy { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(10), MaxAttempts = 6 };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(5));
        Assert.Null(policy.GetDelay(6));
    }

    [Theory]
    [InlineData("192.168.1.5", "wss://192.168.1.5:45900/pocketpad/ws")]
    [InlineData("fe80::1", "wss://[fe80::1]:45900/pocketpad/ws")]
    public void BuildUri_FormatsHosts(string host, string expected) =>
        Assert.Equal(expected, PocketPadClient.BuildUri(host, 45900).ToString());

    [Fact]
    public void SanitizeDeviceName_StripsControlCharsAndTruncates()
    {
        Assert.Equal("Pixel", PocketPadClient.SanitizeDeviceName("Pi\u0000xel\n"));
        Assert.Equal(ProtocolInfo.MaxDeviceNameLength, PocketPadClient.SanitizeDeviceName(new string('x', 200)).Length);
        Assert.Equal("Android", PocketPadClient.SanitizeDeviceName("  "));
    }

    private static X509Certificate2 CreateCert()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
