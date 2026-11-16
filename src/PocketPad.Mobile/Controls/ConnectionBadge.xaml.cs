using System.Globalization;
using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Controls;

/// <summary>Connection indicator placed at the top of every page. Binds to the singleton status view model.</summary>
public partial class ConnectionBadge : ContentView
{
    public ConnectionBadge()
    {
        InitializeComponent();
        BindingContext = IPlatformApplication.Current?.Services.GetService<ConnectionStatusViewModel>();
    }
}

public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.Length > 0 : value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
