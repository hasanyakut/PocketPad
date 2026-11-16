using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PocketPad.Mobile.Services;
using PocketPad.Shared.Client;
using PocketPad.Shared.Input;
using PocketPad.Shared.Protocol;

namespace PocketPad.Mobile.ViewModels;

/// <summary>Shared base: every input page shows the connection indicator and sends over the control session.</summary>
public abstract class InputViewModelBase(IConnectionService connection, ConnectionStatusViewModel status) : ObservableObject
{
    public ConnectionStatusViewModel Status { get; } = status;

    protected ValueTask<bool> SendAsync(ProtocolMessage message) => connection.SendAsync(message);
}

/// <summary>
/// Touchpad: gestures are handed to an <see cref="InputSender"/> which merges moves to ~60 Hz on a
/// background task, so the UI thread never waits on the network. Nothing is sent while disconnected.
/// </summary>
public sealed partial class TouchpadViewModel : InputViewModelBase
{
    public const double MinSensitivity = TouchpadSettings.MinSensitivity;
    public const double MaxSensitivity = TouchpadSettings.MaxSensitivity;
    public const double MinScrollSpeed = TouchpadSettings.MinScrollSpeed;
    public const double MaxScrollSpeed = TouchpadSettings.MaxScrollSpeed;
    private const string SensitivityKey = "touchpad.sensitivity";
    private const string ScrollSpeedKey = "touchpad.scrollSpeed";
    private const string ReverseScrollKey = "touchpad.reverseScroll";

    private readonly IPreferences _preferences;
    private readonly InputSender _sender;
    private TouchpadSettings _settings;

    public TouchpadViewModel(IConnectionService connection, ConnectionStatusViewModel status, IPreferences preferences)
        : base(connection, status)
    {
        _preferences = preferences;
        _sender = new InputSender(connection.SendAsync, () => connection.State == ConnectionState.Connected);
        _settings = new TouchpadSettings
        {
            MouseSensitivity = preferences.Get(SensitivityKey, TouchpadSettings.DefaultSensitivity),
            ScrollSpeed = preferences.Get(ScrollSpeedKey, TouchpadSettings.DefaultScrollSpeed),
            ReverseScroll = preferences.Get(ReverseScrollKey, false),
        }.Normalized();
        Sensitivity = _settings.MouseSensitivity;
        ScrollSpeed = _settings.ScrollSpeed;
        ReverseScroll = _settings.ReverseScroll;
    }

    [ObservableProperty] public partial double Sensitivity { get; set; }
    [ObservableProperty] public partial double ScrollSpeed { get; set; }
    [ObservableProperty] public partial bool ReverseScroll { get; set; }
    [ObservableProperty] public partial bool ShowSettings { get; set; }

    public string SensitivityText => $"{Sensitivity:0.0}×";
    public string ScrollSpeedText => $"{ScrollSpeed:0.0}×";

    partial void OnSensitivityChanged(double value)
    {
        _settings = (_settings with { MouseSensitivity = value }).Normalized();
        _preferences.Set(SensitivityKey, _settings.MouseSensitivity);
        OnPropertyChanged(nameof(SensitivityText));
    }

    partial void OnScrollSpeedChanged(double value)
    {
        _settings = (_settings with { ScrollSpeed = value }).Normalized();
        _preferences.Set(ScrollSpeedKey, _settings.ScrollSpeed);
        OnPropertyChanged(nameof(ScrollSpeedText));
    }

    partial void OnReverseScrollChanged(bool value)
    {
        _settings = _settings with { ReverseScroll = value };
        _preferences.Set(ReverseScrollKey, value);
    }

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    /// <summary>Touch cancelled by the system: unsent motion must not be replayed later.</summary>
    public void OnGestureCancelled() => _sender.DiscardMotion();

    /// <summary>Called on the UI thread by the touchpad surface; only enqueues.</summary>
    public void OnGesture(TouchpadGesture gesture)
    {
        switch (gesture.Kind)
        {
            case TouchpadGestureKind.Move:
                var (dx, dy) = _settings.PointerDelta(gesture.Dx, gesture.Dy);
                _sender.Move(dx, dy);
                break;
            case TouchpadGestureKind.Scroll:
                _sender.Scroll(0, _settings.WheelDelta(gesture.Dy));
                break;
            case TouchpadGestureKind.LeftClick:
                Click(MouseButton.Left, MouseClickAction.Click);
                break;
            case TouchpadGestureKind.RightClick:
                Click(MouseButton.Right, MouseClickAction.Click);
                break;
            case TouchpadGestureKind.DoubleClick:
                Click(MouseButton.Left, MouseClickAction.DoubleClick);
                break;
        }
    }

    [RelayCommand]
    private void LeftClick() => Click(MouseButton.Left, MouseClickAction.Click);

    [RelayCommand]
    private void RightClick() => Click(MouseButton.Right, MouseClickAction.Click);

    private void Click(MouseButton button, MouseClickAction action) =>
        _sender.Send(new MouseClickMessage { Button = button, Action = action });
}

public sealed partial class KeyboardViewModel(IConnectionService connection, ConnectionStatusViewModel status)
    : InputViewModelBase(connection, status)
{
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    public partial string Text { get; set; } = "";

    private bool CanSendText() => !string.IsNullOrEmpty(Text);

    [RelayCommand(CanExecute = nameof(CanSendText))]
    private async Task SendTextAsync()
    {
        var text = Text.Length > ProtocolInfo.MaxTextLength ? Text[..ProtocolInfo.MaxTextLength] : Text;
        if (await SendAsync(new KeyboardTextMessage { Text = text })) Text = "";
    }

    [RelayCommand]
    private Task SendKeyAsync(KeyCode key) => SendAsync(new KeyboardKeyMessage { Key = key }).AsTask();
}

public sealed partial class MediaViewModel(IConnectionService connection, ConnectionStatusViewModel status)
    : InputViewModelBase(connection, status)
{
    [RelayCommand]
    private Task SendMediaAsync(MediaCommand command) => SendAsync(new MediaCommandMessage { Command = command }).AsTask();
}
