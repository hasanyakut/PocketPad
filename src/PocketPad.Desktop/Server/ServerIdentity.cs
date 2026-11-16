using System.IO;
using System.Text.Json;
using PocketPad.Desktop.Security;

namespace PocketPad.Desktop.Server;

/// <summary>Stable, non-secret identity of this Windows installation (the "device id" in the QR code).</summary>
public sealed record ServerIdentity(string ServerId, string ServerName)
{
    public static ServerIdentity LoadOrCreate(AppDataPaths paths)
    {
        if (File.Exists(paths.ServerIdentity))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<ServerIdentity>(File.ReadAllText(paths.ServerIdentity));
                if (stored is { ServerId.Length: > 0 }) return stored with { ServerName = MachineName };
            }
            catch (JsonException)
            {
                // fall through and regenerate
            }
        }

        var identity = new ServerIdentity(Guid.NewGuid().ToString("N"), MachineName);
        AppDataPaths.WriteAtomic(paths.ServerIdentity, JsonSerializer.SerializeToUtf8Bytes(identity));
        return identity;
    }

    private static string MachineName =>
        Environment.MachineName.Length > 64 ? Environment.MachineName[..64] : Environment.MachineName;
}
