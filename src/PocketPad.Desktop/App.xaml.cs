using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PocketPad.Desktop.Input;
using PocketPad.Desktop.Security;
using PocketPad.Desktop.Server;
using PocketPad.Desktop.Services;
using PocketPad.Desktop.ViewModels;
using PocketPad.Desktop.Views;

namespace PocketPad.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\PocketPad.Desktop", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("PocketPad zaten çalışıyor (sistem tepsisine bakın).", "PocketPad");
            Shutdown();
            return;
        }

        _services = ConfigureServices();

        var window = _services.GetRequiredService<MainWindow>();
        var tray = _services.GetRequiredService<TrayIconService>();
        tray.OpenRequested += (_, _) => window.ShowFromTray();
        tray.ExitRequested += async (_, _) => await ExitAsync();
        tray.Show();

        window.Show();
        await _services.GetRequiredService<MainViewModel>().StartServerAsync();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AppDataPaths());
        services.AddSingleton(new ServerOptions());
        services.AddSingleton(sp => ServerIdentity.LoadOrCreate(sp.GetRequiredService<AppDataPaths>()));

        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ICertificateProvider, CertificateStore>();
        services.AddSingleton<IPairedDeviceStore, PairedDeviceStore>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<IInputController, WindowsInputController>();
        services.AddSingleton<InputDispatcher>();
        services.AddSingleton<PocketPad.Desktop.Media.IMediaStatusProvider, PocketPad.Desktop.Media.WindowsMediaStatusProvider>();
        services.AddSingleton<PocketPad.Desktop.Media.MediaStatusPublisher>(sp => new PocketPad.Desktop.Media.MediaStatusPublisher(
            sp.GetRequiredService<PocketPad.Desktop.Media.IMediaStatusProvider>(), sp.GetRequiredService<SessionRegistry>(),
            sp.GetRequiredService<ILogger<PocketPad.Desktop.Media.MediaStatusPublisher>>()));
        services.AddSingleton<ControlServer>();

        services.AddSingleton<TrayIconService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IPairingApprovalService, WpfPairingApprovalService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    internal async Task ExitAsync()
    {
        if (_services is not null)
        {
            await _services.GetRequiredService<ControlServer>().StopAsync();
            _services.GetRequiredService<MainWindow>().AllowClose();
            await _services.DisposeAsync();
            _services = null;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
