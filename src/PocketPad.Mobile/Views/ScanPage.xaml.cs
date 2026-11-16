using PocketPad.Mobile.ViewModels;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace PocketPad.Mobile.Views;

public partial class ScanPage : ContentPage
{
    private readonly ScanViewModel _viewModel;
    private CameraBarcodeReaderView? _reader;

    public ScanPage(ScanViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.StartDiscovery();

        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        var granted = status == PermissionStatus.Granted;
        _viewModel.OnPermissionResult(granted);
        if (granted) StartCamera();
    }

    protected override void OnDisappearing()
    {
        _viewModel.OnDisappearing();
        StopCamera();
        base.OnDisappearing();
    }

    private void StartCamera()
    {
        if (_reader is not null) return;

        _reader = new CameraBarcodeReaderView
        {
            CameraLocation = CameraLocation.Rear,
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode,
                AutoRotate = true,
                Multiple = false,
            },
        };
        _reader.SetBinding(CameraBarcodeReaderView.IsDetectingProperty, nameof(ScanViewModel.IsDetecting));
        _reader.BarcodesDetected += OnBarcodesDetected;
        CameraHost.Content = _reader;
    }

    /// <summary>Releases the camera so it is not held while the page is hidden.</summary>
    private void StopCamera()
    {
        if (_reader is null) return;
        _reader.BarcodesDetected -= OnBarcodesDetected;
        _reader.IsDetecting = false;
        CameraHost.Content = null;
        _reader.Handler?.DisconnectHandler();
        _reader = null;
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        var value = e.Results?.FirstOrDefault()?.Value;
        if (value is null) return;
        MainThread.BeginInvokeOnMainThread(async () => await _viewModel.OnCodeDetectedAsync(value));
    }
}
