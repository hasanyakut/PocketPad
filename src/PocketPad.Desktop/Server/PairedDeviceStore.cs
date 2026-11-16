using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PocketPad.Desktop.Security;

namespace PocketPad.Desktop.Server;

public sealed record PairedDevice(string DeviceId, string Name, string Platform, DateTimeOffset PairedAt, DateTimeOffset? LastSeen);

public interface IPairedDeviceStore
{
    IReadOnlyList<PairedDevice> GetAll();
    /// <summary>Registers a new device and returns its one-time revealed secret.</summary>
    (PairedDevice Device, string Secret) Add(string name, string platform);
    PairedDevice? Verify(string deviceId, string secret);
    void MarkSeen(string deviceId, string name);
    bool Remove(string deviceId);
    event EventHandler? Changed;
}

/// <summary>
/// Paired devices persisted as a DPAPI encrypted file. Only a SHA-256 hash of each device secret
/// is stored (secrets are 256-bit random values, so a plain hash is sufficient).
/// </summary>
public sealed class PairedDeviceStore : IPairedDeviceStore
{
    private sealed record StoredDevice(string DeviceId, string Name, string Platform, DateTimeOffset PairedAt, DateTimeOffset? LastSeen, string SecretHash);

    private readonly AppDataPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<PairedDeviceStore> _logger;
    private readonly Lock _gate = new();
    private readonly List<StoredDevice> _devices;

    public PairedDeviceStore(AppDataPaths paths, ISecretProtector protector, TimeProvider time, ILogger<PairedDeviceStore> logger)
    {
        _paths = paths;
        _protector = protector;
        _time = time;
        _logger = logger;
        _devices = Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PairedDevice> GetAll()
    {
        lock (_gate) return _devices.Select(ToPublic).ToList();
    }

    public (PairedDevice Device, string Secret) Add(string name, string platform)
    {
        var secret = PairingService.Base64Url(RandomNumberGenerator.GetBytes(32));
        var stored = new StoredDevice(Guid.NewGuid().ToString("N"), name, platform, _time.GetUtcNow(), null, HashSecret(secret));
        lock (_gate)
        {
            _devices.Add(stored);
            Save();
        }
        _logger.LogInformation("Paired new device {DeviceId} ({Name}).", stored.DeviceId, name);
        Changed?.Invoke(this, EventArgs.Empty);
        return (ToPublic(stored), secret);
    }

    public PairedDevice? Verify(string deviceId, string secret)
    {
        var candidate = Convert.FromBase64String(HashSecret(secret));
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (device is null) return null;
            return CryptographicOperations.FixedTimeEquals(candidate, Convert.FromBase64String(device.SecretHash))
                ? ToPublic(device)
                : null;
        }
    }

    public void MarkSeen(string deviceId, string name)
    {
        lock (_gate)
        {
            var index = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0) return;
            _devices[index] = _devices[index] with { LastSeen = _time.GetUtcNow(), Name = name };
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string deviceId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.RemoveAll(d => d.DeviceId == deviceId) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private static PairedDevice ToPublic(StoredDevice d) => new(d.DeviceId, d.Name, d.Platform, d.PairedAt, d.LastSeen);

    private static string HashSecret(string secret) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private List<StoredDevice> Load()
    {
        if (!File.Exists(_paths.PairedDevices)) return [];
        try
        {
            var json = _protector.Unprotect(File.ReadAllBytes(_paths.PairedDevices));
            return JsonSerializer.Deserialize<List<StoredDevice>>(json) ?? [];
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogWarning(ex, "Paired device store is unreadable; starting empty.");
            return [];
        }
    }

    private void Save() =>
        AppDataPaths.WriteAtomic(_paths.PairedDevices, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(_devices)));
}
