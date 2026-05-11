using System.Security.Cryptography;
using System.Text;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public sealed class CredentialProtector(IOptions<ChoboStorageOptions> storageOptions, IOptions<ChoboSecurityOptions> securityOptions)
{
    public string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        using var aes = new AesGcm(GetKey(), 16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce) + "." + Convert.ToBase64String(tag) + "." + Convert.ToBase64String(cipher);
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var parts = value.Split('.');
        using var aes = new AesGcm(GetKey(), 16);
        var nonce = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var cipher = Convert.FromBase64String(parts[2]);
        var plain = new byte[cipher.Length];
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] GetKey()
    {
        var dataDirectory = ChoboPaths.GetDataDirectory(storageOptions.Value.DataDirectory);
        var secrets = Path.Combine(dataDirectory, "secrets");
        Directory.CreateDirectory(secrets);
        var keyPath = Path.Combine(secrets, "chobo-data-key");
        var configured = securityOptions.Value.EncryptionKeyBase64;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Convert.FromBase64String(configured);
        }

        if (!File.Exists(keyPath))
        {
            File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        return Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
    }
}
