using PocketPad.Shared.Protocol;

namespace PocketPad.Shared.Input;

/// <summary>
/// Ordered outgoing input queue. Consecutive pointer moves, and consecutive scrolls, are merged into the
/// unsent message at the tail of the queue and "motion" (moves + scrolls) is rate-limited, so a slow link
/// never builds a backlog of stale motion that would be replayed late. Clicks/keys are never merged or
/// dropped and keep their order relative to motion. Not thread-safe; <see cref="InputSender"/> guards it.
/// </summary>
public sealed class MotionCoalescer(TimeSpan moveInterval)
{
    private readonly LinkedList<ProtocolMessage> _queue = new();
    private long? _lastMotionSentMs;

    public int Count => _queue.Count;

    public void AddMove(double dx, double dy)
    {
        if (!IsUsable(dx, dy)) return;
        if (_queue.Last?.Value is MouseMoveMessage tail)
            _queue.Last.Value = new MouseMoveMessage { Dx = Clamp(tail.Dx + dx), Dy = Clamp(tail.Dy + dy) };
        else
            _queue.AddLast(new MouseMoveMessage { Dx = Clamp(dx), Dy = Clamp(dy) });
    }

    public void AddScroll(double dx, double dy)
    {
        if (!IsUsable(dx, dy)) return;
        if (_queue.Last?.Value is MouseScrollMessage tail)
            _queue.Last.Value = new MouseScrollMessage { Dx = Clamp(tail.Dx + dx), Dy = Clamp(tail.Dy + dy) };
        else
            _queue.AddLast(new MouseScrollMessage { Dx = Clamp(dx), Dy = Clamp(dy) });
    }

    public void Add(ProtocolMessage message) => _queue.AddLast(message);

    /// <summary>
    /// Returns the next message to send now, or null. When motion is rate-limited, <paramref name="waitUntilMs"/>
    /// says when to ask again; until then further motion of the same kind keeps merging into it.
    /// </summary>
    public ProtocolMessage? TryTake(long nowMs, out long? waitUntilMs)
    {
        waitUntilMs = null;
        if (_queue.First is not { } first) return null;

        if (IsMotion(first.Value))
        {
            var due = _lastMotionSentMs + (long)moveInterval.TotalMilliseconds;
            if (due is { } d && nowMs < d)
            {
                waitUntilMs = d;
                return null;
            }
            _lastMotionSentMs = nowMs;
        }

        _queue.RemoveFirst();
        return first.Value;
    }

    public void Clear() => _queue.Clear();

    /// <summary>Drops unsent moves and scrolls (e.g. touch cancelled) but keeps clicks and keys.</summary>
    public void DiscardMotion()
    {
        for (var node = _queue.First; node is not null;)
        {
            var next = node.Next;
            if (IsMotion(node.Value)) _queue.Remove(node);
            node = next;
        }
    }

    private static bool IsMotion(ProtocolMessage m) => m is MouseMoveMessage or MouseScrollMessage;

    private static bool IsUsable(double dx, double dy) =>
        double.IsFinite(dx) && double.IsFinite(dy) && (dx != 0 || dy != 0);

    private static double Clamp(double v) => Math.Clamp(v, -ProtocolInfo.MaxPointerDelta, ProtocolInfo.MaxPointerDelta);
}

/// <summary>
/// Background pump for touchpad input: callers (UI thread) only enqueue, never wait.
/// Moves are merged and sent at most every <c>moveInterval</c> (~60 Hz by default).
/// Nothing is queued or sent while <c>canSend</c> is false.
/// </summary>
public sealed class InputSender : IAsyncDisposable
{
    public static readonly TimeSpan DefaultMoveInterval = TimeSpan.FromMilliseconds(16);

    private readonly Func<ProtocolMessage, ValueTask<bool>> _send;
    private readonly Func<bool> _canSend;
    private readonly TimeProvider _time;
    private readonly MotionCoalescer _coalescer;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public InputSender(Func<ProtocolMessage, ValueTask<bool>> send, Func<bool> canSend, TimeSpan? moveInterval = null, TimeProvider? time = null)
    {
        _send = send;
        _canSend = canSend;
        _time = time ?? TimeProvider.System;
        _coalescer = new MotionCoalescer(moveInterval ?? DefaultMoveInterval);
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>Returns false (and drops the input) when not connected.</summary>
    public bool Move(double dx, double dy) => Enqueue(c => c.AddMove(dx, dy));

    /// <summary>Wheel units (120 = one notch, positive = up / right). Returns false (and drops the input) when not connected.</summary>
    public bool Scroll(double dx, double dy) => Enqueue(c => c.AddScroll(dx, dy));

    /// <summary>Returns false (and drops the input) when not connected.</summary>
    public bool Send(ProtocolMessage message) => Enqueue(c => c.Add(message));

    /// <summary>Drops queued, not yet sent moves/scrolls (touch cancelled). Clicks already queued still go out.</summary>
    public void DiscardMotion()
    {
        lock (_gate) _coalescer.DiscardMotion();
    }

    private bool Enqueue(Action<MotionCoalescer> add)
    {
        if (!_canSend())
        {
            lock (_gate) _coalescer.Clear();
            return false;
        }
        lock (_gate) add(_coalescer);
        if (_signal.CurrentCount == 0) _signal.Release();
        return true;
    }

    private long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    ProtocolMessage? next;
                    long? waitUntil;
                    lock (_gate) next = _coalescer.TryTake(NowMs, out waitUntil);

                    if (next is not null)
                    {
                        if (!_canSend() || !await _send(next).ConfigureAwait(false))
                        {
                            lock (_gate) _coalescer.Clear(); // connection gone: stale input must not replay later
                        }
                        continue;
                    }
                    if (waitUntil is not { } until) break;
                    var delay = until - NowMs;
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), _time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _signal.Dispose();
    }
}
