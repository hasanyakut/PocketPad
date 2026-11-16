using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PocketPad.Shared.Protocol;
using Windows.Media.Control;

namespace PocketPad.Desktop.Media;

/// <summary>
/// System volume/mute via Core Audio (default render endpoint) and the current media session via
/// Windows' <c>GlobalSystemMediaTransportControlsSessionManager</c> — the same session the media keys control.
/// Read-only: this class never changes volume or playback.
/// </summary>
public sealed class WindowsMediaStatusProvider(ILogger<WindowsMediaStatusProvider> logger) : IMediaStatusProvider
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _loggedVolumeFailure, _loggedSessionFailure;

    public async Task<MediaStatusMessage> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (volume, muted) = ReadVolume();
        var status = new MediaStatusMessage { Volume = volume, IsMuted = muted };

        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var session = _manager.GetCurrentSession();
            if (session is null) return status;

            var playback = session.GetPlaybackInfo();
            var controls = playback?.Controls;
            GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
            try { props = await session.TryGetMediaPropertiesAsync(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { /* some apps refuse; status still useful */ }

            return status with
            {
                HasSession = true,
                SourceApp = Truncate(FriendlyAppName(session.SourceAppUserModelId)),
                Title = Truncate(props?.Title),
                Artist = Truncate(props?.Artist),
                Playback = Map(playback?.PlaybackStatus),
                CanPlayPause = controls is not null && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled),
                CanNext = controls?.IsNextEnabled ?? false,
                CanPrevious = controls?.IsPreviousEnabled ?? false,
                CanStop = controls?.IsStopEnabled ?? false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_loggedSessionFailure)
            {
                _loggedSessionFailure = true;
                logger.LogWarning(ex, "Media session information is unavailable.");
            }
            return status;
        }
    }

    private (int? Volume, bool? Muted) ReadVolume()
    {
        try
        {
            var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumeratorComObject();
            try
            {
                enumerator.GetDefaultAudioEndpoint(CoreAudio.ERender, CoreAudio.EMultimedia, out var device);
                var iid = typeof(CoreAudio.IAudioEndpointVolume).GUID;
                device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj);
                var endpoint = (CoreAudio.IAudioEndpointVolume)obj;
                endpoint.GetMasterVolumeLevelScalar(out var level);
                endpoint.GetMute(out var mute);
                Marshal.ReleaseComObject(endpoint);
                Marshal.ReleaseComObject(device);
                return ((int)Math.Round(Math.Clamp(level, 0f, 1f) * 100), mute);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // No audio output device (e.g. headless machine) or the audio service is unavailable.
            if (!_loggedVolumeFailure)
            {
                _loggedVolumeFailure = true;
                logger.LogWarning(ex, "System volume is unavailable.");
            }
            return (null, null);
        }
    }

    internal static string? FriendlyAppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return null;
        if (appUserModelId.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Medya Oynatıcı";

        var id = appUserModelId;
        if (id.Contains('!')) id = id[(id.IndexOf('!') + 1)..];      // packaged apps: "Publisher.App_hash!App"
        id = Path.GetFileName(id);                                    // full paths → file name
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        return id.ToLowerInvariant() switch
        {
            "spotify" => "Spotify",
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" or "308046b0af4a39cb" => "Firefox",
            "opera" => "Opera",
            "brave" => "Brave",
            "vlc" => "VLC",
            _ => id,
        };
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length > 256 ? value[..256] : value;

    private static PlaybackState Map(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackState.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackState.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackState.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
        _ => PlaybackState.Unknown,
    };

    /// <summary>Minimal Core Audio COM declarations (vtable order matters; only the used tail is typed).</summary>
    private static class CoreAudio
    {
        public const int ERender = 0;
        public const int EMultimedia = 1;
        public const int ClsCtxAll = 23;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject;

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume
        {
            void RegisterControlChangeNotify(IntPtr notify);
            void UnregisterControlChangeNotify(IntPtr notify);
            void GetChannelCount(out uint count);
            void SetMasterVolumeLevel(float levelDb, ref Guid context);
            void SetMasterVolumeLevelScalar(float level, ref Guid context);
            void GetMasterVolumeLevel(out float levelDb);
            void GetMasterVolumeLevelScalar(out float level);
            void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
            void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            void GetChannelVolumeLevel(uint channel, out float levelDb);
            void GetChannelVolumeLevelScalar(uint channel, out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
