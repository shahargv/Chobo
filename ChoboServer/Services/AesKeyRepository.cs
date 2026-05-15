using System.Security.Cryptography;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public sealed record AesKeyMaterial(Guid KeyId, byte[] KeyBytes);

public interface IAesKeyRepository
{
    Task<AesKeyMaterial> CreateNewAsync(CancellationToken cancellationToken = default);
    Task<AesKeyMaterial?> GetKeyByIdAsync(Guid keyId, CancellationToken cancellationToken = default);
    Task<AesKeyMaterial> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed class FileAesKeyRepository(
    IOptions<ChoboStorageOptions> storageOptions,
    IOptions<ChoboSecurityOptions> securityOptions) : IAesKeyRepository
{
    public async Task<AesKeyMaterial> CreateNewAsync(CancellationToken cancellationToken = default)
    {
        var keyId = Guid.NewGuid();
        var keyBytes = CreateKeyBytes();
        Directory.CreateDirectory(GetKeyDirectory());
        var path = GetKeyPath(keyId);
        await File.WriteAllTextAsync(path, Convert.ToBase64String(keyBytes), cancellationToken);
        return new AesKeyMaterial(keyId, keyBytes);
    }

    public async Task<AesKeyMaterial?> GetKeyByIdAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        var path = GetKeyPath(keyId);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return new AesKeyMaterial(keyId, DecodeKeyBytes(text.Trim()));
    }

    public async Task<AesKeyMaterial> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default)
    {
        var directory = GetKeyDirectory();
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (Guid.TryParse(name, out var keyId))
            {
                var key = await GetKeyByIdAsync(keyId, cancellationToken);
                if (key is not null)
                {
                    return key;
                }
            }
        }

        return await CreateNewAsync(cancellationToken);
    }

    private string GetKeyDirectory()
    {
        var dataDirectory = ChoboPaths.GetDataDirectory(storageOptions.Value.DataDirectory);
        return Path.Combine(dataDirectory, "secrets", "aes-keys");
    }

    private string GetKeyPath(Guid keyId) =>
        Path.Combine(GetKeyDirectory(), keyId.ToString());

    private byte[] CreateKeyBytes()
    {
        var configured = securityOptions.Value.EncryptionKeyBase64;
        return string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : DecodeKeyBytes(configured);
    }

    private static byte[] DecodeKeyBytes(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length != 32)
        {
            throw new InvalidOperationException("AES key material must be exactly 32 bytes.");
        }

        return bytes;
    }
}
