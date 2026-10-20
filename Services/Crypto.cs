using System.Security.Cryptography;
using System.Text;

namespace S3Explorer.Services;

/// <summary>Encrypts the secret key with DPAPI for the current Windows user.</summary>
public static class Crypto
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("S3Explorer.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return ""; // profile file was copied to another machine/user
        }
    }
}
