using PocketPad.Shared.Protocol;

namespace PocketPad.Desktop.Server;

public enum SessionStatus { Handshaking, AwaitingApproval, Controlling }

public sealed record ConnectionSnapshot(
    Guid Id, string RemoteAddress, SessionStatus Status, string? DeviceId, string? DeviceName, DateTimeOffset ConnectedAt);

public enum ActivationResult { Activated, ReplacedPrevious, Busy }

public sealed record InputActivity(string DeviceName, string MessageType, DateTimeOffset At);

/// <summary>Tracks open sockets and enforces a single active control session.</summary>
public sealed class SessionRegistry(ServerOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ClientSession> _sessions = [];
    private ClientSession? _active;

    public event EventHandler? Changed;
    public event EventHandler<InputActivity>? InputReceived;

    public IReadOnlyList<ConnectionSnapshot> Snapshot()
    {
        lock (_gate) return _sessions.Values.Select(s => s.ToSnapshot()).OrderBy(s => s.ConnectedAt).ToList();
    }

    public ConnectionSnapshot? ActiveSession
    {
        get { lock (_gate) return _active?.ToSnapshot(); }
    }

    /// <summary>Queues a message to the current control session; false when no phone is in control.</summary>
    internal bool TrySendToActive(ProtocolMessage message)
    {
        ClientSession? active;
        lock (_gate) active = _active;
        return active is not null && active.TrySend(message);
    }

    internal bool IsActive(ClientSession session)
    {
        lock (_gate) return _active == session;
    }

    /// <summary>Raised when a different session becomes the control session.</summary>
    public event EventHandler? ControlSessionChanged;

    internal bool TryRegister(ClientSession session)
    {
        lock (_gate)
        {
            if (_sessions.Count >= options.MaxConnections) return false;
            _sessions.Add(session.Id, session);
        }
        RaiseChanged();
        return true;
    }

    internal void Unregister(ClientSession session)
    {
        bool wasActive;
        lock (_gate)
        {
            _sessions.Remove(session.Id);
            wasActive = _active == session;
            if (wasActive) _active = null;
        }
        if (wasActive) ControlSessionChanged?.Invoke(this, EventArgs.Empty); // releases held keys/buttons
        RaiseChanged();
    }

    /// <summary>
    /// Makes <paramref name="session"/> the control session. The same device reconnecting replaces its
    /// stale session; any other device is refused while a control session exists.
    /// </summary>
    internal ActivationResult TryActivate(ClientSession session)
    {
        ClientSession? replaced = null;
        lock (_gate)
        {
            if (_active is not null && _active != session)
            {
                if (_active.DeviceId != session.DeviceId) return ActivationResult.Busy;
                replaced = _active;
            }
            _active = session;
        }

        ControlSessionChanged?.Invoke(this, EventArgs.Empty);
        replaced?.Close(RejectReason.Replaced);
        RaiseChanged();
        return replaced is null ? ActivationResult.Activated : ActivationResult.ReplacedPrevious;
    }

    public void DisconnectDevice(string deviceId, RejectReason reason)
    {
        List<ClientSession> targets;
        lock (_gate) targets = _sessions.Values.Where(s => s.DeviceId == deviceId).ToList();
        foreach (var s in targets) s.Close(reason);
    }

    internal void CloseAll(RejectReason reason)
    {
        List<ClientSession> targets;
        lock (_gate) targets = [.. _sessions.Values];
        foreach (var s in targets) s.Close(reason);
    }

    internal void ReportInput(ClientSession session, ProtocolMessage message) =>
        InputReceived?.Invoke(this, new InputActivity(session.DeviceName ?? "?", MessageSerializer.GetTypeName(message), time.GetUtcNow()));

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
