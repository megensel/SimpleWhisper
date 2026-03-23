using System.Security.Cryptography;
using System.Text;

namespace SimpleWhisper.Helpers;

/// <summary>
/// Encrypts and decrypts strings using Windows DPAPI (current-user scope).
/// Used to protect sensitive settings like API keys at rest.
/// </summary>
internal static class ApiKeyProtection
{
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string base64Encrypted)
    {
        if (string.IsNullOrEmpty(base64Encrypted))
            return string.Empty;

        try
        {
            byte[] encrypted = Convert.FromBase64String(base64Encrypted);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            // Decryption failed (e.g., different user or machine). Return empty.
            return string.Empty;
        }
        catch (FormatException)
        {
            // Not valid Base64 — likely a plaintext key from before encryption was added.
            // Return as-is for backward compatibility.
            return base64Encrypted;
        }
    }
}
