using PocketPad.Shared.Protocol;

namespace PocketPad.Shared.Input;

/// <summary>
/// Which media buttons the remote should offer, derived from the PC's <see cref="MediaStatusMessage"/>.
/// A button is disabled only when the PC positively reports that the current media app does not support it.
/// Volume keys act on Windows itself and are always available.
/// </summary>
public readonly record struct MediaCapabilities(bool PlayPause, bool Next, bool Previous, bool Stop, bool PlaybackCertain)
{
    public static MediaCapabilities From(MediaStatusMessage? status) => status switch
    {
        // No information (status not received yet / older PC version): offer everything, but not as "certain".
        null => new(true, true, true, true, PlaybackCertain: false),
        // No app owns a media session: Windows still receives the key, yet nothing may react.
        { HasSession: false } => new(true, true, true, true, PlaybackCertain: false),
        _ => new(status.CanPlayPause, status.CanNext, status.CanPrevious, status.CanStop, PlaybackCertain: true),
    };

    public bool IsAvailable(MediaCommand command) => command switch
    {
        MediaCommand.PlayPause => PlayPause,
        MediaCommand.Next => Next,
        MediaCommand.Previous => Previous,
        MediaCommand.Stop => Stop,
        _ => true, // VolumeUp / VolumeDown / Mute
    };
}
