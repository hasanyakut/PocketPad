using System.Net.WebSockets;
using PocketPad.Desktop.Server;
using PocketPad.Shared.Client;
using PocketPad.Shared.Protocol;

namespace PocketPad.Tests.Integration;

/// <summary>End-to-end tests over real TLS WebSockets (Kestrel on loopback ↔ PocketPadClient).</summary>
public class ConnectionIntegrationTests
{
    [Fact]
    public async Task Pairing_WithUserApproval_IssuesCredentials_AndDeviceCanControl()
    {
        await using var host = await TestHost.StartAsync();
        host.Approval.Decide = (_, _) => Task.FromResult(true);

        await using var client = new PocketPadClient(TestHost.FastClient);
        var credentials = await client.PairAsync(host.Invite(), "Pixel 8", CancellationToken.None);

        Assert.Equal(1, host.Approval.Requests);
        Assert.Equal(host.Identity.ServerId, credentials.ServerId);
        Assert.Equal(host.Fingerprint, credentials.CertificateFingerprint);
        Assert.Single(host.Devices.GetAll());

        var inputs = new List<InputActivity>();
        host.Registry.InputReceived += (_, a) => { lock (inputs) inputs.Add(a); };

        await client.StartAsync(credentials, "Pixel 8");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        Assert.Equal(credentials.DeviceId, host.Registry.ActiveSession?.DeviceId);
        Assert.True(await client.SendAsync(new MouseMoveMessage { Dx = 5, Dy = 5 }));
        Assert.True(await client.SendAsync(new MediaCommandMessage { Command = MediaCommand.PlayPause }));
        await TestExtensions.WaitUntilAsync(() => { lock (inputs) return inputs.Count == 2; });
        lock (inputs) Assert.Equal(["mouse.move", "media.command"], inputs.Select(i => i.MessageType));
    }

    [Fact]
    public async Task AuthorizedTouchpadInput_ReachesInputController_InOrder()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        await client.SendAsync(new MouseMoveMessage { Dx = 10.6, Dy = -3.2 });
        await client.SendAsync(new MouseMoveMessage { Dx = 0.6, Dy = -0.9 });
        await client.SendAsync(new MouseClickMessage { Button = MouseButton.Left });
        await client.SendAsync(new MouseClickMessage { Button = MouseButton.Right });
        await client.SendAsync(new MouseClickMessage { Button = MouseButton.Left, Action = MouseClickAction.DoubleClick });

        await TestExtensions.WaitUntilAsync(() => host.Input.Calls.Count == 5);
        // Fractions carry over: 10.6 → 10 (+0.6), then 0.6 + 0.6 → 1; -3.2 → -3 (-0.2), then -0.9 - 0.2 → -1.
        Assert.Equal(["move 10,-3", "move 1,-1", "Click Left", "Click Right", "DoubleClick Left"], host.Input.Calls);
    }

    [Fact]
    public async Task KeyboardAndMedia_ReachInputController_AndHeldKeysAreReleasedOnDisconnect()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        await client.SendAsync(new KeyboardTextMessage { Text = "Merhaba ğüşıöç" });
        await client.SendAsync(new KeyboardKeyMessage { Key = KeyCode.Enter });
        await client.SendAsync(new MediaCommandMessage { Command = MediaCommand.PlayPause });
        await client.SendAsync(new KeyboardKeyMessage { Key = KeyCode.Down, Modifiers = KeyModifiers.Shift, Action = KeyAction.Down });
        await TestExtensions.WaitUntilAsync(() => host.Input.Calls.Count == 4);

        await client.StopAsync();
        await TestExtensions.WaitUntilAsync(() => host.Input.Calls.Count == 5);

        Assert.Equal(
            ["text Merhaba ğüşıöç", "key Press Enter None", "media PlayPause", "key Down Down Shift", "key Up Down Shift"],
            host.Input.Calls);
    }

    [Fact]
    public async Task TwoFingerScroll_ReachesInputController_AsWheel_WithFractionCarry()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        await client.SendAsync(new MouseScrollMessage { Dy = 60.5 });
        await client.SendAsync(new MouseScrollMessage { Dy = 60.5 });
        await client.SendAsync(new MouseScrollMessage { Dy = -120 });
        await client.SendAsync(new MouseClickMessage { Button = MouseButton.Left }); // clicks keep working alongside scroll

        await TestExtensions.WaitUntilAsync(() => host.Input.Calls.Count == 4);
        Assert.Equal(["wheel 60 v", "wheel 61 v", "wheel -120 v", "Click Left"], host.Input.Calls);
    }

    [Fact]
    public async Task InputController_IsNeverCalled_ForUnauthenticatedOrRejectedDevices()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        using (var raw = await host.ConnectRawAsync())
        {
            await raw.SendRawAsync(new MouseClickMessage { Button = MouseButton.Left });
            await raw.ReadUntilClosedAsync();
        }

        using (var raw = await host.ConnectRawAsync())
        {
            await raw.SendRawAsync(new AuthRequestMessage { DeviceId = credentials.DeviceId, DeviceSecret = "wrong", DeviceName = "x" });
            await raw.SendRawAsync(new MouseMoveMessage { Dx = 50, Dy = 50 });
            await raw.ReadUntilClosedAsync();
        }

        Assert.Empty(host.Input.Calls);
    }

    [Fact]
    public async Task BusyDevice_CannotInjectInput()
    {
        await using var host = await TestHost.StartAsync();
        var phoneA = await host.PairDeviceAsync("A");
        var phoneB = await host.PairDeviceAsync("B");

        await using var a = new PocketPadClient(TestHost.FastClient);
        await a.StartAsync(phoneA, "A");
        await a.WaitForAsync(c => c.State == ConnectionState.Connected);

        using var raw = await host.ConnectRawAsync();
        await raw.SendRawAsync(new AuthRequestMessage { DeviceId = phoneB.DeviceId, DeviceSecret = phoneB.DeviceSecret, DeviceName = "B" });
        await raw.SendRawAsync(new MouseMoveMessage { Dx = 50, Dy = 50 });
        var (messages, _) = await raw.ReadUntilClosedAsync();

        Assert.Contains(messages, m => m is AuthResultMessage { Accepted: false, Reason: RejectReason.Busy });
        Assert.Empty(host.Input.Calls);
    }

    [Fact]
    public async Task Pairing_DeniedByUser_IsRejected_AndNothingIsStored()
    {
        await using var host = await TestHost.StartAsync();
        host.Approval.Decide = (_, _) => Task.FromResult(false);

        await using var client = new PocketPadClient(TestHost.FastClient);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(host.Invite(), "Pixel", CancellationToken.None));

        Assert.Equal(RejectReason.UserDenied, ex.Reason);
        Assert.Empty(host.Devices.GetAll());
    }

    [Fact]
    public async Task Pairing_WithoutUserResponse_TimesOut_AndNothingIsStored()
    {
        await using var host = await TestHost.StartAsync(o => o with { ApprovalTimeout = TimeSpan.FromMilliseconds(500) });
        host.Approval.Decide = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return true; };

        await using var client = new PocketPadClient(TestHost.FastClient);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(host.Invite(), "Pixel", CancellationToken.None));

        Assert.Equal(RejectReason.ApprovalTimeout, ex.Reason);
        Assert.True(host.Approval.WasCancelled, "Approval prompt must be closed when it times out.");
        Assert.Empty(host.Devices.GetAll());
    }

    [Fact]
    public async Task Pairing_QrCodeIsSingleUse()
    {
        await using var host = await TestHost.StartAsync();
        host.Approval.Decide = (_, _) => Task.FromResult(true);
        var qr = host.Invite();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.PairAsync(qr, "First", CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(qr, "Second", CancellationToken.None));

        Assert.Equal(RejectReason.InvalidPairingToken, ex.Reason);
        Assert.Equal(1, host.Approval.Requests); // the user is never asked for a replayed code
        Assert.Single(host.Devices.GetAll());
    }

    [Fact]
    public async Task Pairing_OldQrCodeIsInvalidAfterNewOneIsShown()
    {
        await using var host = await TestHost.StartAsync();
        host.Approval.Decide = (_, _) => Task.FromResult(true);
        var old = host.Invite();
        host.Invite();

        await using var client = new PocketPadClient(TestHost.FastClient);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(old, "Pixel", CancellationToken.None));

        Assert.Equal(RejectReason.InvalidPairingToken, ex.Reason);
        Assert.Equal(0, host.Approval.Requests);
    }

    [Fact]
    public async Task Pairing_ExpiredQrCodeIsRejected()
    {
        await using var host = await TestHost.StartAsync(o => o with { PairingTokenLifetime = TimeSpan.FromMilliseconds(300) });
        host.Approval.Decide = (_, _) => Task.FromResult(true);
        var qr = host.Invite();
        await Task.Delay(600);

        await using var client = new PocketPadClient(TestHost.FastClient);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(qr, "Pixel", CancellationToken.None));
        Assert.Equal(RejectReason.InvalidPairingToken, ex.Reason);
    }

    [Fact]
    public async Task WrongCertificate_IsRejected_DuringPairing()
    {
        await using var host = await TestHost.StartAsync();
        host.Approval.Decide = (_, _) => Task.FromResult(true);
        var qr = host.Invite() with { CertificateFingerprint = new string('0', 64) };

        await using var client = new PocketPadClient(TestHost.FastClient);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.PairAsync(qr, "Pixel", CancellationToken.None));

        Assert.Equal(ConnectionFailure.CertificateMismatch, ex.Failure);
        Assert.Equal(0, host.Approval.Requests); // token never left the phone
    }

    [Fact]
    public async Task WrongCertificate_IsRejected_OnReconnect_AndNotRetried()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials with { CertificateFingerprint = new string('F', 64) }, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Disconnected && c.LastFailure == ConnectionFailure.CertificateMismatch);

        Assert.Null(host.Registry.ActiveSession);
    }

    [Fact]
    public async Task UnpairedDevice_IsRejected()
    {
        await using var host = await TestHost.StartAsync();
        var real = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(real with { DeviceId = Guid.NewGuid().ToString("N") }, "Intruder");
        await client.WaitForAsync(c => c.State == ConnectionState.Disconnected && c.LastReason == RejectReason.UnknownDevice);
        Assert.Null(host.Registry.ActiveSession);

        await using var client2 = new PocketPadClient(TestHost.FastClient);
        await client2.StartAsync(real with { DeviceSecret = "wrong-secret" }, "Intruder");
        await client2.WaitForAsync(c => c.State == ConnectionState.Disconnected && c.LastReason == RejectReason.UnknownDevice);
        Assert.Null(host.Registry.ActiveSession);
    }

    [Fact]
    public async Task InputBeforeAuthentication_IsRefused_AndConnectionClosed()
    {
        await using var host = await TestHost.StartAsync();
        var inputs = 0;
        host.Registry.InputReceived += (_, _) => Interlocked.Increment(ref inputs);

        using var socket = await host.ConnectRawAsync();
        await socket.SendRawAsync(new MouseMoveMessage { Dx = 100, Dy = 100 });
        var (messages, status) = await socket.ReadUntilClosedAsync();

        Assert.Contains(messages, m => m is ErrorMessage { Reason: RejectReason.Unauthorized });
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Equal(0, inputs);
    }

    [Fact]
    public async Task ConnectionThatNeverAuthenticates_IsClosedAfterHandshakeTimeout()
    {
        await using var host = await TestHost.StartAsync(o => o with { HandshakeTimeout = TimeSpan.FromMilliseconds(500) });
        using var socket = await host.ConnectRawAsync();

        var (_, status) = await socket.ReadUntilClosedAsync(timeoutMs: 5000);

        Assert.NotNull(status);
        await TestExtensions.WaitUntilAsync(() => host.Registry.Snapshot().Count == 0);
    }

    [Fact]
    public async Task OversizedOrMalformedMessages_CloseTheConnection()
    {
        await using var host = await TestHost.StartAsync();

        using (var socket = await host.ConnectRawAsync())
        {
            await socket.SendAsync(new byte[ProtocolInfo.MaxMessageBytes + 100], WebSocketMessageType.Text, true, CancellationToken.None);
            var (_, status) = await socket.ReadUntilClosedAsync();
            Assert.Equal(WebSocketCloseStatus.MessageTooBig, status);
        }

        using (var socket = await host.ConnectRawAsync())
        {
            await socket.SendRawAsync("""{"v":1,"type":"shell.exec","payload":{}}""");
            var (messages, status) = await socket.ReadUntilClosedAsync();
            Assert.Contains(messages, m => m is ErrorMessage { Reason: RejectReason.ProtocolError });
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        }
    }

    [Fact]
    public async Task OnlyOneControlSession_SecondDeviceGetsBusy()
    {
        await using var host = await TestHost.StartAsync();
        var phoneA = await host.PairDeviceAsync("Phone A");
        var phoneB = await host.PairDeviceAsync("Phone B");

        await using var a = new PocketPadClient(TestHost.FastClient);
        await a.StartAsync(phoneA, "Phone A");
        await a.WaitForAsync(c => c.State == ConnectionState.Connected);

        await using var b = new PocketPadClient(TestHost.FastClient);
        await b.StartAsync(phoneB, "Phone B");
        await b.WaitForAsync(c => c.LastReason == RejectReason.Busy);

        Assert.NotEqual(ConnectionState.Connected, b.State);
        Assert.Equal(phoneA.DeviceId, host.Registry.ActiveSession?.DeviceId);

        // When A leaves, B's automatic retry takes over.
        await a.StopAsync();
        await b.WaitForAsync(c => c.State == ConnectionState.Connected);
        Assert.Equal(phoneB.DeviceId, host.Registry.ActiveSession?.DeviceId);
    }

    [Fact]
    public async Task ServerDetectsGracefulClientDisconnect()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);
        await TestExtensions.WaitUntilAsync(() => host.Registry.ActiveSession is not null);

        await client.StopAsync();

        Assert.Equal(ConnectionState.Disconnected, client.State);
        await TestExtensions.WaitUntilAsync(() => host.Registry.Snapshot().Count == 0, timeoutMs: 3000);
    }

    [Fact]
    public async Task ServerDetectsSilentClient_ViaHeartbeatTimeout()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        // Authenticates, then never answers pings (e.g. phone lost Wi-Fi without closing TCP).
        using var socket = await host.ConnectRawAsync();
        await socket.SendRawAsync(new AuthRequestMessage { DeviceId = credentials.DeviceId, DeviceSecret = credentials.DeviceSecret, DeviceName = "Silent" });
        await TestExtensions.WaitUntilAsync(() => host.Registry.ActiveSession is not null);

        var (messages, _) = await socket.ReadUntilClosedAsync(timeoutMs: 5000);

        Assert.Contains(messages, m => m is AuthResultMessage { Accepted: true });
        await TestExtensions.WaitUntilAsync(() => host.Registry.ActiveSession is null, timeoutMs: 3000);
    }

    [Fact]
    public async Task ClientDetectsServerLoss_AndReconnectsAutomatically()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();
        var states = new List<ConnectionState>();

        await using var client = new PocketPadClient(TestHost.FastClient);
        client.StateChanged += (_, e) => { lock (states) states.Add(e.State); };
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        await host.Server.StopAsync();
        await client.WaitForAsync(c => c.State == ConnectionState.Reconnecting);

        await host.Server.StartAsync();
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        Assert.Equal(credentials.DeviceId, host.Registry.ActiveSession?.DeviceId);
        lock (states)
        {
            var lost = states.IndexOf(ConnectionState.Reconnecting);
            Assert.True(lost > 0);
            Assert.Contains(ConnectionState.Connected, states.Skip(lost));
        }
    }

    [Fact]
    public async Task RemovingPairedDevice_DisconnectsIt_AndStopsReconnecting()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        host.Devices.Remove(credentials.DeviceId);

        await client.WaitForAsync(c => c.State == ConnectionState.Disconnected && c.LastReason == RejectReason.DeviceRemoved);
        Assert.Null(host.Registry.ActiveSession);
    }

    [Fact]
    public async Task SameDeviceReconnecting_ReplacesItsStaleSession()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();

        using var stale = await host.ConnectRawAsync();
        await stale.SendRawAsync(new AuthRequestMessage { DeviceId = credentials.DeviceId, DeviceSecret = credentials.DeviceSecret, DeviceName = "Pixel" });
        await TestExtensions.WaitUntilAsync(() => host.Registry.ActiveSession is not null);
        var staleId = host.Registry.ActiveSession!.Id;

        await using var client = new PocketPadClient(TestHost.FastClient);
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);

        Assert.NotEqual(staleId, host.Registry.ActiveSession?.Id);
        var (messages, _) = await stale.ReadUntilClosedAsync(timeoutMs: 3000);
        Assert.Contains(messages, m => m is DisconnectMessage { Reason: RejectReason.Replaced });
    }
}
