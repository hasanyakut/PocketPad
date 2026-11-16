using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Controls;

/// <summary>Shortcut tile: action name (up to two lines) + key combination. Tap = short press animation + <see cref="Tapped"/>.</summary>
public sealed class ShortcutTile : ContentView
{
    public static readonly BindableProperty ItemProperty = BindableProperty.Create(
        nameof(Item), typeof(ShortcutItem), typeof(ShortcutTile),
        propertyChanged: (b, _, n) => ((ShortcutTile)b).Show((ShortcutItem?)n));

    private readonly Label _title;
    private readonly Label _keys;

    public ShortcutTile()
    {
        _title = new Label
        {
            FontFamily = "OpenSansSemibold",
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.WordWrap,
            MaxLines = 2,
            // Always reserve two lines: star-sized grid columns can measure text unconstrained (one line),
            // which would clip the second line of long names. Short names are centred in the same space.
            HeightRequest = 38,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _keys = new Label
        {
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = (Color)Application.Current!.Resources["TextSecondary"],
        };

        var tile = new Border
        {
            BackgroundColor = (Color)Application.Current.Resources["SurfaceRaised"],
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(6, 8),
            MinimumHeightRequest = 66,
            Content = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { _title, _keys } },
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (Item is null || !IsEnabled) return;
            Tapped?.Invoke(this, Item);
            await tile.ScaleToAsync(0.93, 60, Easing.CubicOut);
            await tile.ScaleToAsync(1, 90, Easing.CubicIn);
        };
        tile.GestureRecognizers.Add(tap);
        Content = tile;
        IsVisible = false;
    }

    public ShortcutItem? Item
    {
        get => (ShortcutItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public event EventHandler<ShortcutItem>? Tapped;

    private void Show(ShortcutItem? item)
    {
        IsVisible = item is not null;
        _title.Text = item?.Title;
        _keys.Text = item?.Keys;
        _keys.IsVisible = !string.IsNullOrEmpty(item?.Keys);
    }
}
