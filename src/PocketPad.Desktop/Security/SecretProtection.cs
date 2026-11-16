using System.IO;
using System.Security.Cryptography;

namespace PocketPad.Desktop.Security;

/// <summary>Encrypts data at rest so secrets never touch the disk in plain text.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Windows DPAPI bound to the current Windows user account.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "PocketPad.Desktop.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Per-user storage location (%LOCALAPPDATA%\PocketPad by default).</summary>
public sealed class AppDataPaths
{
    public AppDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketPad");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string Certificate => Path.Combine(Root, "server-certificate.bin");
    public string PairedDevices => Path.Combine(Root, "paired-devices.bin");
    public string ServerIdentity => Path.Combine(Root, "server-identity.json");

    /// <summary>Writes via a temp file so a crash never leaves a half-written store.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
