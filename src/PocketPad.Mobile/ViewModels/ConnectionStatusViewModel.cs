using CommunityToolkit.Mvvm.ComponentModel;
using PocketPad.Mobile.Services;
using PocketPad.Shared.Client;

namespace PocketPad.Mobile.ViewModels;

/// <summary>Drives the connection indicator shown on every page.</summary>
public sealed partial class ConnectionStatusViewModel : ObservableObject
{
    private readonly IConnectionService _connection;

    public ConnectionStatusViewModel(IConnectionService connection)
    {
        _connection = connection;
        _connection.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(Refresh);
        Refresh();
    }

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string? Detail { get; set; }
    [ObservableProperty] public partial Color IndicatorColor { get; set; } = Colors.Gray;
    [ObservableProperty] public partial bool IsConnected { get; set; }

    private void Refresh()
    {
        var state = _connection.State;
        IsConnected = state == ConnectionState.Connected;
        var server = _connection.CurrentServer?.ServerName;
        Text = state == ConnectionState.Connected && server is not null
            ? $"{server} · Bağlı"
            : StatusText.ForState(state);
        Detail = state == ConnectionState.Connected ? null : StatusText.ForFailure(_connection.LastFailure, _connection.LastReason);
        IndicatorColor = state switch
        {
            ConnectionState.Connected => Color.FromArgb("#3FCF8E"),
            ConnectionState.Disconnected when _connection.LastFailure != ConnectionFailure.None => Color.FromArgb("#F0616D"),
            ConnectionState.Disconnected => Color.FromArgb("#6B7280"),
            _ => Color.FromArgb("#F5B841"),
        };
    }
}
