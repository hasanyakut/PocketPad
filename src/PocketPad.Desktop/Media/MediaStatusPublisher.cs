using Microsoft.Extensions.Logging;
using PocketPad.Desktop.Server;
using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Media;

/// <summary>
/// Pushes <see cref="MediaStatusMessage"/> to the active control session: once when it starts, whenever the state
/// changes (polled every <c>interval</c>), and shortly after each media command so the phone shows the real outcome.
/// Nothing is read or sent while no phone is in control.
/// </summary>
public sealed class MediaStatusPublisher : IAsyncDisposable
{
    private static readonly TimeSpan[] AfterCommandDelays = [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)];

    private readonly IMediaStatusProvider _provider;
    private readonly SessionRegistry _registry;
    private readonly ILogger<MediaStatusPublisher> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _refresh = new(0);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MediaStatusMessage? _lastSent;

    public MediaStatusPublisher(IMediaStatusProvider provider, SessionRegistry registry, ILogger<MediaStatusPublisher> logger,
        TimeSpan? interval = null)
    {
        _provider = provider;
        _registry = registry;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(1);
        _registry.ControlSessionChanged += (_, _) =>
        {
            _lastSent = null; // a new phone gets the full state immediately
            Signal();
        };
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>A phone just completed authentication: send it the full current state.</summary>
    public void OnControlSessionReady()
    {
        _lastSent = null;
        Signal();
    }

    /// <summary>Called after a media command: re-read soon (volume reacts at once, track changes take a moment).</summary>
    public void RequestRefresh()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        foreach (var delay in AfterCommandDelays)
            _ = Task.Delay(delay, token).ContinueWith(_ => Signal(), token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Signal()
    {
        if (_refresh.CurrentCount == 0) _refresh.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _refresh.WaitAsync(_interval, ct).ConfigureAwait(false);
                if (_registry.ActiveSession is null) continue;

                var status = await _provider.GetStatusAsync(ct).ConfigureAwait(false);
                if (status == _lastSent) continue;
                if (_registry.TrySendToActive(status)) _lastSent = status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Media status update failed.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
