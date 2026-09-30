using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageWidget;

/// <summary>Keeps the TypeSafe (Jev) API key encrypted with DPAPI (current user) under %APPDATA%.</summary>
public static class JevKeyStore
{
    static string FilePath => Path.Combine(AppPaths.DataDir, "jev.key");

    /// <summary>Whether a key file exists (it is not decrypted).</summary>
    public static bool Exists => File.Exists(FilePath);

    /// <summary>The saved key, or null when none is saved or it cannot be read.</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length == 0 ? null : key;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Encrypts and saves the key.</summary>
    public static void Save(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Directory.CreateDirectory(AppPaths.DataDir);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, encrypted);
    }

    /// <summary>Deletes the saved key.</summary>
    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }
}
