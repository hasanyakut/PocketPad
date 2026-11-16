using Microsoft.Extensions.Logging;
using PocketPad.Mobile.Services;
using PocketPad.Mobile.ViewModels;
using PocketPad.Mobile.Views;
using PocketPad.Shared.Client;
using ZXing.Net.Maui.Controls;

namespace PocketPad.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureMauiHandlers(handlers =>
                handlers.AddHandler<Controls.TouchpadSurface, Handlers.TouchpadSurfaceHandler>())
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var services = builder.Services;

        // Platform services
        services.AddSingleton(SecureStorage.Default);
        services.AddSingleton(DeviceInfo.Current);
        services.AddSingleton(Preferences.Default);
        services.AddSingleton(HapticFeedback.Default);

        // Connection
        services.AddSingleton(new ClientOptions());
        services.AddSingleton(sp => new PocketPadClient(sp.GetRequiredService<ClientOptions>()));
        services.AddSingleton<ICredentialStore, SecureCredentialStore>();
        services.AddSingleton<IConnectionService, ConnectionService>();

        // View models
        services.AddSingleton<ConnectionStatusViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddSingleton<TouchpadViewModel>();
        services.AddSingleton<KeyboardViewModel>();
        services.AddSingleton<MediaViewModel>();

        // Views
        services.AddSingleton<AppShell>();
        services.AddTransient<DevicesPage>();
        services.AddTransient<ScanPage>();
        services.AddTransient<TouchpadPage>();
        services.AddTransient<KeyboardPage>();
        services.AddTransient<MediaPage>();

        return builder.Build();
    }
}
