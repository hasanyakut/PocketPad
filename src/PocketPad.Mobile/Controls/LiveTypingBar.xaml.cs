namespace PocketPad.Mobile.Controls;

/// <summary>Receives the keystrokes produced by <see cref="LiveTypingBar"/>.</summary>
public interface ILiveTypingSink
{
    /// <summary>The field changed: send the Backspaces/text that make the PC match.</summary>
    void OnTypingChanged(string? oldText, string? newText);
    void SendEnter();
    /// <summary>Backspace while the field is empty (soft keyboards report nothing in that case).</summary>
    void SendBackspace();
}

/// <summary>
/// Live typing field used on the Touchpad and Klavye pages: every change is sent to the PC immediately,
/// Enter sends Enter and clears the field (the text stays on the PC), ⌫ deletes on the PC even when the field is empty.
/// </summary>
public partial class LiveTypingBar : ContentView
{
    /// <summary>Keep the field short; what was typed is already on the PC.</summary>
    private const int MaxFieldLength = 200;

    public static readonly BindableProperty SinkProperty =
        BindableProperty.Create(nameof(Sink), typeof(ILiveTypingSink), typeof(LiveTypingBar));

    private bool _suppressDiff;

    public LiveTypingBar()
    {
        InitializeComponent();
    }

    public ILiveTypingSink? Sink
    {
        get => (ILiveTypingSink?)GetValue(SinkProperty);
        set => SetValue(SinkProperty, value);
    }

    /// <summary>Focuses the field, which opens the phone's soft keyboard.</summary>
    public async Task OpenKeyboardAsync()
    {
        await Task.Yield(); // let a just-shown bar finish layout first
        Field.Focus();
        await Field.ShowSoftInputAsync(CancellationToken.None);
    }

    public async Task CloseKeyboardAsync()
    {
        await Field.HideSoftInputAsync(CancellationToken.None);
        Field.Unfocus();
        ClearField();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsEnabled) && !IsEnabled) ClearField(); // disconnected: start clean later
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressDiff) return;
        Sink?.OnTypingChanged(e.OldTextValue, e.NewTextValue);
        if ((e.NewTextValue?.Length ?? 0) > MaxFieldLength) ClearField();
    }

    private void OnCompleted(object? sender, EventArgs e)
    {
        Sink?.SendEnter();
        ClearField();
        Field.Focus(); // keep typing after Enter
    }

    private void OnEnterClicked(object? sender, EventArgs e) => OnCompleted(sender, e);

    private void OnBackspaceClicked(object? sender, EventArgs e)
    {
        var text = Field.Text;
        if (string.IsNullOrEmpty(text))
            Sink?.SendBackspace();                                         // field empty: delete on the PC directly
        else
            Field.Text = text[..^(char.IsLowSurrogate(text[^1]) && text.Length > 1 ? 2 : 1)]; // the diff sends the Backspace
    }

    /// <summary>Empties the field without sending Backspaces.</summary>
    private void ClearField()
    {
        _suppressDiff = true;
        Field.Text = string.Empty;
        _suppressDiff = false;
    }
}
