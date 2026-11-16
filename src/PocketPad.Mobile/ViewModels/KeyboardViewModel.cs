using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PocketPad.Mobile.Controls;
using PocketPad.Mobile.Services;
using PocketPad.Shared.Client;
using PocketPad.Shared.Input;
using PocketPad.Shared.Protocol;

namespace PocketPad.Mobile.ViewModels;

/// <summary>One shortcut tile: what it does, the key combination shown under it, and what is sent.</summary>
public sealed record ShortcutItem(string Title, string Keys, KeyCode Key, KeyModifiers Modifiers = KeyModifiers.None);

/// <summary>Three tiles per row; the last row may be partly empty.</summary>
public sealed record ShortcutRow(ShortcutItem? A, ShortcutItem? B, ShortcutItem? C);

public sealed record ShortcutGroup(string Title, string Icon, IReadOnlyList<ShortcutItem> Items)
{
    public IReadOnlyList<ShortcutRow> Rows { get; } = Items.Chunk(3)
        .Select(c => new ShortcutRow(c[0], c.ElementAtOrDefault(1), c.ElementAtOrDefault(2))).ToList();
}

/// <summary>Klavye tab: live typing (same as the touchpad bar) and a panel of common Windows shortcuts.</summary>
public sealed partial class KeyboardViewModel : InputViewModelBase, ILiveTypingSink
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;
    private const KeyModifiers Alt = KeyModifiers.Alt;
    private const KeyModifiers Win = KeyModifiers.Windows;

    private readonly InputSender _sender;
    private readonly IHapticFeedback _haptics;

    public KeyboardViewModel(IConnectionService connection, ConnectionStatusViewModel status, IHapticFeedback haptics)
        : base(connection, status)
    {
        _haptics = haptics;
        // Same ordered sender as the touchpad: typing and shortcuts keep their order, nothing is sent while disconnected.
        _sender = new InputSender(connection.SendAsync, () => connection.State == ConnectionState.Connected);
    }

    public IReadOnlyList<ShortcutGroup> Groups { get; } =
    [
        new("Düzenleme", "ic_edit.png",
        [
            new("Kopyala", "Ctrl+C", KeyCode.C, Ctrl),
            new("Yapıştır", "Ctrl+V", KeyCode.V, Ctrl),
            new("Kes", "Ctrl+X", KeyCode.X, Ctrl),
            new("Geri al", "Ctrl+Z", KeyCode.Z, Ctrl),
            new("Yinele", "Ctrl+Y", KeyCode.Y, Ctrl),
            new("Tümünü seç", "Ctrl+A", KeyCode.A, Ctrl),
            new("Kaydet", "Ctrl+S", KeyCode.S, Ctrl),
            new("Bul", "Ctrl+F", KeyCode.F, Ctrl),
            new("Yazdır", "Ctrl+P", KeyCode.P, Ctrl),
        ]),
        new("Pencereler", "ic_windows.png",
        [
            new("Pencere değiştir", "Alt+Tab", KeyCode.Tab, Alt),
            new("Pencereyi kapat", "Alt+F4", KeyCode.F4, Alt),
            new("Masaüstü", "Win+D", KeyCode.D, Win),
            new("Görev görünümü", "Win+Tab", KeyCode.Tab, Win),
            new("Dosya Gezgini", "Win+E", KeyCode.E, Win),
            new("Başlat", "Win", KeyCode.Windows),
            new("Ekran alıntısı", "Win+Shift+S", KeyCode.S, Win | Shift),
            new("Görev Yöneticisi", "Ctrl+Shift+Esc", KeyCode.Escape, Ctrl | Shift),
            new("Ayarlar", "Win+I", KeyCode.I, Win),
        ]),
        new("Tarayıcı", "ic_globe.png",
        [
            new("Yeni sekme", "Ctrl+T", KeyCode.T, Ctrl),
            new("Sekmeyi kapat", "Ctrl+W", KeyCode.W, Ctrl),
            new("Kapananı aç", "Ctrl+Shift+T", KeyCode.T, Ctrl | Shift),
            new("Sonraki sekme", "Ctrl+Tab", KeyCode.Tab, Ctrl),
            new("Önceki sekme", "Ctrl+Shift+Tab", KeyCode.Tab, Ctrl | Shift),
            new("Yenile", "F5", KeyCode.F5),
            new("Yakınlaştır", "Ctrl +", KeyCode.Plus, Ctrl),
            new("Uzaklaştır", "Ctrl −", KeyCode.Minus, Ctrl),
            new("Tam ekran", "F11", KeyCode.F11),
            new("Adres çubuğu", "Ctrl+L", KeyCode.L, Ctrl),
            new("Geri", "Alt+←", KeyCode.Left, Alt),
            new("İleri", "Alt+→", KeyCode.Right, Alt),
        ]),
    ];

    public IReadOnlyList<ShortcutItem> FunctionKeys { get; } =
        Enumerable.Range(1, 12).Select(i => new ShortcutItem($"F{i}", "", KeyCode.F1 + (i - 1))).ToList();

    [RelayCommand]
    private void SendShortcut(ShortcutItem item)
    {
        TryHaptic();
        _sender.Send(new KeyboardKeyMessage { Key = item.Key, Modifiers = item.Modifiers });
    }

    [RelayCommand]
    private void SendKey(KeyCode key)
    {
        TryHaptic();
        _sender.Send(new KeyboardKeyMessage { Key = key });
    }

    // ---- ILiveTypingSink ----
    public void OnTypingChanged(string? oldText, string? newText)
    {
        foreach (var message in TypingDiff.ToMessages(oldText, newText)) _sender.Send(message);
    }

    public void SendEnter() => _sender.Send(new KeyboardKeyMessage { Key = KeyCode.Enter });

    public void SendBackspace() => _sender.Send(new KeyboardKeyMessage { Key = KeyCode.Backspace });

    private void TryHaptic()
    {
        try { _haptics.Perform(HapticFeedbackType.Click); }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException) { }
    }
}
