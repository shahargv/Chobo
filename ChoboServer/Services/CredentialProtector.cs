using System.Security.Cryptography;
using System.Text;
using ChoboServer.Repositories;

namespace ChoboServer.Services;

public sealed record ProtectedSecret(string Ciphertext, Guid KeyId);

public interface ICredentialProtector
{
    Task<ProtectedSecret?> EncryptAsync(string? value, CancellationToken cancellationToken = default);
    Task<string?> DecryptAsync(string? value, Guid? keyId, CancellationToken cancellationToken = default);
}

public sealed class CredentialProtector(IAesKeyRepository keys) : ICredentialProtector
{
    public async Task<ProtectedSecret?> EncryptAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var key = await keys.GetOrCreateCurrentAsync(cancellationToken);
        using var aes = new AesGcm(key.KeyBytes, 16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plain, cipher, tag);
        var ciphertext = Convert.ToBase64String(nonce) + "." + Convert.ToBase64String(tag) + "." + Convert.ToBase64String(cipher);
        return new ProtectedSecret(ciphertext, key.KeyId);
    }

    public async Task<string?> DecryptAsync(string? value, Guid? keyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        if (keyId is null || keyId == Guid.Empty)
        {
            throw new InvalidOperationException("Encrypted credential is missing an AES key id.");
        }

        var parts = value.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Encrypted credential has an invalid format.");
        }

        var key = await keys.GetKeyByIdAsync(keyId.Value, cancellationToken)
            ?? throw new InvalidOperationException($"AES key '{keyId}' was not found.");
        using var aes = new AesGcm(key.KeyBytes, 16);
        var nonce = Convert.FromBase64String(parts[0]);
        var tag = Convert.FromBase64String(parts[1]);
        var cipher = Convert.FromBase64String(parts[2]);
        var plain = new byte[cipher.Length];
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
