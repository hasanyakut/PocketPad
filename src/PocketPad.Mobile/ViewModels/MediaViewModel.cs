using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PocketPad.Mobile.Services;
using PocketPad.Shared.Input;
using PocketPad.Shared.Protocol;

namespace PocketPad.Mobile.ViewModels;

/// <summary>
/// Media remote. Shows only what the PC reports: the real system volume/mute state and the current media session.
/// A press is reported as "delivered to Windows" (or not) — never as "worked", because whether an app reacts
/// is only visible in the next status update.
/// </summary>
public sealed partial class MediaViewModel : InputViewModelBase
{
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(3);

    private readonly IConnectionService _connection;
    private readonly IHapticFeedback _haptics;
    private MediaStatusMessage? _status;
    private CancellationTokenSource? _feedbackCts;

    public MediaViewModel(IConnectionService connection, ConnectionStatusViewModel status, IHapticFeedback haptics)
        : base(connection, status)
    {
        _connection = connection;
        _haptics = haptics;
        _status = connection.LastMediaStatus; // the PC may have sent it before this page was first opened
        _connection.MessageReceived += (_, message) => MainThread.BeginInvokeOnMainThread(() => OnMessage(message));
        Status.PropertyChanged += OnStatusChanged;
        Refresh();
    }

    // Now playing
    [ObservableProperty] public partial bool HasStatus { get; set; }
    [ObservableProperty] public partial string NowPlayingTitle { get; set; } = "";
    [ObservableProperty] public partial string NowPlayingSubtitle { get; set; } = "";
    [ObservableProperty] public partial string PlaybackText { get; set; } = "";
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial string? SupportNote { get; set; }

    // Volume
    [ObservableProperty] public partial bool VolumeKnown { get; set; }
    [ObservableProperty] public partial double VolumeLevel { get; set; }
    [ObservableProperty] public partial string VolumeText { get; set; } = "—";
    [ObservableProperty] public partial bool IsMuted { get; set; }

    // Button availability (connected AND supported by the current media app)
    [ObservableProperty] public partial bool PlayPauseEnabled { get; set; }
    [ObservableProperty] public partial bool NextEnabled { get; set; }
    [ObservableProperty] public partial bool PreviousEnabled { get; set; }
    [ObservableProperty] public partial bool StopEnabled { get; set; }
    [ObservableProperty] public partial bool VolumeEnabled { get; set; }

    // Result of the last press
    [ObservableProperty] public partial string? Feedback { get; set; }
    [ObservableProperty] public partial bool FeedbackIsError { get; set; }

    public string PlayPauseIcon => IsPlaying ? "ic_pause.png" : "ic_play.png";
    public string MuteText => IsMuted ? "Sesi aç" : "Sessiz";
    public string VolumeIcon => IsMuted ? "ic_volume_off.png" : "ic_volume_on.png";

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseIcon));

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MuteText));
        OnPropertyChanged(nameof(VolumeIcon));
    }

    [RelayCommand]
    private async Task SendMediaAsync(MediaCommand command)
    {
        TryHaptic();

        if (!Status.IsConnected)
        {
            ShowFeedback("Bağlı değil — komut gönderilmedi.", error: true);
            return;
        }
        if (!MediaCapabilities.From(_status).IsAvailable(command))
        {
            ShowFeedback($"{_status?.SourceApp ?? "Bu uygulama"} bu komutu desteklemiyor.", error: true);
            return;
        }
        if (!await SendAsync(new MediaCommandMessage { Command = command }))
            ShowFeedback("Komut gönderilemedi.", error: true);
        // Otherwise wait for MediaCommandResultMessage: only the PC knows whether Windows accepted the key.
    }

    private void OnMessage(ProtocolMessage message)
    {
        switch (message)
        {
            case MediaStatusMessage status:
                _status = status;
                Refresh();
                break;
            case MediaCommandResultMessage result:
                ShowResult(result);
                break;
        }
    }

    private void ShowResult(MediaCommandResultMessage result)
    {
        if (!result.Delivered)
        {
            ShowFeedback("Windows tuşu kabul etmedi (ekran kilitli veya yönetici penceresi önde olabilir).", error: true);
            return;
        }

        var isPlaybackKey = result.Command is MediaCommand.PlayPause or MediaCommand.Next or MediaCommand.Previous or MediaCommand.Stop;
        if (isPlaybackKey && _status is { HasSession: false })
            ShowFeedback("Tuş Windows'a iletildi, ancak şu an medya çalan bir uygulama görünmüyor.", error: true);
        else if (isPlaybackKey && _status?.SourceApp is { } app)
            ShowFeedback($"Windows'a iletildi · {app}", error: false);
        else
            ShowFeedback("Windows'a iletildi", error: false);
    }

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionStatusViewModel.IsConnected)) return;
        if (!Status.IsConnected) _status = null; // stale PC state must not be shown after a disconnect
        Refresh();
    }

    private void Refresh()
    {
        var connected = Status.IsConnected;
        var status = connected ? _status : null;
        var caps = MediaCapabilities.From(status);

        HasStatus = status is not null;
        PlayPauseEnabled = connected && caps.PlayPause;
        NextEnabled = connected && caps.Next;
        PreviousEnabled = connected && caps.Previous;
        StopEnabled = connected && caps.Stop;
        VolumeEnabled = connected;

        // Volume
        VolumeKnown = status?.Volume is not null;
        IsMuted = status?.IsMuted == true;
        VolumeLevel = (status?.Volume ?? 0) / 100.0;
        VolumeText = status?.Volume is { } v ? (IsMuted ? $"Sessiz · %{v}" : $"%{v}") : "—";

        // Now playing
        IsPlaying = status?.Playback == PlaybackState.Playing;
        if (!connected)
        {
            NowPlayingTitle = "Bağlı değil";
            NowPlayingSubtitle = "Bir bilgisayara bağlanınca çalan medya burada görünür.";
            PlaybackText = "";
            SupportNote = null;
            return;
        }
        if (status is null)
        {
            NowPlayingTitle = "Medya bilgisi bekleniyor";
            NowPlayingSubtitle = "Bilgisayardaki PocketPad güncel değilse bu bilgi gelmeyebilir; tuşlar yine gönderilir.";
            PlaybackText = "";
            SupportNote = null;
            return;
        }
        if (!status.HasSession)
        {
            NowPlayingTitle = "Şu an çalan medya yok";
            NowPlayingSubtitle = "Oynatma tuşları Windows'a gönderilir ama tepki veren bir uygulama olmayabilir.";
            PlaybackText = "";
            SupportNote = null;
            return;
        }

        NowPlayingTitle = status.Title ?? status.SourceApp ?? "Bilinmeyen medya";
        NowPlayingSubtitle = string.Join(" · ", new[] { status.Artist, status.SourceApp }.Where(s => !string.IsNullOrWhiteSpace(s)));
        PlaybackText = status.Playback switch
        {
            PlaybackState.Playing => "Çalıyor",
            PlaybackState.Paused => "Duraklatıldı",
            PlaybackState.Stopped => "Durduruldu",
            PlaybackState.Changing => "Değişiyor",
            _ => "",
        };

        var unsupported = new List<string>();
        if (!caps.PlayPause) unsupported.Add("oynat/duraklat");
        if (!caps.Previous) unsupported.Add("önceki");
        if (!caps.Next) unsupported.Add("sonraki");
        SupportNote = unsupported.Count == 0
            ? null
            : $"{status.SourceApp ?? "Bu uygulama"} şu an desteklemiyor: {string.Join(", ", unsupported)}";
    }

    private void ShowFeedback(string text, bool error)
    {
        Feedback = text;
        FeedbackIsError = error;
        _feedbackCts?.Cancel();
        var cts = _feedbackCts = new CancellationTokenSource();
        _ = Task.Delay(FeedbackDuration, cts.Token).ContinueWith(
            _ => MainThread.BeginInvokeOnMainThread(() => { if (!cts.IsCancellationRequested) Feedback = null; }),
            cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void TryHaptic()
    {
        try { _haptics.Perform(HapticFeedbackType.Click); }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException) { }
    }
}
