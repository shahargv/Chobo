using System.Security.Cryptography;
using ChoboServer.Options;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ChoboServer.Repositories;

public sealed record AesKeyMaterial(Guid KeyId, byte[] KeyBytes);

public enum AesKeyAvailability { Available, Missing, Invalid }

public interface IAesKeyRepository
{
    Task<AesKeyMaterial> CreateNewAsync(CancellationToken cancellationToken = default);
    Task<AesKeyMaterial?> GetKeyByIdAsync(Guid keyId, CancellationToken cancellationToken = default);
    Task<AesKeyMaterial> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default);
    Task<AesKeyAvailability> GetAvailabilityAsync(Guid keyId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, AesKeyAvailability>> GetAvailabilitiesAsync(IEnumerable<Guid> keyIds, CancellationToken cancellationToken = default);
    void Refresh(Guid? keyId = null);
}

public sealed class FileAesKeyRepository(
    IOptions<ChoboStorageOptions> storageOptions,
    IOptions<ChoboSecurityOptions> securityOptions) : IAesKeyRepository, IDisposable
{
    private static readonly TimeSpan CacheValidationInterval = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    private readonly object _watcherLock = new();
    private FileSystemWatcher? _watcher;

    public async Task<AesKeyMaterial> CreateNewAsync(CancellationToken cancellationToken = default)
    {
        EnsureWatcher();
        var keyId = Guid.NewGuid();
        var keyBytes = CreateKeyBytes();
        Directory.CreateDirectory(GetKeyDirectory());
        var path = GetKeyPath(keyId);
        await File.WriteAllTextAsync(path, Convert.ToBase64String(keyBytes), cancellationToken);
        var material = new AesKeyMaterial(keyId, keyBytes);
        _cache[keyId] = new CacheEntry(material, AesKeyAvailability.Available, File.GetLastWriteTimeUtc(path), DateTimeOffset.UtcNow);
        return material;
    }

    public async Task<AesKeyMaterial?> GetKeyByIdAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        EnsureWatcher();
        var path = GetKeyPath(keyId);
        if (_cache.TryGetValue(keyId, out var cached) && DateTimeOffset.UtcNow - cached.CheckedAt < CacheValidationInterval)
        {
            return cached.Material;
        }

        if (!File.Exists(path))
        {
            _cache[keyId] = new CacheEntry(null, AesKeyAvailability.Missing, null, DateTimeOffset.UtcNow);
            return null;
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (cached is not null && cached.Availability == AesKeyAvailability.Invalid && cached.LastWriteUtc == lastWrite)
        {
            _cache[keyId] = cached with { CheckedAt = DateTimeOffset.UtcNow };
            return null;
        }
        if (cached is not null && cached.Availability == AesKeyAvailability.Available && cached.LastWriteUtc == lastWrite)
        {
            _cache[keyId] = cached with { CheckedAt = DateTimeOffset.UtcNow };
            return cached.Material;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            var material = new AesKeyMaterial(keyId, DecodeKeyBytes(text.Trim()));
            _cache[keyId] = new CacheEntry(material, AesKeyAvailability.Available, lastWrite, DateTimeOffset.UtcNow);
            return material;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _cache[keyId] = new CacheEntry(null, AesKeyAvailability.Invalid, lastWrite, DateTimeOffset.UtcNow);
            return null;
        }
    }

    public async Task<AesKeyAvailability> GetAvailabilityAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        _ = await GetKeyByIdAsync(keyId, cancellationToken);
        return _cache.TryGetValue(keyId, out var entry) ? entry.Availability : AesKeyAvailability.Missing;
    }

    public async Task<IReadOnlyDictionary<Guid, AesKeyAvailability>> GetAvailabilitiesAsync(IEnumerable<Guid> keyIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, AesKeyAvailability>();
        foreach (var keyId in keyIds.Distinct())
        {
            result[keyId] = await GetAvailabilityAsync(keyId, cancellationToken);
        }
        return result;
    }

    public void Refresh(Guid? keyId = null)
    {
        if (keyId is { } id)
        {
            _cache.TryRemove(id, out _);
        }
        else
        {
            _cache.Clear();
        }
    }

    public void Dispose()
    {
        lock (_watcherLock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
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

    private void EnsureWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        lock (_watcherLock)
        {
            if (_watcher is not null)
            {
                return;
            }

            var directory = GetKeyDirectory();
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = false
            };
            watcher.Created += OnKeyFileChanged;
            watcher.Changed += OnKeyFileChanged;
            watcher.Deleted += OnKeyFileChanged;
            watcher.Renamed += OnKeyFileRenamed;
            watcher.Error += (_, _) => Refresh();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    private void OnKeyFileChanged(object sender, FileSystemEventArgs args)
    {
        if (Guid.TryParse(Path.GetFileName(args.Name), out var keyId))
        {
            Refresh(keyId);
        }
    }

    private void OnKeyFileRenamed(object sender, RenamedEventArgs args)
    {
        if (Guid.TryParse(Path.GetFileName(args.OldName), out var oldKeyId))
        {
            Refresh(oldKeyId);
        }
        if (Guid.TryParse(Path.GetFileName(args.Name), out var newKeyId))
        {
            Refresh(newKeyId);
        }
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

    private sealed record CacheEntry(AesKeyMaterial? Material, AesKeyAvailability Availability, DateTime? LastWriteUtc, DateTimeOffset CheckedAt);
}
