using System.Text;
using System.Text.Json;
using PocketPad.Shared.Protocol;

namespace PocketPad.Tests.Protocol;

public class MessageSerializerTests
{
    public static TheoryData<ProtocolMessage> AllMessages() => new(Samples);

    private static readonly ProtocolMessage[] Samples =
    [
        new MouseMoveMessage { Dx = 12.5, Dy = -3 },
        new MouseClickMessage { Button = MouseButton.Right, Action = MouseClickAction.DoubleClick },
        new MouseScrollMessage { Dx = 0, Dy = -120 },
        new KeyboardTextMessage { Text = "Merhaba dünya ğüşiöç" },
        new KeyboardKeyMessage { Key = KeyCode.F5, Modifiers = KeyModifiers.Control | KeyModifiers.Shift, Action = KeyAction.Down },
        new MediaCommandMessage { Command = MediaCommand.VolumeUp },
        new PingMessage { Timestamp = 1_700_000_000_000 },
        new PongMessage { Timestamp = 1_700_000_000_000 },
        new DisconnectMessage { Reason = RejectReason.ServerShuttingDown },
        new ErrorMessage { Reason = RejectReason.Unauthorized, Detail = "nope" },
        new PairRequestMessage { PairingToken = "tok_0123456789abcdef", DeviceName = "Pixel 8" },
        new PairResultMessage { Accepted = true, DeviceId = "dev", DeviceSecret = "sec", ServerId = "srv", ServerName = "PC" },
        new PairResultMessage { Accepted = false, Reason = RejectReason.UserDenied },
        new AuthRequestMessage { DeviceId = "dev", DeviceSecret = "sec", DeviceName = "Pixel 8" },
        new AuthResultMessage { Accepted = false, Reason = RejectReason.Busy },
    ];

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void RoundTrip_PreservesMessage(ProtocolMessage message)
    {
        var bytes = MessageSerializer.Serialize(message);
        var result = MessageSerializer.Deserialize(bytes);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public void Serialize_ProducesVersionedEnvelope()
    {
        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(new MouseMoveMessage { Dx = 1, Dy = 2 }));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(ProtocolInfo.Version, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("mouse.move", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("payload").GetProperty("dx").GetDouble());
    }

    [Fact]
    public void Enums_AreSerializedAsStrings()
    {
        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(new MediaCommandMessage { Command = MediaCommand.PlayPause }));
        Assert.Contains("\"command\":\"PlayPause\"", json);
    }

    [Fact]
    public void AllRegisteredTypesAreCoveredByRoundTripTest()
    {
        var covered = Samples.Select(MessageSerializer.GetTypeName).ToHashSet();
        Assert.Subset(covered, MessageSerializer.KnownTypes.ToHashSet());
    }

    [Theory]
    [InlineData("""{"v":2,"type":"ping","payload":{"timestamp":1}}""", MessageReadError.UnsupportedVersion)]
    [InlineData("""{"v":1,"type":"shell.exec","payload":{}}""", MessageReadError.UnknownType)]
    [InlineData("""{"v":1,"type":"mouse.move","payload":{"dx":1}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"mouse.move","payload":{"dx":1e9,"dy":0}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"mouse.move","payload":[1,2]}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"media.command","payload":{"command":"FormatDisk"}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"media.command","payload":{"command":99}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"keyboard.text","payload":{"text":""}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"keyboard.key","payload":{"key":"Enter","modifiers":64}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"pair.request","payload":{"pairingToken":"x","deviceName":"\u0007bell"}}""", MessageReadError.InvalidPayload)]
    [InlineData("""{"v":1,"type":"ping"}""", MessageReadError.MalformedJson)]
    [InlineData("""not json""", MessageReadError.MalformedJson)]
    public void Deserialize_RejectsInvalidInput(string json, MessageReadError expected)
    {
        var result = MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.False(result.Success);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public void Deserialize_RejectsOversizedMessage()
    {
        var text = new string('a', ProtocolInfo.MaxTextLength);
        var bytes = MessageSerializer.Serialize(new KeyboardTextMessage { Text = text });

        var result = MessageSerializer.Deserialize(bytes, maxBytes: 512);

        Assert.Equal(MessageReadError.TooLarge, result.Error);
    }

    [Fact]
    public void Deserialize_RejectsTooLongText()
    {
        var json = "{\"v\":1,\"type\":\"keyboard.text\",\"payload\":{\"text\":\""
                   + new string('a', ProtocolInfo.MaxTextLength + 1) + "\"}}";
        var result = MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.Equal(MessageReadError.InvalidPayload, result.Error);
    }

    [Fact]
    public void Deserialize_IgnoresUnknownPayloadFields_ForForwardCompatibility()
    {
        var json = """{"v":1,"type":"mouse.move","payload":{"dx":1,"dy":2,"pressure":0.5}}""";
        var result = MessageSerializer.Deserialize(Encoding.UTF8.GetBytes(json));
        Assert.Equal(new MouseMoveMessage { Dx = 1, Dy = 2 }, result.Message);
    }

    [Fact]
    public void ToString_NeverRevealsSecrets()
    {
        ProtocolMessage[] messages =
        [
            new PairRequestMessage { PairingToken = "SECRET-TOKEN-123456", DeviceName = "Pixel" },
            new PairResultMessage { Accepted = true, DeviceId = "d", DeviceSecret = "SECRET-DEVICE", ServerId = "s" },
            new AuthRequestMessage { DeviceId = "d", DeviceSecret = "SECRET-DEVICE", DeviceName = "Pixel" },
            new KeyboardTextMessage { Text = "SECRET-PASSWORD" },
        ];

        foreach (var m in messages)
            Assert.DoesNotContain("SECRET", m.ToString());
    }
}
