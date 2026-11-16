namespace PocketPad.Shared.Protocol;

/// <summary>Base type for every payload carried inside a <see cref="MessageEnvelope"/>.</summary>
public abstract record ProtocolMessage
{
    /// <summary>Returns a human readable error when the payload is semantically invalid, otherwise null.</summary>
    public virtual string? Validate() => null;

    protected static string? ValidateDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Device name is required.";
        if (name.Length > ProtocolInfo.MaxDeviceNameLength) return "Device name is too long.";
        if (name.Any(char.IsControl)) return "Device name contains control characters.";
        return null;
    }

    protected static string? ValidateDelta(double value, string name) =>
        double.IsFinite(value) && Math.Abs(value) <= ProtocolInfo.MaxPointerDelta
            ? null
            : $"{name} is out of range.";

    protected static string? ValidateEnum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? null : $"Unknown {typeof(TEnum).Name} value.";

    protected static string? FirstError(params string?[] errors) =>
        errors.FirstOrDefault(e => e is not null);
}

/// <summary>Marker for messages that drive mouse/keyboard/media input. Only accepted from an authenticated control session.</summary>
public interface IInputMessage;
