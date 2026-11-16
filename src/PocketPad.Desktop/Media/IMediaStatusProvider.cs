using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Media;

/// <summary>Reads the PC's real media state (system volume, current media session) for the phone's remote.</summary>
public interface IMediaStatusProvider
{
    /// <summary>Never throws: unknown parts are left null / <see cref="PlaybackState.Unknown"/>.</summary>
    Task<MediaStatusMessage> GetStatusAsync(CancellationToken cancellationToken);
}
