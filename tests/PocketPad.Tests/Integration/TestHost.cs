using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PocketPad.Desktop.Input;
using PocketPad.Desktop.Media;
using PocketPad.Desktop.Security;
using PocketPad.Desktop.Server;
using PocketPad.Shared.Client;
using PocketPad.Shared.Pairing;
using PocketPad.Shared.Protocol;
using PocketPad.Shared.Security;
using PocketPad.Shared.Transport;

namespace PocketPad.Tests.Integration;

/// <summary>Real Kestrel WSS server on loopback with the production components (DPAPI, certificate, stores).</summary>
internal sealed class TestHost : IAsyncDisposable
{
    public static readonly ChannelOptions FastChannel = new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(200),
        IdleTimeout = TimeSpan.FromSeconds(1),
        CloseTimeout = TimeSpan.FromSeconds(1),
    };

    public static ClientOptions FastClient => new()
    {
        Channel = FastChannel,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        HandshakeTimeout = TimeSpan.FromSeconds(5),
        PairingTimeout = TimeSpan.FromSeconds(10),
        Reconnect = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(400) },
    };

    private readonly string _dir;

    private TestHost(string dir, ServerOptions options)
    {
        _dir = dir;
        Options = options;
        var paths = new AppDataPaths(dir);
        var protector = new DpapiSecretProtector();
        Identity = new ServerIdentity("server-under-test", "TEST-PC");
        Certificates = new CertificateStore(paths, protector, TimeProvider.System, NullLogger<CertificateStore>.Instance);
        Devices = new PairedDeviceStore(paths, protector, TimeProvider.System, NullLogger<PairedDeviceStore>.Instance);
        Pairing = new PairingService(Identity, options, TimeProvider.System);
        Registry = new SessionRegistry(options, TimeProvider.System);
        // Never the real WindowsInputController in tests: it would move the developer's mouse.
        Media = new MediaStatusPublisher(MediaProvider, Registry, NullLogger<MediaStatusPublisher>.Instance, TimeSpan.FromMilliseconds(100));
        Server = new ControlServer(options, Identity, Certificates, Pairing, Devices, Approval, Registry,
            new InputDispatcher(Input), TimeProvider.System, NullLogger<ControlServer>.Instance, Media);
    }

    public ServerOptions Options { get; }
    public ServerIdentity Identity { get; }
    public CertificateStore Certificates { get; }
    public PairedDeviceStore Devices { get; }
    public PairingService Pairing { get; }
    public SessionRegistry Registry { get; }
    public ControlServer Server { get; }
    public TestApproval Approval { get; } = new();
    public RecordingInputController Input { get; } = new();
    public FakeMediaStatusProvider MediaProvider { get; } = new();
    public MediaStatusPublisher Media { get; }
    public int Port => Server.Port;
    public string Fingerprint => Server.CertificateFingerprint;

    public static async Task<TestHost> StartAsync(Func<ServerOptions, ServerOptions>? configure = null)
    {
        var options = new ServerOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = FreePort(),
            DiscoveryPort = 0,
            Channel = FastChannel,
            HandshakeTimeout = TimeSpan.FromSeconds(2),
            ApprovalTimeout = TimeSpan.FromSeconds(2),
        };
        options = configure?.Invoke(options) ?? options;
        var host = new TestHost(Path.Combine(Path.GetTempPath(), "PocketPadTests", Guid.NewGuid().ToString("N")), options);
        await host.Server.StartAsync();
        return host;
    }

    public PairingQrPayload Invite() => Pairing.CreateInvitation("127.0.0.1", Port, Fingerprint);

    /// <summary>Pairs a new phone (auto-approved) and returns its credentials.</summary>
    public async Task<PairedServer> PairDeviceAsync(string name = "Test Phone")
    {
        Approval.Decide = (_, _) => Task.FromResult(true);
        await using var client = new PocketPadClient(FastClient);
        return await client.PairAsync(Invite(), name, CancellationToken.None);
    }

    /// <summary>A pinned raw WebSocket for protocol-level abuse tests.</summary>
    public async Task<ClientWebSocket> ConnectRawAsync()
    {
        var socket = new ClientWebSocket();
        var fingerprint = Fingerprint;
        socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => CertificateFingerprint.Matches(cert, fingerprint);
        await socket.ConnectAsync(PocketPadClient.BuildUri("127.0.0.1", Port), CancellationToken.None);
        return socket;
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Certificates.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    public static int FreeUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class RecordingInputController : IInputController
{
    private readonly List<string> _calls = [];
    public bool Result { get; set; } = true;

    public IReadOnlyList<string> Calls
    {
        get { lock (_calls) return [.. _calls]; }
    }

    public bool MoveRelative(int dx, int dy)
    {
        lock (_calls) _calls.Add($"move {dx},{dy}");
        return Result;
    }

    public bool Click(MouseButton button, MouseClickAction action)
    {
        lock (_calls) _calls.Add($"{action} {button}");
        return Result;
    }

    public bool Wheel(int delta, bool horizontal)
    {
        lock (_calls) _calls.Add($"wheel {delta} {(horizontal ? "h" : "v")}");
        return Result;
    }

    public bool TypeText(string text)
    {
        lock (_calls) _calls.Add($"text {text}");
        return Result;
    }

    public bool Key(KeyCode key, KeyModifiers modifiers, KeyAction action)
    {
        lock (_calls) _calls.Add($"key {action} {key} {modifiers}");
        return Result;
    }

    public bool Media(MediaCommand command)
    {
        lock (_calls) _calls.Add($"media {command}");
        return Result;
    }
}

internal sealed class TestApproval : IPairingApprovalService
{
    public Func<PairingApprovalRequest, CancellationToken, Task<bool>> Decide { get; set; } = (_, _) => Task.FromResult(false);
    public int Requests;
    public bool WasCancelled;

    public async Task<bool> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        try
        {
            return await Decide(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            throw;
        }
    }
}

internal static class TestExtensions
{
    public static async Task WaitForAsync(this PocketPadClient client, Func<PocketPadClient, bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition(client))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Condition not met. State={client.State}, Failure={client.LastFailure}, Reason={client.LastReason}");
            await Task.Delay(25);
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(25);
        }
    }

    public static Task SendRawAsync(this ClientWebSocket socket, ProtocolMessage message) =>
        socket.SendAsync(MessageSerializer.Serialize(message), WebSocketMessageType.Text, true, CancellationToken.None);

    public static Task SendRawAsync(this ClientWebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Reads messages until the server closes the socket (or timeout). Pings are not answered.</summary>
    public static async Task<(List<ProtocolMessage> Messages, WebSocketCloseStatus? Status)> ReadUntilClosedAsync(
        this ClientWebSocket socket, int timeoutMs = 8000)
    {
        var messages = new List<ProtocolMessage>();
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return (messages, result.CloseStatus);
                var read = MessageSerializer.Deserialize(buffer.AsSpan(0, result.Count), buffer.Length);
                if (read.Message is not null and not PingMessage) messages.Add(read.Message);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return (messages, null);
        }
    }
}

/// <summary>Stands in for the real Windows reader so tests never depend on (or read) the machine's audio/media state.</summary>
internal sealed class FakeMediaStatusProvider : IMediaStatusProvider
{
    private MediaStatusMessage _status = new() { Volume = 40, IsMuted = false, HasSession = false };
    public int Reads;

    public MediaStatusMessage Status
    {
        get { lock (this) return _status; }
        set { lock (this) _status = value; }
    }

    public Task<MediaStatusMessage> GetStatusAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        return Task.FromResult(Status);
    }
}
