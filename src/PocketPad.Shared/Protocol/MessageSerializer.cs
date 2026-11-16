using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PocketPad.Shared.Protocol;

/// <summary>
/// Wire envelope: <c>{"v":1,"type":"mouse.move","payload":{...}}</c>.
/// The version lets both sides reject incompatible peers before touching the payload.
/// </summary>
public sealed record MessageEnvelope
{
    [JsonPropertyName("v")] public required int Version { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("payload")] public required JsonElement Payload { get; init; }
}

public enum MessageReadError
{
    None,
    TooLarge,
    MalformedJson,
    UnsupportedVersion,
    UnknownType,
    InvalidPayload,
}

public readonly record struct MessageReadResult(ProtocolMessage? Message, MessageReadError Error, string? Detail)
{
    public bool Success => Message is not null;
}

/// <summary>Serializes protocol messages into versioned envelopes and validates incoming ones.</summary>
public static class MessageSerializer
{
    private static readonly Dictionary<string, JsonTypeInfo> TypeInfoByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> NameByType = [];

    static MessageSerializer()
    {
        var ctx = ProtocolJsonContext.Default;
        Register("ping", ctx.PingMessage);
        Register("pong", ctx.PongMessage);
        Register("disconnect", ctx.DisconnectMessage);
        Register("error", ctx.ErrorMessage);
        Register("pair.request", ctx.PairRequestMessage);
        Register("pair.result", ctx.PairResultMessage);
        Register("auth.request", ctx.AuthRequestMessage);
        Register("auth.result", ctx.AuthResultMessage);
        Register("mouse.move", ctx.MouseMoveMessage);
        Register("mouse.click", ctx.MouseClickMessage);
        Register("mouse.scroll", ctx.MouseScrollMessage);
        Register("keyboard.text", ctx.KeyboardTextMessage);
        Register("keyboard.key", ctx.KeyboardKeyMessage);
        Register("media.command", ctx.MediaCommandMessage);
    }

    private static void Register<T>(string name, JsonTypeInfo<T> info) where T : ProtocolMessage
    {
        TypeInfoByName.Add(name, info);
        NameByType.Add(typeof(T), name);
    }

    public static IReadOnlyCollection<string> KnownTypes => TypeInfoByName.Keys;

    public static string GetTypeName(ProtocolMessage message) =>
        NameByType.TryGetValue(message.GetType(), out var name)
            ? name
            : throw new ArgumentException($"Unregistered message type {message.GetType().Name}.", nameof(message));

    public static byte[] Serialize(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var name = GetTypeName(message);
        var envelope = new MessageEnvelope
        {
            Version = ProtocolInfo.Version,
            Type = name,
            Payload = JsonSerializer.SerializeToElement(message, TypeInfoByName[name]),
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJsonContext.Default.MessageEnvelope);
    }

    public static MessageReadResult Deserialize(ReadOnlySpan<byte> utf8Json, int maxBytes = ProtocolInfo.MaxMessageBytes)
    {
        if (utf8Json.Length > maxBytes)
            return Fail(MessageReadError.TooLarge, $"Message exceeds {maxBytes} bytes.");

        MessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(utf8Json, ProtocolJsonContext.Default.MessageEnvelope);
        }
        catch (JsonException)
        {
            return Fail(MessageReadError.MalformedJson, "Envelope is not valid JSON.");
        }

        if (envelope is null)
            return Fail(MessageReadError.MalformedJson, "Envelope is empty.");
        if (envelope.Version != ProtocolInfo.Version)
            return Fail(MessageReadError.UnsupportedVersion, $"Unsupported protocol version {envelope.Version}.");
        if (!TypeInfoByName.TryGetValue(envelope.Type, out var typeInfo))
            return Fail(MessageReadError.UnknownType, "Unknown message type.");
        if (envelope.Payload.ValueKind != JsonValueKind.Object)
            return Fail(MessageReadError.InvalidPayload, "Payload must be an object.");

        ProtocolMessage? message;
        try
        {
            message = envelope.Payload.Deserialize(typeInfo) as ProtocolMessage;
        }
        catch (JsonException)
        {
            return Fail(MessageReadError.InvalidPayload, $"Payload of '{envelope.Type}' is invalid.");
        }

        if (message is null)
            return Fail(MessageReadError.InvalidPayload, "Payload is empty.");
        if (message.Validate() is { } error)
            return Fail(MessageReadError.InvalidPayload, error);

        return new MessageReadResult(message, MessageReadError.None, null);
    }

    private static MessageReadResult Fail(MessageReadError error, string detail) => new(null, error, detail);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 16)]
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(PongMessage))]
[JsonSerializable(typeof(DisconnectMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(PairRequestMessage))]
[JsonSerializable(typeof(PairResultMessage))]
[JsonSerializable(typeof(AuthRequestMessage))]
[JsonSerializable(typeof(AuthResultMessage))]
[JsonSerializable(typeof(MouseMoveMessage))]
[JsonSerializable(typeof(MouseClickMessage))]
[JsonSerializable(typeof(MouseScrollMessage))]
[JsonSerializable(typeof(KeyboardTextMessage))]
[JsonSerializable(typeof(KeyboardKeyMessage))]
[JsonSerializable(typeof(MediaCommandMessage))]
[JsonSerializable(typeof(Pairing.PairedServer))]
[JsonSerializable(typeof(List<Pairing.PairedServer>))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
