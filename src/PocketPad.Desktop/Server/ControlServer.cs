using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PocketPad.Desktop.Input;
using PocketPad.Desktop.Media;
using PocketPad.Desktop.Security;
using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Server;

/// <summary>Dependencies shared by every <see cref="ClientSession"/>.</summary>
internal sealed record ServerContext(
    ServerOptions Options,
    ServerIdentity Identity,
    PairingService Pairing,
    IPairedDeviceStore Devices,
    IPairingApprovalService Approval,
    SessionRegistry Registry,
    InputDispatcher Input,
    TimeProvider Time,
    ILogger Logger,
    MediaStatusPublisher? Media = null);

/// <summary>Kestrel hosted WSS endpoint, running inside the WPF process (current Windows user session).</summary>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly ServerContext _ctx;
    private readonly ICertificateProvider _certificates;
    private readonly ILogger<ControlServer> _logger;
    private WebApplication? _app;
    private CancellationTokenSource? _stopping;
    private DiscoveryResponder? _discovery;

    public ControlServer(
        ServerOptions options,
        ServerIdentity identity,
        ICertificateProvider certificates,
        PairingService pairing,
        IPairedDeviceStore devices,
        IPairingApprovalService approval,
        SessionRegistry registry,
        InputDispatcher input,
        TimeProvider time,
        ILogger<ControlServer> logger,
        MediaStatusPublisher? media = null)
    {
        _certificates = certificates;
        _logger = logger;
        _ctx = new ServerContext(options, identity, pairing, devices, approval, registry, input, time, logger, media);
        registry.ControlSessionChanged += (_, _) => input.Reset();
        devices.Changed += (_, _) => DropRemovedDevices();
    }

    public bool IsRunning => _app is not null;

    /// <summary>Actual listening port (differs from options when port 0 was requested).</summary>
    public int Port { get; private set; }

    public string CertificateFingerprint => _certificates.Fingerprint;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null) return;

        var certificate = _certificates.GetCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = _ctx.Options.MaxConnections + 2;
            kestrel.Limits.MaxConcurrentUpgradedConnections = _ctx.Options.MaxConnections;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Listen(_ctx.Options.ListenAddress, _ctx.Options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = certificate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateMode = ClientCertificateMode.NoCertificate,
                });
            });
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        app.Map(ProtocolInfo.WebSocketPath, HandleWebSocketAsync); // everything else → 404

        _stopping = new CancellationTokenSource();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Port = address is not null ? new Uri(address.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost")).Port : _ctx.Options.Port;
        _logger.LogInformation("PocketPad server listening on port {Port}.", Port);
        _ctx.Media?.Start();

        if (_ctx.Options.DiscoveryPort > 0)
        {
            try
            {
                _discovery = new DiscoveryResponder(_ctx.Options.ListenAddress, _ctx.Options.DiscoveryPort, _ctx.Pairing, _ctx.Time, _logger);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // Discovery is a convenience; QR pairing keeps working without it.
                _logger.LogWarning(ex, "LAN discovery unavailable on UDP {Port}.", _ctx.Options.DiscoveryPort);
            }
        }
    }

    public async Task StopAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;

        if (_ctx.Media is not null) await _ctx.Media.DisposeAsync().ConfigureAwait(false);

        if (_discovery is not null)
        {
            await _discovery.DisposeAsync().ConfigureAwait(false);
            _discovery = null;
        }

        _ctx.Registry.CloseAll(RejectReason.ServerShuttingDown);
        await Task.Delay(200).ConfigureAwait(false); // let close notices go out
        _stopping?.Cancel();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
        _stopping?.Dispose();
        _stopping = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (!LocalNetwork.IsAllowedRemote(remote))
        {
            _logger.LogWarning("Refused connection from non-local address {Remote}.", remote);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var session = new ClientSession(socket, remote!.ToString(), _ctx);
        if (!_ctx.Registry.TryRegister(session))
        {
            session.Close(RejectReason.Busy);
            _logger.LogWarning("Too many connections; refused {Remote}.", remote);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, _stopping?.Token ?? CancellationToken.None);
        try
        {
            await session.RunAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _ctx.Registry.Unregister(session);
        }
    }

    private void DropRemovedDevices()
    {
        var known = _ctx.Devices.GetAll().Select(d => d.DeviceId).ToHashSet();
        foreach (var connection in _ctx.Registry.Snapshot())
        {
            if (connection is { Status: SessionStatus.Controlling, DeviceId: { } id } && !known.Contains(id))
                _ctx.Registry.DisconnectDevice(id, RejectReason.DeviceRemoved);
        }
    }
}
