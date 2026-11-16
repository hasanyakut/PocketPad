using System.Text.Json;
using PocketPad.Shared.Pairing;
using PocketPad.Shared.Protocol;

namespace PocketPad.Mobile.Services;

public interface ICredentialStore
{
    Task<IReadOnlyList<PairedServer>> LoadAsync();
    Task SaveAsync(IReadOnlyList<PairedServer> servers);
    Task<string?> GetLastServerIdAsync();
    Task SetLastServerIdAsync(string? serverId);
}

/// <summary>Paired PC credentials (incl. device secret) kept in Android SecureStorage (Keystore backed).</summary>
public sealed class SecureCredentialStore(ISecureStorage storage) : ICredentialStore
{
    private const string ServersKey = "pocketpad.paired-servers.v1";
    private const string LastServerKey = "pocketpad.last-server.v1";

    public async Task<IReadOnlyList<PairedServer>> LoadAsync()
    {
        var json = await storage.GetAsync(ServersKey);
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ListPairedServer) ?? [];
        }
        catch (JsonException)
        {
            storage.Remove(ServersKey);
            return [];
        }
    }

    public Task SaveAsync(IReadOnlyList<PairedServer> servers) =>
        storage.SetAsync(ServersKey, JsonSerializer.Serialize(servers.ToList(), ProtocolJsonContext.Default.ListPairedServer));

    public Task<string?> GetLastServerIdAsync() => storage.GetAsync(LastServerKey);

    public Task SetLastServerIdAsync(string? serverId)
    {
        if (serverId is null)
        {
            storage.Remove(LastServerKey);
            return Task.CompletedTask;
        }
        return storage.SetAsync(LastServerKey, serverId);
    }
}
