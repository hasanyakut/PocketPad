namespace PocketPad.Shared.Protocol;

/// <summary>Playback state of the PC's current media session (mirrors Windows' media session states).</summary>
public enum PlaybackState { Unknown, Closed, Opened, Changing, Stopped, Playing, Paused }

/// <summary>
/// PC → phone: what is really going on on the PC, so the remote never pretends a command worked.
/// Volume/mute come from the default audio output; the rest from the current Windows media session
/// (the app that owns the media keys, e.g. Spotify or a browser playing YouTube).
/// </summary>
public sealed record MediaStatusMessage : ProtocolMessage
{
    /// <summary>System volume 0–100, or null when it could not be read.</summary>
    public int? Volume { get; init; }
    public bool? IsMuted { get; init; }

    /// <summary>False when no app currently exposes a media session: play/next/prev keys may then do nothing.</summary>
    public bool HasSession { get; init; }
    public string? SourceApp { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public PlaybackState Playback { get; init; }

    // What the current session says it supports. Meaningful only when HasSession is true.
    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanStop { get; init; }

    public override string? Validate() => FirstError(
        Volume is null or (>= 0 and <= 100) ? null : "Volume out of range.",
        ValidateEnum(Playback),
        MaxLength(SourceApp, nameof(SourceApp)),
        MaxLength(Title, nameof(Title)),
        MaxLength(Artist, nameof(Artist)));

    private static string? MaxLength(string? value, string name) => value is { Length: > 256 } ? $"{name} is too long." : null;
}

/// <summary>PC → phone: whether the media key was actually injected into Windows (not whether an app reacted).</summary>
public sealed record MediaCommandResultMessage : ProtocolMessage
{
    public required MediaCommand Command { get; init; }
    public required bool Delivered { get; init; }

    public override string? Validate() => ValidateEnum(Command);
}
