using Microsoft.Extensions.Logging.Abstractions;
using PocketPad.Desktop.Input;
using PocketPad.Desktop.Media;
using PocketPad.Shared.Client;
using PocketPad.Shared.Input;
using PocketPad.Shared.Protocol;

namespace PocketPad.Tests.Integration;

/// <summary>Phase 6: media remote end to end (fake media provider, recording input controller).</summary>
public class MediaRemoteTests
{
    private static async Task<(TestHost Host, PocketPadClient Client, List<ProtocolMessage> Received)> ConnectAsync()
    {
        var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();
        var client = new PocketPadClient(TestHost.FastClient);
        var received = new List<ProtocolMessage>();
        client.MessageReceived += (_, m) => { lock (received) received.Add(m); };
        await client.StartAsync(credentials, "Pixel");
        await client.WaitForAsync(c => c.State == ConnectionState.Connected);
        return (host, client, received);
    }

    private static List<T> Of<T>(List<ProtocolMessage> received)
    {
        lock (received) return received.OfType<T>().ToList();
    }

    [Fact]
    public async Task PhoneReceivesRealStatus_AsSoonAsItTakesControl()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await using var __ = client;

        await TestExtensions.WaitUntilAsync(() => Of<MediaStatusMessage>(received).Count > 0);
        var status = Of<MediaStatusMessage>(received)[0];
        Assert.Equal(40, status.Volume);
        Assert.False(status.HasSession);
    }

    [Fact]
    public async Task EveryMediaCommand_IsInjectedAsKeyPress_AndConfirmedAsDelivered()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await using var __ = client;

        MediaCommand[] all = [MediaCommand.VolumeUp, MediaCommand.VolumeDown, MediaCommand.Mute,
                              MediaCommand.PlayPause, MediaCommand.Next, MediaCommand.Previous];
        foreach (var command in all) await client.SendAsync(new MediaCommandMessage { Command = command });

        await TestExtensions.WaitUntilAsync(() => Of<MediaCommandResultMessage>(received).Count == all.Length);
        Assert.Equal(all.Select(c => $"media {c}"), host.Input.Calls);
        Assert.Equal(all, Of<MediaCommandResultMessage>(received).Select(r => r.Command));
        Assert.All(Of<MediaCommandResultMessage>(received), r => Assert.True(r.Delivered));
    }

    [Fact]
    public async Task RejectedKey_IsReportedAsNotDelivered()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await using var __ = client;
        host.Input.Result = false; // e.g. lock screen: SendInput injects nothing

        await client.SendAsync(new MediaCommandMessage { Command = MediaCommand.PlayPause });

        await TestExtensions.WaitUntilAsync(() => Of<MediaCommandResultMessage>(received).Count == 1);
        Assert.False(Of<MediaCommandResultMessage>(received)[0].Delivered);
    }

    [Fact]
    public async Task StatusIsRefreshedAfterACommand_SoThePhoneSeesTheRealOutcome()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await using var __ = client;
        await TestExtensions.WaitUntilAsync(() => Of<MediaStatusMessage>(received).Count == 1);

        // Windows raised the volume after the key press; the next status must carry the new value.
        host.MediaProvider.Status = host.MediaProvider.Status with { Volume = 42 };
        await client.SendAsync(new MediaCommandMessage { Command = MediaCommand.VolumeUp });

        await TestExtensions.WaitUntilAsync(() => Of<MediaStatusMessage>(received).Any(s => s.Volume == 42), timeoutMs: 3000);
    }

    [Fact]
    public async Task UnchangedStatus_IsNotResent()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await using var __ = client;
        await TestExtensions.WaitUntilAsync(() => Of<MediaStatusMessage>(received).Count == 1);

        var reads = host.MediaProvider.Reads;
        await Task.Delay(600); // several 100 ms polls
        Assert.True(host.MediaProvider.Reads > reads);
        Assert.Single(Of<MediaStatusMessage>(received));
    }

    [Fact]
    public async Task RapidRepeatedPresses_AreAllDelivered_InOrder()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await using var __ = client;

        for (var i = 0; i < 30; i++)
            await client.SendAsync(new MediaCommandMessage { Command = i % 3 == 0 ? MediaCommand.Mute : MediaCommand.VolumeUp });

        await TestExtensions.WaitUntilAsync(() => Of<MediaCommandResultMessage>(received).Count == 30);
        Assert.Equal(Enumerable.Range(0, 30).Select(i => i % 3 == 0 ? "media Mute" : "media VolumeUp"), host.Input.Calls);
    }

    [Fact]
    public async Task AfterDisconnect_NoStatusIsReadOrSent_AndCommandsAreNotSent()
    {
        var (host, client, received) = await ConnectAsync();
        await using var _ = host;
        await client.StopAsync();
        await TestExtensions.WaitUntilAsync(() => host.Registry.ActiveSession is null);

        var reads = host.MediaProvider.Reads;
        await Task.Delay(400);
        Assert.Equal(reads, host.MediaProvider.Reads);
        Assert.False(await client.SendAsync(new MediaCommandMessage { Command = MediaCommand.PlayPause }));
        Assert.Empty(host.Input.Calls);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task PhoneCannotSendServerOnlyMessages()
    {
        await using var host = await TestHost.StartAsync();
        var credentials = await host.PairDeviceAsync();
        using var raw = await host.ConnectRawAsync();
        await raw.SendRawAsync(new AuthRequestMessage { DeviceId = credentials.DeviceId, DeviceSecret = credentials.DeviceSecret, DeviceName = "x" });
        await raw.SendRawAsync(new MediaStatusMessage { Volume = 100 });

        var (messages, _) = await raw.ReadUntilClosedAsync();
        Assert.Contains(messages, m => m is ErrorMessage { Reason: RejectReason.Unauthorized });
    }
}

public class MediaUnitTests
{
    [Theory]
    [InlineData(MediaCommand.VolumeUp, 0xAF)]
    [InlineData(MediaCommand.VolumeDown, 0xAE)]
    [InlineData(MediaCommand.Mute, 0xAD)]
    [InlineData(MediaCommand.PlayPause, 0xB3)]
    [InlineData(MediaCommand.Next, 0xB0)]
    [InlineData(MediaCommand.Previous, 0xB1)]
    public void MediaCommand_IsSentAsVirtualKeyDownThenUp(MediaCommand command, int vk)
    {
        Assert.Equal(vk, WindowsInputController.MediaVirtualKey(command));
        var inputs = WindowsInputController.BuildVirtualKeyPress((ushort)vk);

        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, i => Assert.Equal(vk, i.U.ki.wVk));
        Assert.Equal(0u, inputs[0].U.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP);             // down
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP); // up
        Assert.All(inputs, i => Assert.Equal(NativeMethods.KEYEVENTF_EXTENDEDKEY, i.U.ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY));
    }

    [Fact]
    public void Capabilities_UnknownStatus_OffersEverything_ButNotAsCertain()
    {
        var caps = MediaCapabilities.From(null);
        Assert.True(caps.PlayPause && caps.Next && caps.Previous);
        Assert.False(caps.PlaybackCertain);
    }

    [Fact]
    public void Capabilities_NoSession_KeysStillSent_ButNotCertain()
    {
        var caps = MediaCapabilities.From(new MediaStatusMessage { HasSession = false, Volume = 20 });
        Assert.True(caps.IsAvailable(MediaCommand.Next));
        Assert.False(caps.PlaybackCertain);
    }

    [Fact]
    public void Capabilities_SessionWithoutNextPrevious_DisablesOnlyThose()
    {
        // e.g. a YouTube video without a playlist: play/pause works, next/previous are not offered by the page.
        var caps = MediaCapabilities.From(new MediaStatusMessage
        {
            HasSession = true, SourceApp = "Google Chrome", CanPlayPause = true, CanNext = false, CanPrevious = false,
        });

        Assert.True(caps.IsAvailable(MediaCommand.PlayPause));
        Assert.False(caps.IsAvailable(MediaCommand.Next));
        Assert.False(caps.IsAvailable(MediaCommand.Previous));
        Assert.True(caps.IsAvailable(MediaCommand.VolumeUp));   // volume is Windows' own, always available
        Assert.True(caps.IsAvailable(MediaCommand.Mute));
        Assert.True(caps.PlaybackCertain);
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Chrome", "Google Chrome")]
    [InlineData("MSEdge", "Microsoft Edge")]
    [InlineData("308046B0AF4A39CB", "Firefox")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Medya Oynatıcı")]
    [InlineData("C:\\Tools\\foobar2000.exe", "foobar2000")]
    [InlineData(null, null)]
    public void FriendlyAppName_MapsKnownPlayers(string? id, string? expected) =>
        Assert.Equal(expected, WindowsMediaStatusProvider.FriendlyAppName(id));

    [Fact]
    public void MediaStatus_RejectsOutOfRangeVolume_AndOverlongText()
    {
        Assert.NotNull(new MediaStatusMessage { Volume = 101 }.Validate());
        Assert.NotNull(new MediaStatusMessage { Title = new string('x', 300) }.Validate());
        Assert.Null(new MediaStatusMessage { Volume = 0, Title = "Song" }.Validate());
    }

    /// <summary>Read-only check against this machine's real audio/media state. Never changes volume or playback.</summary>
    [Fact]
    public async Task RealWindowsProvider_ReadsWithoutThrowing()
    {
        var provider = new WindowsMediaStatusProvider(NullLogger<WindowsMediaStatusProvider>.Instance);
        var status = await provider.GetStatusAsync(CancellationToken.None);

        Assert.Null(status.Validate());
        Assert.True(status.Volume is null or (>= 0 and <= 100));
        Assert.Equal(status.Volume is null, status.IsMuted is null);
    }
}
